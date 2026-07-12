using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace DelayedStartupTool
{
    public partial class App : Application
    {
        private static Mutex? mutex;
        private const string MutexId = "{C8A9E8E0-1B4A-4E4A-8D1E-9F6B3F4E5D7C}";

        private void App_Startup(object sender, StartupEventArgs e)
        {
            // 全局异常兜底：任何未捕获异常都先记录日志，避免程序“静默消失”
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            bool isLaunchMode = e.Args.Length > 0 && e.Args[0].Equals("/launch", StringComparison.OrdinalIgnoreCase);
            bool isHiddenMode = e.Args.Length > 1 && e.Args[1].Equals("/hidden", StringComparison.OrdinalIgnoreCase);

            if (isLaunchMode)
            {
                if (isHiddenMode)
                {
                    // 后台隐藏模式：静默启动，不显示任何界面
                    RunHiddenMode();
                }
                else
                {
                    // /launch 模式不检查互斥体，允许与主窗口同时运行
                    RunSplashForm();
                }
            }
            else
            {
                mutex = new Mutex(true, MutexId, out bool createdNew);
                if (!createdNew)
                {
                    MessageBox.Show("程序已在运行中！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    Environment.Exit(0);
                    return;
                }
                MainWindow = new MainWindow();
                // 先将窗口移出屏幕外（窗口仍为 Visible，WebView2 可正常初始化，
                // 但用户看不到），待 WebView2 导航完成后再“瞬移”到屏幕中央，避免纯色窗口闪现
                MainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                MainWindow.Left = -100000;
                MainWindow.Top = -100000;
                MainWindow.Show();
            }
        }

        // ============================================================
        // 全局异常处理 — 记录日志 + 尽量不让程序崩溃
        // ============================================================
        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogException(e.Exception, "DispatcherUnhandledException");
            try
            {
                // 仅当主窗口已就绪时才弹窗，避免启动阶段无窗口可显示
                if (MainWindow != null && MainWindow.IsLoaded)
                {
                    StyledMessageBox.Show(
                        "发生了一个未处理的错误，但程序会继续运行。\n\n" +
                        "详细信息已记录到日志文件，可向开发者反馈。",
                        "程序遇到问题",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch { /* 弹窗本身失败也不要二次崩溃 */ }
            e.Handled = true; // 标记为已处理，阻止进程终止
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            LogException(e.Exception, "UnobservedTaskException");
            e.SetObserved(); // 防止后台任务异常导致进程崩溃
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            LogException(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
        }

        /// <summary>
        /// 将异常写入 logs/error-YYYYMMDD.txt（追加），失败则降级到临时目录，保证不抛异常。
        /// </summary>
        public static void LogException(Exception? ex, string context)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string logDir = Path.Combine(baseDir, "logs");
                try { Directory.CreateDirectory(logDir); } catch { logDir = Path.GetTempPath(); }

                string logFile = Path.Combine(logDir, $"error-{DateTime.Now:yyyyMMdd}.txt");
                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}]");
                sb.AppendLine($"Message: {ex?.Message}");
                sb.AppendLine($"Type:    {ex?.GetType().FullName}");
                sb.AppendLine($"Stack:    {ex?.StackTrace}");
                if (ex?.InnerException != null)
                {
                    sb.AppendLine($"Inner:   {ex.InnerException.Message}");
                    sb.AppendLine($"InnerStack: {ex.InnerException.StackTrace}");
                }
                sb.AppendLine(new string('-', 60));
                File.AppendAllText(logFile, sb.ToString(), Encoding.UTF8);
            }
            catch { /* 日志本身失败则静默 */ }
        }

        private void RunSplashForm()
        {
            SplashForm splashForm = new SplashForm();
            splashForm.ShowDialog();
            Environment.Exit(0);
        }

        private void RunHiddenMode()
        {
            // 后台隐藏模式：不创建任何窗口，按顺序延迟启动所有程序后退出
            var items = LoadStartupItemsForHidden();
            if (items == null || items.Count == 0)
            {
                Environment.Exit(0);
                return;
            }

            int currentIndex = 0;

            void StartNext()
            {
                if (currentIndex >= items.Count)
                {
                    Environment.Exit(0);
                    return;
                }

                var item = items[currentIndex];
                int delay = item.Delay;

                if (delay > 0)
                {
                    // 等待 delay 秒后再启动该程序
                    var timer = new System.Windows.Threading.DispatcherTimer();
                    timer.Interval = TimeSpan.FromSeconds(delay);
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        LaunchItemHidden(item);
                        currentIndex++;
                        StartNext();
                    };
                    timer.Start();
                }
                else
                {
                    // delay 为 0，立即启动
                    LaunchItemHidden(item);
                    currentIndex++;
                    // 短暂间隔避免同时启动
                    var timer = new System.Windows.Threading.DispatcherTimer();
                    timer.Interval = TimeSpan.FromMilliseconds(500);
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        StartNext();
                    };
                    timer.Start();
                }
            }

            StartNext();
        }

        private List<StartupItemForSplash> LoadStartupItemsForHidden()
        {
            string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(configFilePath))
            {
                try
                {
                    string json = File.ReadAllText(configFilePath, Encoding.UTF8);
                    var options = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                    };
                    var items = System.Text.Json.JsonSerializer.Deserialize<List<StartupItemForSplash>>(json, options) ?? new List<StartupItemForSplash>();
                    return items.FindAll(i => i.Enabled);
                }
                catch { }
            }
            return null;
        }

        private void LaunchItemHidden(StartupItemForSplash item)
        {
            try
            {
                string expandedPath = Environment.ExpandEnvironmentVariables(item.FilePath);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = expandedPath;
                if (!string.IsNullOrEmpty(item.Arguments))
                    psi.Arguments = item.Arguments;
                string directory = Path.GetDirectoryName(expandedPath);
                if (!string.IsNullOrEmpty(directory))
                    psi.WorkingDirectory = directory;
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Normal;
                Process.Start(psi);
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                mutex?.ReleaseMutex();
                mutex?.Dispose();
            }
            catch { }
            base.OnExit(e);
        }
    }

    public class StartupItemForSplash
    {
        public string FilePath { get; set; } = "";
        public int Delay { get; set; } = 0;
        public string Comment { get; set; } = "";
        public string Arguments { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    public class SplashForm : Window
    {
        private Microsoft.Web.WebView2.Wpf.WebView2 webView;
        private System.Windows.Threading.DispatcherTimer countdownTimer;
        private System.Windows.Threading.DispatcherTimer delayTimer;
        private int remainingSeconds;
        private int currentIndex;
        private List<StartupItemForSplash> startupItems;
        private bool isWebViewReady = false;
        private bool isClosing = false;

        public SplashForm()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.WindowStyle = WindowStyle.None;
            this.Topmost = true;
            this.Opacity = 1;
            this.Width = 420;
            this.Height = 84;
            this.ShowInTaskbar = false;
            this.AllowsTransparency = true;
            this.WindowStartupLocation = WindowStartupLocation.Manual;
            this.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            this.Cursor = Cursors.Arrow;
            this.IsHitTestVisible = true;

            webView = new Microsoft.Web.WebView2.Wpf.WebView2();
            webView.Width = 420;
            webView.Height = 84;
            webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            webView.Cursor = Cursors.Arrow;
            webView.IsHitTestVisible = true;
            webView.Visibility = Visibility.Visible;

            this.Content = webView;

            this.Loaded += SplashForm_Load;
            // 关闭时释放 WebView2，避免浏览器进程/句柄残留
            this.Closed += (s, e) => { try { webView?.Dispose(); } catch { } };
        }

        private async void SplashForm_Load(object sender, RoutedEventArgs e)
        {
            this.Left = (System.Windows.SystemParameters.PrimaryScreenWidth - this.Width) / 2;
            this.Top = 15;

            startupItems = LoadStartupItems();
            currentIndex = 0;

            if (startupItems == null || startupItems.Count == 0)
            {
                this.Close();
                return;
            }

            await InitializeWebView();
        }

        private async Task InitializeWebView()
        {
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            
            string uiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "datashui", "startup-ui.html");
            if (File.Exists(uiPath))
            {
                webView.Source = new Uri($"file:///{uiPath.Replace("\\", "/")}");
            }
        }

        private void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!isWebViewReady)
            {
                System.Windows.Threading.DispatcherTimer readyTimer = new System.Windows.Threading.DispatcherTimer();
                readyTimer.Interval = TimeSpan.FromMilliseconds(300);
                readyTimer.Tick += (s, args) =>
                {
                    readyTimer.Stop();
                    if (!isWebViewReady)
                    {
                        StartNextItem();
                    }
                };
                readyTimer.Start();
            }
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();
                if (message == "ready")
                {
                    isWebViewReady = true;
                    StartNextItem();
                }
                else if (message == "close")
                {
                    StopAllTimers();
                    isClosing = true;
                    FadeOutAndClose();
                }
            }
            catch { }
        }

        private void StopAllTimers()
        {
            if (countdownTimer != null)
            {
                countdownTimer.Stop();
                countdownTimer = null;
            }
            if (delayTimer != null)
            {
                delayTimer.Stop();
                delayTimer = null;
            }
        }

        private List<StartupItemForSplash> LoadStartupItems()
        {
            string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

            if (File.Exists(configFilePath))
            {
                try
                {
                    string json = File.ReadAllText(configFilePath, Encoding.UTF8);
                    var options = new System.Text.Json.JsonSerializerOptions 
                    { 
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                    };
                    var items = System.Text.Json.JsonSerializer.Deserialize<List<StartupItemForSplash>>(json, options) ?? new List<StartupItemForSplash>();
                    return items.FindAll(i => i.Enabled);
                }
                catch (Exception ex)
                {
                    return null;
                }
            }
            return null;
        }

        private void StartNextItem()
        {
            if (isClosing) return;

            if (currentIndex >= startupItems.Count)
            {
                FadeOutAndClose();
                return;
            }

            this.Show();

            StartupItemForSplash item = startupItems[currentIndex];
            string displayName = !string.IsNullOrEmpty(item.Comment) ? item.Comment : Path.GetFileName(item.FilePath);
            int total = startupItems.Count;

            SendMessageToWebView(new
            {
                action = "update",
                current = currentIndex + 1,
                total = total,
                fileName = displayName,
                delay = item.Delay,
                maxDelay = item.Delay
            });

            remainingSeconds = item.Delay;

            if (item.Delay > 0)
            {
                countdownTimer = new System.Windows.Threading.DispatcherTimer();
                countdownTimer.Interval = TimeSpan.FromSeconds(1);
                countdownTimer.Tick += CountdownTimer_Tick;
                countdownTimer.Start();
            }
            else
            {
                LaunchItem(item);
                currentIndex++;
                StartNextAfterDelay();
            }
        }

        private void StartNextAfterDelay()
        {
            delayTimer = new System.Windows.Threading.DispatcherTimer();
            delayTimer.Interval = TimeSpan.FromSeconds(1);
            delayTimer.Tick += (s, args) =>
            {
                delayTimer.Stop();
                delayTimer = null;
                StartNextItem();
            };
            delayTimer.Start();
        }

        private void CountdownTimer_Tick(object sender, EventArgs e)
        {
            if (isClosing) return;

            remainingSeconds--;

            if (remainingSeconds > 0)
            {
                SendMessageToWebView(new
                {
                    action = "tick",
                    remaining = remainingSeconds,
                    current = currentIndex + 1,
                    total = startupItems.Count
                });
            }
            else
            {
                countdownTimer.Stop();
                countdownTimer = null;

                LaunchItem(startupItems[currentIndex]);
                currentIndex++;

                StartNextAfterDelay();
            }
        }

        private void SendMessageToWebView(object data)
        {
            try
            {
                var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
                string json = System.Text.Json.JsonSerializer.Serialize(data, options);
                webView.CoreWebView2?.PostWebMessageAsString(json);
            }
            catch { }
        }

        private void LaunchItem(StartupItemForSplash item)
        {
            try
            {
                string expandedPath = Environment.ExpandEnvironmentVariables(item.FilePath);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = expandedPath;

                if (!string.IsNullOrEmpty(item.Arguments))
                {
                    psi.Arguments = item.Arguments;
                }

                string directory = Path.GetDirectoryName(expandedPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    psi.WorkingDirectory = directory;
                }

                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        private void FadeOutAndClose()
        {
            System.Windows.Threading.DispatcherTimer fadeTimer = new System.Windows.Threading.DispatcherTimer();
            fadeTimer.Interval = TimeSpan.FromMilliseconds(50);
            fadeTimer.Tick += (s, args) =>
            {
                if (this.Opacity > 0)
                {
                    this.Opacity -= 0.05;
                }
                else
                {
                    fadeTimer.Stop();
                    this.Close();
                }
            };
            fadeTimer.Start();
        }
    }
}