using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DelayedStartupTool
{
    public partial class MainWindow : Window
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCAPTION = 2;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_DROPFILES = 0x0233;
        private const int WM_COPYGLOBALDATA = 0x0049;
        private const int WM_NCCALCSIZE = 0x0083;
        private const int MSGFLT_ADD = 1;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageW(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr pChangeFilterStruct);

        [DllImport("user32.dll")]
        private static extern bool ChangeWindowMessageFilter(uint msg, uint dwFlag);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

        // ============================================================
        // Win32 / COM 拖放 — 注册自定义 IDropTarget 替换 WebView2 的 OLE 拖放目标
        // ============================================================
        [DllImport("ole32.dll")]
        private static extern int OleInitialize(IntPtr pvReserved);

        [DllImport("ole32.dll")]
        private static extern int RegisterDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IDropTarget pDropTarget);

        [DllImport("ole32.dll")]
        private static extern int RevokeDragDrop(IntPtr hwnd);

        [DllImport("ole32.dll")]
        private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

        [DllImport("shell32.dll")]
        private static extern void DragFinish(IntPtr hDrop);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        // COM IDropTarget 接口
        [ComImport]
        [Guid("00000122-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDropTarget
        {
            [PreserveSig] int DragEnter(IntPtr pDataObj, uint grfKeyState, WinPoint pt, ref uint pdwEffect);
            [PreserveSig] int DragOver(uint grfKeyState, WinPoint pt, ref uint pdwEffect);
            [PreserveSig] int DragLeave();
            [PreserveSig] int Drop(IntPtr pDataObj, uint grfKeyState, WinPoint pt, ref uint pdwEffect);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinPoint { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct FORMATETC
        {
            public short cfFormat;
            public IntPtr ptd;
            public uint dwAspect;
            public int lindex;
            public uint tymed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STGMEDIUM
        {
            public uint tymed;
            public IntPtr unionmember;
            public IntPtr pUnkForRelease;
        }

        [ComImport]
        [Guid("0000010e-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IComDataObject
        {
            [PreserveSig] int GetData(ref FORMATETC pfe, out STGMEDIUM pmedium);
            [PreserveSig] int GetDataHere(ref FORMATETC pfe, ref STGMEDIUM pmedium);
            [PreserveSig] int QueryGetData(ref FORMATETC pfe);
            [PreserveSig] int GetCanonicalFormatEtc(ref FORMATETC pfeIn, out FORMATETC pfeOut);
            [PreserveSig] int SetData(ref FORMATETC pfe, ref STGMEDIUM pmedium, bool fRelease);
            [PreserveSig] int EnumFormatEtc(uint dwDirection, out IntPtr ppenum);
            [PreserveSig] int DAdvise(ref FORMATETC pfe, uint advf, IntPtr pAdvSink, out uint pdwConnection);
            [PreserveSig] int DUnadvise(uint dwConnection);
            [PreserveSig] int EnumDAdvise(out IntPtr ppenumAdvise);
        }

        /// <summary>
        /// 自定义 COM IDropTarget — 拦截文件拖放，提取文件路径
        /// </summary>
        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        [Guid("D5BF8E70-7E8A-4D3C-9B1F-A6E4D83C5F20")]
        private class FileDropTarget : IDropTarget
        {
            private const uint DROPEFFECT_COPY = 1;
            private const short CF_HDROP = 15;

            private readonly MainWindow _owner;
            private bool _isFileDrag;
            private GCHandle _gcHandle; // 防止 GC 回收

            public FileDropTarget(MainWindow owner)
            {
                _owner = owner;
                // 固定对象防止 GC 回收，确保 COM 引用有效
                _gcHandle = GCHandle.Alloc(this);
            }

            ~FileDropTarget()
            {
                if (_gcHandle.IsAllocated) _gcHandle.Free();
            }

            public int DragEnter(IntPtr pDataObj, uint grfKeyState, WinPoint pt, ref uint pdwEffect)
            {
                _isFileDrag = false;
                if (pDataObj != IntPtr.Zero)
                {
                    try
                    {
                        var dataObj = (IComDataObject)Marshal.GetObjectForIUnknown(pDataObj);
                        var fmt = new FORMATETC
                        {
                            cfFormat = CF_HDROP,
                            ptd = IntPtr.Zero,
                            dwAspect = 1,
                            lindex = -1,
                            tymed = 1
                        };
                        if (dataObj.QueryGetData(ref fmt) == 0)
                            _isFileDrag = true;
                    }
                    catch { }
                }
                pdwEffect = DROPEFFECT_COPY;
                _owner.Dispatcher.BeginInvoke(new Action(() => _owner.SendMessage("fileDragEnter", null)));
                return 0;
            }

            public int DragOver(uint grfKeyState, WinPoint pt, ref uint pdwEffect)
            {
                pdwEffect = DROPEFFECT_COPY;
                return 0;
            }

            public int DragLeave()
            {
                if (_isFileDrag)
                {
                    _owner.Dispatcher.BeginInvoke(new Action(() => _owner.SendMessage("fileDragLeave", null)));
                    _isFileDrag = false;
                }
                return 0;
            }

            public int Drop(IntPtr pDataObj, uint grfKeyState, WinPoint pt, ref uint pdwEffect)
            {
                _owner.Dispatcher.BeginInvoke(new Action(() => _owner.SendMessage("fileDragLeave", null)));
                if (_isFileDrag && pDataObj != IntPtr.Zero)
                {
                    try
                    {
                        var dataObj = (IComDataObject)Marshal.GetObjectForIUnknown(pDataObj);
                        var files = ExtractFilePaths(dataObj);
                        if (files.Count > 0)
                            _owner.Dispatcher.BeginInvoke(new Action(() => _owner.AddDroppedFiles(files)));
                    }
                    catch { }
                }
                _isFileDrag = false;
                pdwEffect = DROPEFFECT_COPY;
                return 0;
            }

            private List<string> ExtractFilePaths(IComDataObject dataObj)
            {
                var files = new List<string>();
                var fmt = new FORMATETC { cfFormat = CF_HDROP, ptd = IntPtr.Zero, dwAspect = 1, lindex = -1, tymed = 1 };
                STGMEDIUM medium;
                int hr = dataObj.GetData(ref fmt, out medium);
                if (hr != 0 || medium.tymed != 1) return files;
                IntPtr pDrop = GlobalLock(medium.unionmember);
                if (pDrop == IntPtr.Zero) return files;
                try
                {
                    uint fileCount = DragQueryFile(pDrop, 0xFFFFFFFF, null, 0);
                    for (uint i = 0; i < fileCount; i++)
                    {
                        uint chars = DragQueryFile(pDrop, i, null, 0);
                        if (chars == 0) continue;
                        var sb = new StringBuilder((int)chars + 1);
                        DragQueryFile(pDrop, i, sb, (uint)sb.Capacity);
                        files.Add(sb.ToString());
                    }
                }
                finally
                {
                    GlobalUnlock(medium.unionmember);
                    ReleaseStgMedium(ref medium);
                }
                return files;
            }
        }

        private FileDropTarget? _fileDropTarget;

        private List<StartupItem> startupItems = new List<StartupItem>();
        private List<SystemStartupEntry> systemItems = new List<SystemStartupEntry>();
        private string configFilePath;
        private string settingsFilePath;
        private string scanResultsFilePath;
        private string startupBatPath;
        private string startupShortcutPath;
        private string uiHtmlPath;
        private string optimizationBackupFilePath;
        private Dictionary<string, int> _optimizationBackup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private bool isDarkTheme = false;
        private string startupMode = "gui";
        private string lastScanTime = "";
        private Process? testLaunchProcess = null;

        public MainWindow()
        {
            InitializeComponent();

            // 在构造函数中立即设置 WebView2 背景色（避免初始化前闪白）
            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1e, 0x3a, 0x5f);

            // 确保 OLE 已初始化（拖放依赖 OLE）
            try { OleInitialize(IntPtr.Zero); } catch { }

            configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            scanResultsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scan_results.json");
            optimizationBackupFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "optimization_backup.json");
            startupBatPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DelayedStartup.bat");
            startupShortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DelayedStartup.lnk");
            uiHtmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "datashui", "index.html");

            // 设置窗口标题栏/任务栏图标为 datashui/max.ico
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "datashui", "max.ico");
                if (File.Exists(iconPath))
                    this.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath));
            }
            catch { }

            LoadSettings();

            // Win32 Hook：原生消息层处理拖动、拖放、Win11 圆角
            SourceInitialized += (s, e) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                var source = HwndSource.FromHwnd(handle)!;
                source.AddHook(WndProc);
                // Win11 圆角
                int cornerPref = DWMWCP_ROUND;
                DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

                // ★ 关键修复：程序以管理员权限运行时，Windows UIPI 会阻止
                //   从低权限进程（如资源管理器 Explorer）拖放文件到本程序。
                //   必须调用 ChangeWindowMessageFilterEx 允许 WM_DROPFILES 和 WM_COPYGLOBALDATA 消息
                //   这就是"别人电脑不行"的根因 — 不同 UAC 设置导致权限隔离行为不同
                try
                {
                    ChangeWindowMessageFilterEx(handle, WM_DROPFILES, MSGFLT_ADD, IntPtr.Zero);
                    ChangeWindowMessageFilterEx(handle, WM_COPYGLOBALDATA, MSGFLT_ADD, IntPtr.Zero);
                    // 也对全局消息过滤器添加（某些 Windows 版本需要）
                    ChangeWindowMessageFilter(WM_DROPFILES, MSGFLT_ADD);
                    ChangeWindowMessageFilter(WM_COPYGLOBALDATA, MSGFLT_ADD);
                }
                catch { }

                DragAcceptFiles(handle, true);
            };
        }

        // 标记窗口是否已经显示，避免重复显示
        private bool _isWindowRevealed = false;

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 兜底：若 5 秒内 WebView2 未触发导航完成（异常兜底），
            // 也强制显示窗口，避免程序“看不见”
            var revealFallback = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            revealFallback.Tick += (s, args) =>
            {
                revealFallback.Stop();
                RevealWindow();
            };
            revealFallback.Start();

            await InitializeWebView();
            CheckUACAndPromptAsync();
        }

        /// <summary>
        /// 显示主窗口：WebView2 导航完成（或兜底超时）后调用，
        /// 把窗口从屏幕外“瞬移”到屏幕中央。此时 HTML 已就绪，
        /// 直接呈现正常界面，且窗口一直是 Visible（WebView2 正常初始化），避免纯色窗口闪现。
        /// </summary>
        private void RevealWindow()
        {
            if (_isWindowRevealed) return;
            _isWindowRevealed = true;
            Dispatcher.Invoke(() =>
            {
                // 居中显示（对齐原 CenterScreen 行为）
                double screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
                double screenH = System.Windows.SystemParameters.PrimaryScreenHeight;
                this.Left = (screenW - this.Width) / 2;
                this.Top = (screenH - this.Height) / 2;
                this.Activate();
            });
        }

        /// <summary>
        /// 检查 UAC 状态，如果 EnableLUA != 0 则提示用户关闭 UAC 并重启
        /// UAC 开启时：拖放文件会被 UIPI 拦截、开机启动管理员程序可能失败
        /// </summary>
        private async void CheckUACAndPromptAsync()
        {
            bool uacEnabled = false;
            await Task.Run(() =>
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
                    if (key != null)
                    {
                        int enableLUA = Convert.ToInt32(key.GetValue("EnableLUA", 1));
                        uacEnabled = enableLUA != 0;
                    }
                }
                catch { }
            });

            if (!uacEnabled) return;

            Dispatcher.Invoke(() =>
            {
                var result = StyledMessageBox.Show(
                    "检测到 UAC（用户账户控制）已开启，可能导致以下问题：\n\n" +
                    "1. 拖放文件到本程序时显示禁止图标\n" +
                    "2. 开机自启无法正常启动管理员程序\n\n" +
                    "建议关闭 UAC 以获得最佳体验。\n\n" +
                    "点击“关闭 UAC 并重启”将自动关闭 UAC 并重启电脑\n" +
                    "点击“继续使用”保持当前 UAC 设置（部分功能可能受限）",
                    "UAC 兼容性提示",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    "关闭 UAC 并重启",
                    "继续使用");

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        using var regKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
                        regKey?.SetValue("EnableLUA", 0, Microsoft.Win32.RegistryValueKind.DWord);

                        Process.Start(new ProcessStartInfo("shutdown", "/r /t 3 /c \"UAC 已关闭，3秒后重启电脑...\"")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });

                        Application.Current.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        StyledMessageBox.Show($"修改注册表失败: {ex.Message}\n\n请手动导入以下注册表并重启：\n\n" +
                            "Windows Registry Editor Version 5.00\n\n" +
                            "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System]\n" +
                            "\"EnableLUA\"=dword:00000000",
                            "错误",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
            });
        }

        private async Task InitializeWebView()
        {
            try
            {
                // 如果已经初始化，跳过重复初始化
                if (WebView.CoreWebView2 == null)
                {
                    var env = await CoreWebView2Environment.CreateAsync();
                    await WebView.EnsureCoreWebView2Async(env);
                }
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show(
                    "本程序需要 Microsoft Edge WebView2 运行时。\n\n" +
                    "请安装 WebView2 Runtime 后重试：\n" +
                    "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                    "缺少运行时组件",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Environment.Exit(1);
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化 WebView2 失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(1);
                return;
            }

            // 移除旧的事件处理程序（防止重复绑定）
            WebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
            WebView.CoreWebView2.NewWindowRequested -= CoreWebView2_NewWindowRequested;
            WebView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;

            // 重新绑定事件
            WebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            WebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            WebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

            if (File.Exists(uiHtmlPath))
            {
                // WebView2 的 file:// 协议不支持查询串，不能用 ?v= 做缓存防呆；
                // 改为导航前清理 WebView2 缓存，确保每次启动都加载最新界面。
                try
                {
                    await WebView.CoreWebView2.Profile.ClearBrowsingDataAsync();
                }
                catch { }
                WebView.Source = new Uri("file:///" + uiHtmlPath.Replace('\\', '/'));
            }
            else
            {
                WebView.NavigateToString("");
            }
        }

        /// <summary>
        /// 在 WebView2 子窗口上注册自定义 COM IDropTarget，替换 WebView2 自带的拖放目标。
        /// </summary>
        private void RegisterCustomDropTarget()
        {
            try
            {
                if (_fileDropTarget == null)
                    _fileDropTarget = new FileDropTarget(this);

                var mainWindowHandle = new WindowInteropHelper(this).Handle;
                EnumChildWindows(mainWindowHandle, (childHwnd, _) =>
                {
                    // 给子窗口也添加 UIPI 消息过滤器
                    try
                    {
                        ChangeWindowMessageFilterEx(childHwnd, WM_DROPFILES, MSGFLT_ADD, IntPtr.Zero);
                        ChangeWindowMessageFilterEx(childHwnd, WM_COPYGLOBALDATA, MSGFLT_ADD, IntPtr.Zero);
                    }
                    catch { }

                    RevokeDragDrop(childHwnd);
                    DragAcceptFiles(childHwnd, true);
                    int hr = RegisterDragDrop(childHwnd, _fileDropTarget);
                    if (hr == unchecked((int)0x80040101)) // DRAGDROP_E_ALREADYREGISTERED
                    {
                        RevokeDragDrop(childHwnd);
                        RegisterDragDrop(childHwnd, _fileDropTarget);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                // 显示 HTML 标题栏（由 HTML 渲染标题栏）
                WebView.CoreWebView2.ExecuteScriptAsync(
                    "var tb = document.querySelector('.titlebar'); if(tb) tb.style.display='flex';" +
                    "document.body.style.margin='0'; document.body.style.padding='0';");

                // 安全措施：WM_NCCALCSIZE 修改了窗口框架后，强制触发一次布局刷新
                // 确保内容立即渲染，不需要手动调整窗口大小
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    InvalidateMeasure();
                    UpdateLayout();
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                RegisterCustomDropTarget();

                var keepTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                int tickCount = 0;
                keepTimer.Tick += (s, args) =>
                {
                    RegisterCustomDropTarget();
                    tickCount++;
                    if (tickCount >= 30)
                        keepTimer.Stop();
                };
                keepTimer.Start();

                LoadConfig();
                LoadScanResults();
                LoadOptimizationBackup();
                SendConfigLoaded();
                // 发送已保存的扫描结果（不触发新扫描、不弹提示）
                if (systemItems.Count > 0)
                {
                    SendSystemScanResult(showToast: false);

                    // 如果存在已禁用的 HKLM/系统级启动项，且当前没有管理员权限，提前提示用户
                    if (!IsRunAsAdministrator() && systemItems.Any(s => s.Disabled && RequiresAdminForEntry(s)))
                    {
                        SendToast("检测到已禁用的系统启动项需要管理员权限才能恢复，请关闭程序后右键以管理员身份运行", "error");
                    }
                }
            }

            // WebView2 导航结束（成功或失败）后显示窗口，
            // 此时 HTML 已就绪或至少进程已就绪，避免纯色背景闪现
            RevealWindow();
        }



        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                var message = JsonSerializer.Deserialize<JsonElement>(json);

                if (message.ValueKind == JsonValueKind.String)
                {
                    json = message.GetString() ?? "";
                    message = JsonSerializer.Deserialize<JsonElement>(json);
                }

                if (message.ValueKind != JsonValueKind.Object) return;

                string action = message.TryGetProperty("action", out JsonElement actionElement) ? actionElement.GetString() ?? "" : "";

                Debug.WriteLine($"Received action: {action}");

                switch (action)
                {
                    case "loadConfig":
                        SendConfigLoaded();
                        break;
                    case "addFiles":
                        HandleAddFiles(message);
                        break;
                    case "addFile":
                        HandleAddFile();
                        break;
                    case "addFileData":
                        HandleAddFileData(message);
                        break;
                    case "deleteItem":
                        HandleDeleteItem(message);
                        break;
                    case "moveItem":
                        HandleMoveItem(message);
                        break;
                    case "clearItems":
                        HandleClearItems(message);
                        break;
                    case "updateDelay":
                        HandleUpdateDelay(message);
                        break;
                    case "saveConfig":
                        HandleSaveConfig(message);
                        break;
                    case "scanSystem":
                        HandleScanSystem();
                        break;
                    case "importSystemItem":
                    case "importEntry":
                        HandleImportSystemItem(message);
                        break;
                    case "restoreSystemItem":
                        HandleRestoreSystemItem(message);
                        break;
                    case "getOptimization":
                        HandleGetOptimization();
                        break;
                    case "applyOptimization":
                        HandleApplyOptimization(message);
                        break;
                    case "resetOptimization":
                        HandleResetOptimization();
                        break;
                    case "toggleStartup":
                        HandleToggleStartup(message);
                        break;
                    case "toggleItemEnabled":
                        HandleToggleItemEnabled(message);
                        break;
                    case "testStartup":
                    case "testLaunch":
                        HandleTestStartup();
                        break;
                    case "stopStartup":
                    case "stopLaunch":
                        HandleStopStartup();
                        break;
                    case "toggleTheme":
                        HandleToggleTheme();
                        break;
                    case "minimize":
                        WindowState = WindowState.Minimized;
                        break;
                    case "close":
                        Close();
                        break;
                    case "dragWindow":
                        try
                        {
                            var handle = new WindowInteropHelper(this).Handle;
                            ReleaseCapture();
                            SendMessageW(handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                        }
                        catch { }
                        break;
                    case "setMode":
                        HandleSetMode(message);
                        break;
                    case "editPath":
                        HandleEditPath(message);
                        break;
                    case "editArgs":
                        HandleEditArgs(message);
                        break;
                    case "editComment":
                        HandleEditComment(message);
                        break;
                    case "openDir":
                        HandleOpenDir(message);
                        break;
                    case "createShortcut":
                        HandleCreateShortcut();
                        break;
                    case "createItemShortcut":
                        HandleCreateItemShortcut(message);
                        break;
                    case "launchItem":
                        HandleLaunchItem(message);
                        break;
                    case "jsError":
                        HandleJsError(message);
                        break;
                    case "removeStartup":
                        HandleRemoveStartup();
                        break;
                    case "openStartupDir":
                        HandleOpenStartupDir();
                        break;
                    case "about":
                        HandleAbout();
                        break;
                    case "help":
                        HandleHelp();
                        break;

                }
            }
            catch (Exception ex)
            {
                // 全局兜底：任何消息处理异常都记录日志并提示用户，避免静默失败
                App.LogException(ex, "WebMessageReceived");
                try
                {
                    SendToast($"操作失败：{ex.Message}", "error");
                }
                catch { }
            }
        }

        private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
        }

        // 复用同一实例，避免每次发消息都分配 JsonSerializerOptions（高频调用，显著降低 GC 压力）
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private void SendMessage(string type, object? data = null)
        {
            string json;

            if (data == null)
            {
                json = JsonSerializer.Serialize(new { type }, _jsonOptions);
            }
            else
            {
                string dataJson = JsonSerializer.Serialize(data, _jsonOptions);
                if (dataJson.StartsWith("{"))
                {
                    json = dataJson.Insert(1, $"\"type\":\"{type}\",");
                }
                else
                {
                    json = JsonSerializer.Serialize(new { type, data }, _jsonOptions);
                }
            }
            WebView.CoreWebView2?.PostWebMessageAsString(json);
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(configFilePath))
                {
                    string json = File.ReadAllText(configFilePath, Encoding.UTF8);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    startupItems = JsonSerializer.Deserialize<List<StartupItem>>(json, options) ?? new List<StartupItem>();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Load config error: {ex.Message}");
            }
        }

        private void SaveConfig()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                string json = JsonSerializer.Serialize(startupItems, options);
                File.WriteAllText(configFilePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Save config error: {ex.Message}");
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath, Encoding.UTF8);
                    var settings = JsonSerializer.Deserialize<Settings>(json);
                    if (settings != null)
                    {
                        startupMode = settings.Mode ?? "gui";
                        isDarkTheme = settings.IsDark ?? false;
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new Settings { Mode = startupMode, IsDark = isDarkTheme };
                string json = JsonSerializer.Serialize(settings);
                File.WriteAllText(settingsFilePath, json, Encoding.UTF8);
            }
            catch { }
        }

        private void SaveScanResults()
        {
            try
            {
                var data = new
                {
                    scanTime = lastScanTime,
                    entries = systemItems
                };
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                string json = JsonSerializer.Serialize(data, options);
                File.WriteAllText(scanResultsFilePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Save scan results error: {ex.Message}");
            }
        }

        private void LoadScanResults()
        {
            try
            {
                if (File.Exists(scanResultsFilePath))
                {
                    string json = File.ReadAllText(scanResultsFilePath, Encoding.UTF8);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var data = JsonSerializer.Deserialize<ScanResultsData>(json, options);
                    if (data != null && data.Entries != null && data.Entries.Count > 0)
                    {
                        systemItems = data.Entries;
                        lastScanTime = data.ScanTime ?? "";
                        foreach (var sysItem in systemItems)
                        {
                            sysItem.IsManaged = startupItems.Any(i =>
                                i.FilePath.Equals(sysItem.FilePath, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Load scan results error: {ex.Message}");
            }
        }

        private void SendConfigLoaded()
        {
            var items = startupItems.Select(i => new
            {
                filePath = i.FilePath,
                delay = i.Delay,
                comment = i.Comment,
                arguments = i.Arguments,
                enabled = i.Enabled,
                displayName = ""
            }).ToList();

            var data = new
            {
                items,
                mode = startupMode,
                isInStartup = IsInStartup()
            };

            SendMessage("configLoaded", data);
            SendThemeState();
        }

        private string GetDisplayName(StartupItem item)
        {
            if (!string.IsNullOrEmpty(item.Comment)) return item.Comment;
            return Path.GetFileName(item.FilePath).Replace(".exe", "").Replace(".lnk", "").Replace(".bat", "").Replace(".cmd", "");
        }

        private bool IsSelfStartupEntry(string name, string filePath)
        {
            string exeName = "DelayedStartupTool";
            string exeNameLower = exeName.ToLower();
            string nameLower = (name ?? "").ToLower();
            string pathLower = (filePath ?? "").ToLower();

            if (nameLower.Contains(exeNameLower)) return true;
            if (nameLower.Contains("delayedstartup")) return true;
            if (pathLower.Contains(exeNameLower + ".exe")) return true;
            if (pathLower.Contains("delayedstartup.lnk")) return true;
            if (pathLower.Contains("delayedstartup.bat")) return true;

            return false;
        }

        private const string TaskName = "DelayedStartupTool";

        private bool IsInStartup()
        {
            // 三种模式互斥：命令行模式用启动文件夹的 .bat，图形/后台模式用计划任务；
            // 任意一种存在即视为“已添加到开机启动”。
            return IsTaskSchedulerTaskExists()
                || File.Exists(startupBatPath)
                || File.Exists(startupShortcutPath);
        }

        /// <summary>
        /// 检查 Windows 任务计划程序中是否存在延迟启动任务
        /// </summary>
        private bool IsTaskSchedulerTaskExists()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks")
                {
                    Arguments = $"/query /tn \"{TaskName}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit(5000);
                return proc.ExitCode == 0;
            }
            catch { return false; }
        }

        private void AddToStartup()
        {
            SaveConfig();

            try
            {
                // 互锁：先清理所有旧的启动方式（计划任务 / 快捷方式 / bat 文件），保证三种模式互斥
                if (File.Exists(startupBatPath))
                    try { File.Delete(startupBatPath); } catch { }
                if (File.Exists(startupShortcutPath))
                    try { File.Delete(startupShortcutPath); } catch { }
                DeleteScheduledTask();

                // 命令行模式：仅在启动文件夹生成 .bat（由 bat 自身完成延迟启动），不创建计划任务
                if (startupMode == "bat")
                {
                    string batContent = GenerateBatContent();
                    File.WriteAllText(startupBatPath, batContent, new UTF8Encoding(false));
                    SendStartupToggled(true, "已添加到开机启动（命令行模式）");
                    return;
                }

                // 图形界面 / 后台隐藏模式：仅创建计划任务启动本程序，不生成 bat
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(exePath))
                    exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DelayedStartupTool.exe");

                string arguments = startupMode switch
                {
                    "gui" => "/launch",
                    "hidden" => "/launch /hidden",
                    _ => "/launch"
                };

                // 使用 Windows 任务计划程序创建开机启动任务
                // 关键：RL（Run Level）= HIGHEST — 以管理员权限运行，绕过 UAC
                var psi = new ProcessStartInfo("schtasks")
                {
                    Arguments = $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\" {arguments}\" /sc onlogon /rl HIGHEST /f",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    SendToast("创建计划任务失败", "error");
                    return;
                }
                proc.WaitForExit(10000);
                if (proc.ExitCode != 0)
                {
                    string error = proc.StandardError.ReadToEnd();
                    SendToast($"创建计划任务失败: {error}", "error");
                    return;
                }

                string modeText = startupMode == "gui" ? "图形界面" : "后台隐藏";
                SendStartupToggled(true, $"已添加到开机启动（{modeText}模式）");
            }
            catch (UnauthorizedAccessException)
            {
                SendToast("需要管理员权限才能修改启动项", "error");
            }
            catch (Exception ex)
            {
                SendToast($"添加失败: {ex.Message}", "error");
            }
        }

        private void DeleteScheduledTask()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks")
                {
                    Arguments = $"/delete /tn \"{TaskName}\" /f",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
            }
            catch { }
        }

        private void RemoveFromStartup()
        {
            try
            {
                bool removed = false;

                // 删除任务计划
                if (IsTaskSchedulerTaskExists())
                {
                    DeleteScheduledTask();
                    removed = true;
                }

                // 也删除旧的快捷方式和 bat 文件（兼容旧版本）
                if (File.Exists(startupBatPath))
                {
                    try { File.Delete(startupBatPath); removed = true; } catch { }
                }

                if (File.Exists(startupShortcutPath))
                {
                    try { File.Delete(startupShortcutPath); removed = true; } catch { }
                }

                if (removed)
                {
                    SendStartupToggled(false, "已移除开机启动");
                }
                else
                {
                    SendToast("未找到启动项", "error");
                }
            }
            catch (Exception ex)
            {
                SendToast($"移除失败: {ex.Message}", "error");
            }
        }

        private T? GetMessageValue<T>(JsonElement message, string key)
        {
            if (message.TryGetProperty(key, out JsonElement value))
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return value.Deserialize<T>(options);
            }
            return default;
        }

        private void HandleAddFiles(JsonElement message)
        {
            var paths = GetMessageValue<List<string>>(message, "paths");
            if (paths == null) return;

            foreach (string path in paths)
            {
                if (!startupItems.Any(i => i.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    startupItems.Add(new StartupItem { FilePath = path, Delay = 5, Enabled = true });
                }
            }

            SaveConfig();
            SendConfigLoaded();
            SendToast("已添加", "success");
        }

        private void HandleAddFile()
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog();
            openFileDialog.Filter = "可执行文件 (*.exe)|*.exe|批处理文件 (*.bat)|*.bat|快捷方式 (*.lnk)|*.lnk|所有文件 (*.*)|*.*";
            openFileDialog.Multiselect = true;
            openFileDialog.Title = "选择要延迟启动的程序";

            if (openFileDialog.ShowDialog() == true)
            {
                foreach (string path in openFileDialog.FileNames)
                {
                    if (!startupItems.Any(i => i.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    {
                        startupItems.Add(new StartupItem { FilePath = path, Delay = 5, Enabled = true });
                    }
                }

                SaveConfig();
                SendConfigLoaded();
                SendToast("已添加", "success");
            }
        }

        private void HandleAddFileData(JsonElement message)
        {
            // 已弃用 — 拖放文件现在通过 addDroppedFileNames 只传文件名
            try
            {
                string fileName = GetMessageValue<string>(message, "fileName");
                Debug.WriteLine($"HandleAddFileData: 收到文件数据请求（{fileName}），已弃用");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HandleAddFileData error: {ex.Message}");
            }
        }

        private void HandleDeleteItem(JsonElement message)
        {
            int index = GetMessageValue<int>(message, "index");
            if (index >= 0 && index < startupItems.Count)
            {
                var deletedItem = startupItems[index];
                startupItems.RemoveAt(index);
                SaveConfig();
                SendConfigLoaded();

                foreach (var sysItem in systemItems)
                {
                    sysItem.IsManaged = startupItems.Any(i => i.FilePath.Equals(sysItem.FilePath, StringComparison.OrdinalIgnoreCase));

                    if (sysItem.Disabled &&
                        sysItem.FilePath.Equals(deletedItem.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        RestoreSystemStartup(sysItem);
                    }
                }

                SaveScanResults();
                SendSystemScanResult(showToast: false);

                SendToast("已删除", "success");
            }
        }

        private void HandleMoveItem(JsonElement message)
        {
            int fromIndex = GetMessageValue<int>(message, "fromIndex");
            int toIndex = GetMessageValue<int>(message, "toIndex");

            if (fromIndex >= 0 && fromIndex < startupItems.Count && toIndex >= 0 && toIndex < startupItems.Count && fromIndex != toIndex)
            {
                var item = startupItems[fromIndex];
                startupItems.RemoveAt(fromIndex);
                startupItems.Insert(toIndex, item);
                SaveConfig();
                SendConfigLoaded();
            }
        }

        private void HandleClearItems(JsonElement message)
        {
            startupItems.Clear();
            SaveConfig();
            SendConfigLoaded();

            foreach (var sysItem in systemItems)
            {
                if (sysItem.Disabled)
                {
                    RestoreSystemStartup(sysItem);
                }
                sysItem.IsManaged = false;
            }

            SaveScanResults();
                SendSystemScanResult(showToast: false);

                SendToast("已清空", "success");
        }

        private void HandleUpdateDelay(JsonElement message)
        {
            int index = GetMessageValue<int>(message, "index");
            int delay = GetMessageValue<int>(message, "delay");

            if (index >= 0 && index < startupItems.Count)
            {
                startupItems[index].Delay = delay;
                SaveConfig();
            }
        }

        private void HandleSaveConfig(JsonElement message)
        {
            var items = GetMessageValue<List<StartupItem>>(message, "items");
            if (items != null)
            {
                startupItems = items;
            }
            
            var mode = GetMessageValue<string>(message, "mode");
            if (!string.IsNullOrEmpty(mode))
            {
                startupMode = mode;
                SaveSettings();
            }

            SaveConfig();
            SendConfigLoaded();
            SendToast("配置已保存", "success");
        }

        private void HandleScanSystem()
        {
            var previouslyDisabled = systemItems.Where(s => s.Disabled).ToList();

            systemItems.Clear();
            ScanRegistryRunKeys();
            ScanStartupFolder();
            ScanTaskScheduler();

            foreach (var prev in previouslyDisabled)
            {
                var existing = systemItems.FirstOrDefault(s =>
                    s.Source == prev.Source &&
                    s.Name.Equals(prev.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Disabled = true;
                    existing.OriginalValue = prev.OriginalValue;
                    existing.BackupPath = prev.BackupPath;
                    existing.TaskPath = prev.TaskPath;
                }
                else
                {
                    systemItems.Add(prev);
                }
            }

            foreach (var sysItem in systemItems)
            {
                sysItem.IsManaged = startupItems.Any(i =>
                    i.FilePath.Equals(sysItem.FilePath, StringComparison.OrdinalIgnoreCase));
            }

            lastScanTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SaveScanResults();
            SendSystemScanResult();
        }

        private void SendSystemScanResult(bool showToast = true)
        {
            var entries = systemItems.Select(e => new
            {
                name = e.Name,
                filePath = e.FilePath,
                source = e.Source,
                sourceDisplay = e.SourceDisplay,
                arguments = e.Arguments,
                isManaged = e.IsManaged,
                isSelf = e.IsSelf,
                disabled = e.Disabled
            }).ToList();

            var result = new {
                entries,
                scanTime = lastScanTime,
                showToast = showToast,
                regCount = systemItems.Count(s => s.Source == "registry"),
                folderCount = systemItems.Count(s => s.Source == "folder"),
                taskCount = systemItems.Count(s => s.Source == "task")
            };
            SendMessage("systemScanResult", result);
        }

        private void ScanRegistryRunKeys()
        {
            try
            {
                string[] keys = {
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run",
                    @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Run",
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\RunOnce",
                    @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\RunOnce"
                };

                foreach (string key in keys)
                {
                    try
                    {
                        Microsoft.Win32.RegistryKey rootKey;
                        string subKey;

                        if (key.StartsWith("HKEY_LOCAL_MACHINE\\"))
                        {
                            rootKey = Microsoft.Win32.Registry.LocalMachine;
                            subKey = key.Replace("HKEY_LOCAL_MACHINE\\", "");
                        }
                        else if (key.StartsWith("HKEY_CURRENT_USER\\"))
                        {
                            rootKey = Microsoft.Win32.Registry.CurrentUser;
                            subKey = key.Replace("HKEY_CURRENT_USER\\", "");
                        }
                        else
                        {
                            continue;
                        }

                        using (var regKey = rootKey.OpenSubKey(subKey))
                        {
                            if (regKey != null)
                            {
                                foreach (string name in regKey.GetValueNames())
                                {
                                    string value = regKey.GetValue(name)?.ToString() ?? "";
                            string path;
                            string args = "";

                            if (value.StartsWith("\""))
                            {
                                int endQuote = value.IndexOf("\"", 1);
                                if (endQuote > 0)
                                {
                                    path = value.Substring(1, endQuote - 1);
                                    if (endQuote + 1 < value.Length)
                                    {
                                        args = value.Substring(endQuote + 1).Trim();
                                    }
                                }
                                else
                                {
                                    path = value.Trim('"');
                                }
                            }
                            else
                            {
                                path = FindLongestValidPath(value);
                                if (path.Length < value.Length)
                                {
                                    args = value.Substring(path.Length).Trim();
                                }
                            }

                                    if (!string.IsNullOrEmpty(path))
                                    {
                                        systemItems.Add(new SystemStartupEntry
                                        {
                                            Name = name,
                                            FilePath = path,
                                            Arguments = args,
                                            Source = "registry",
                                            SourceDisplay = key,
                                            IsManaged = startupItems.Any(i => i.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)),
                                            IsSelf = IsSelfStartupEntry(name, path)
                                        });
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void ScanStartupFolder()
        {
            try
            {
                string[] folders = {
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
                };

                Debug.WriteLine($"Scanning startup folders: {string.Join(", ", folders)}");

                foreach (string folder in folders)
                {
                    if (Directory.Exists(folder))
                    {
                        string[] files = Directory.GetFiles(folder);
                        Debug.WriteLine($"Found {files.Length} files in {folder}");

                        foreach (string file in files)
                        {
                            string ext = Path.GetExtension(file).ToLower();
                            if (ext == ".exe" || ext == ".lnk" || ext == ".bat" || ext == ".cmd" || ext == ".vbs" || ext == ".ps1" || ext == ".url" || ext == ".ahk")
                            {
                                string name = Path.GetFileName(file);
                                string targetPath = file;

                                if (ext == ".lnk")
                                {
                                    targetPath = ResolveShortcut(file);
                                }

                                systemItems.Add(new SystemStartupEntry
                                {
                                    Name = name,
                                    FilePath = targetPath,
                                    Source = "folder",
                                    SourceDisplay = folder,
                                    IsManaged = startupItems.Any(i => i.FilePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase)),
                                    IsSelf = IsSelfStartupEntry(name, targetPath)
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ScanStartupFolder error: {ex.Message}");
            }
        }

        private void ScanTaskScheduler()
        {
            string? tempScript = null;
            string? tempOutput = null;
            try
            {
                string tempDir = Path.GetTempPath();
                tempScript = Path.Combine(tempDir, $"scan_tasks_{Guid.NewGuid():N}.ps1");
                tempOutput = Path.Combine(tempDir, $"scan_tasks_{Guid.NewGuid():N}.json");

                // 强制脚本以 UTF-8 写出到文件，避免中文路径在 GBK/CP936 代码页下乱码
                var sb = new StringBuilder();
                sb.AppendLine("$ErrorActionPreference = 'Stop'");
                sb.AppendLine("$OutputEncoding = [System.Text.Encoding]::UTF8");
                sb.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
                sb.AppendLine("$outFile = '" + tempOutput.Replace("'", "''") + "'");
                sb.AppendLine("$tasks = @(Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\\Microsoft*' -and ($_.State -eq 3 -or $_.State -eq 4) -and $_.Actions.Count -gt 0 } | ForEach-Object {");
                sb.AppendLine("  $action = $_.Actions[0]");
                sb.AppendLine("  if ($action -and $action.Execute) {");
                sb.AppendLine("    [PSCustomObject]@{ Name = $_.TaskName; TaskPath = $_.TaskPath; Executable = $action.Execute; Arguments = if ($action.Arguments) { $action.Arguments } else { '' } }");
                sb.AppendLine("  }");
                sb.AppendLine("})");
                sb.AppendLine("$json = $tasks | ConvertTo-Json -Compress -Depth 3");
                sb.AppendLine("Set-Content -Path $outFile -Value $json -Encoding UTF8");
                File.WriteAllText(tempScript, sb.ToString(), Encoding.UTF8);

                using (var process = new Process())
                {
                    process.StartInfo.FileName = "powershell.exe";
                    process.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"";
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                    process.Start();

                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (!string.IsNullOrEmpty(error)) Debug.WriteLine($"[SCAN] PowerShell error: {error}");
                }

                if (File.Exists(tempOutput))
                {
                    string output = File.ReadAllText(tempOutput, Encoding.UTF8);
                    Debug.WriteLine($"[SCAN] PowerShell task output: {output.Length} chars");

                    if (!string.IsNullOrWhiteSpace(output) && output.TrimStart().StartsWith("["))
                    {
                        try
                        {
                            var tasks = JsonSerializer.Deserialize<List<PowerShellTask>>(output);
                            if (tasks != null)
                            {
                                foreach (var task in tasks)
                                {
                                    string cleanName = CleanTaskName(task.Name);
                                    string displayName = cleanName;
                                    if (!string.IsNullOrEmpty(task.TaskPath) && !task.TaskPath.Equals("\\", StringComparison.Ordinal))
                                    {
                                        displayName = task.TaskPath.TrimEnd('\\') + "\\" + cleanName;
                                    }

                                    systemItems.Add(new SystemStartupEntry
                                    {
                                        Name = cleanName,
                                        FilePath = task.Executable,
                                        Arguments = task.Arguments,
                                        Source = "task",
                                        SourceDisplay = "Task Scheduler: " + displayName,
                                        TaskPath = task.TaskPath ?? "",
                                        IsManaged = startupItems.Any(i => i.FilePath.Equals(task.Executable, StringComparison.OrdinalIgnoreCase)),
                                        IsSelf = IsSelfStartupEntry(cleanName, task.Executable)
                                    });
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SCAN] JSON parse error: {ex.Message}");
                        }
                    }
                }

                Debug.WriteLine($"[SCAN] PowerShell tasks: added {systemItems.Count(s => s.Source == "task")} user tasks");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SCAN] ScanTaskScheduler error: {ex.Message}");
            }
            finally
            {
                if (tempScript != null && File.Exists(tempScript))
                {
                    try { File.Delete(tempScript); } catch { }
                }
                if (tempOutput != null && File.Exists(tempOutput))
                {
                    try { File.Delete(tempOutput); } catch { }
                }
            }
        }

        private readonly string[] ExecutableExtensions = {
            ".exe", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", 
            ".wsf", ".wsh", ".ps1", ".msc", ".cpl", ".scr", ".hta"
        };

        private string FindLongestValidPath(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            string remaining = value.Trim();
            
            if (remaining.StartsWith("\""))
            {
                int endQuote = remaining.IndexOf("\"", 1);
                if (endQuote > 0)
                {
                    return remaining.Substring(1, endQuote - 1);
                }
                return remaining.Trim('"');
            }

            string bestPath = "";

            foreach (string ext in ExecutableExtensions)
            {
                int searchStart = 0;
                while (true)
                {
                    int extIndex = remaining.IndexOf(ext, searchStart, StringComparison.OrdinalIgnoreCase);
                    if (extIndex < 0) break;

                    int extEnd = extIndex + ext.Length;
                    string candidate = remaining.Substring(0, extEnd).Trim();

                    if (File.Exists(candidate))
                    {
                        if (candidate.Length > bestPath.Length)
                        {
                            bestPath = candidate;
                        }
                    }

                    searchStart = extEnd;
                }
            }

            if (!string.IsNullOrEmpty(bestPath))
            {
                return bestPath;
            }

            for (int i = remaining.Length - 1; i > 0; i--)
            {
                if (remaining[i] == ' ')
                {
                    string candidate = remaining.Substring(0, i).Trim();
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            int firstSpace = remaining.IndexOf(' ');
            if (firstSpace > 0)
            {
                return remaining.Substring(0, firstSpace);
            }

            return remaining;
        }

        private string CleanTaskName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            string result = name;

            result = System.Text.RegularExpressions.Regex.Replace(result, @"_\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}", "");

            result = System.Text.RegularExpressions.Regex.Replace(result, @"_[0-9A-Fa-f]{16,32}$", "");

            result = result.Trim('_');

            return result;
        }

        private class PowerShellTask
        {
            public string Name { get; set; } = "";
            public string TaskPath { get; set; } = "";
            public string Executable { get; set; } = "";
            public string Arguments { get; set; } = "";
        }

        private List<string> ParseCsvLine(string line)
        {
            List<string> result = new List<string>();
            StringBuilder current = new StringBuilder();
            bool inQuotes = false;

            foreach (char c in line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            result.Add(current.ToString());
            return result;
        }

        private string ResolveShortcut(string lnkPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                return shortcut.TargetPath?.Trim('"') ?? lnkPath;
            }
            catch
            {
                return lnkPath;
            }
        }

        private void HandleImportSystemItem(JsonElement message)
        {
            string name = GetMessageValue<string>(message, "name");
            var item = systemItems.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                if (item.IsSelf)
                {
                    SendToast("自身启动项不能导入", "error");
                    return;
                }

                if (!startupItems.Any(i => i.FilePath.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    startupItems.Add(new StartupItem
                    {
                        FilePath = item.FilePath,
                        Delay = 5,
                        Comment = item.Name,
                        Arguments = item.Arguments,
                        Enabled = true
                    });

                    SaveConfig();
                    SendConfigLoaded();

                    if (!item.Disabled)
                    {
                        DisableSystemStartup(item);
                    }

                    foreach (var sysItem in systemItems)
                    {
                        sysItem.IsManaged = startupItems.Any(i => i.FilePath.Equals(sysItem.FilePath, StringComparison.OrdinalIgnoreCase));
                    }

                    SaveScanResults();
                    SendSystemScanResult(showToast: false);

                    SendToast("已导入并禁用系统启动", "success");
                }
            }
        }

        private (Microsoft.Win32.RegistryKey? root, string subKey) ParseRegistryPath(string path)
        {
            if (path.StartsWith("HKEY_LOCAL_MACHINE\\"))
            {
                return (Microsoft.Win32.Registry.LocalMachine, path.Replace("HKEY_LOCAL_MACHINE\\", ""));
            }
            else if (path.StartsWith("HKEY_CURRENT_USER\\"))
            {
                return (Microsoft.Win32.Registry.CurrentUser, path.Replace("HKEY_CURRENT_USER\\", ""));
            }
            return (null, path);
        }

        private bool IsRunAsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private bool RequiresAdminForEntry(SystemStartupEntry entry)
        {
            if (entry.Source == "registry")
            {
                var (rootKey, _) = ParseRegistryPath(entry.SourceDisplay);
                return rootKey == Microsoft.Win32.Registry.LocalMachine;
            }
            else if (entry.Source == "folder")
            {
                string commonStartup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                return entry.SourceDisplay.Equals(commonStartup, StringComparison.OrdinalIgnoreCase);
            }
            else if (entry.Source == "task")
            {
                return true;
            }
            return false;
        }

        private bool DisableSystemStartup(SystemStartupEntry entry)
        {
            try
            {
                if (RequiresAdminForEntry(entry) && !IsRunAsAdministrator())
                {
                    SendToast("当前未以管理员权限运行，无法修改此系统启动项。请关闭程序后右键以管理员身份运行。", "error");
                    return false;
                }

                if (entry.Source == "registry")
                {
                    var (rootKey, subKey) = ParseRegistryPath(entry.SourceDisplay);
                    if (rootKey == null) return false;

                    using (var regKey = rootKey.OpenSubKey(subKey, true))
                    {
                        if (regKey == null) return false;
                        entry.OriginalValue = regKey.GetValue(entry.Name)?.ToString() ?? "";
                        regKey.DeleteValue(entry.Name, false);
                    }
                }
                else if (entry.Source == "folder")
                {
                    string filePath = Path.Combine(entry.SourceDisplay, entry.Name);
                    if (File.Exists(filePath))
                    {
                        string backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backup");
                        Directory.CreateDirectory(backupDir);
                        string backupFile = Path.Combine(backupDir,
                            $"{entry.Name}_{DateTime.Now:yyyyMMddHHmmss}.bak");
                        File.Move(filePath, backupFile);
                        entry.BackupPath = backupFile;
                    }
                    else return false;
                }
                else if (entry.Source == "task")
                {
                    string taskFullName = string.IsNullOrEmpty(entry.TaskPath) || entry.TaskPath == "\\"
                        ? entry.Name
                        : entry.TaskPath.TrimEnd('\\') + "\\" + entry.Name;

                    var psi = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Change /TN \"{taskFullName}\" /Disable",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var proc = Process.Start(psi))
                    {
                        proc?.WaitForExit();
                    }
                }

                entry.Disabled = true;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                SendToast("需要管理员权限才能修改此系统启动项", "error");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DisableSystemStartup error: {ex.Message}");
                SendToast($"禁用失败: {ex.Message}", "error");
                return false;
            }
        }

        private bool RestoreSystemStartup(SystemStartupEntry entry)
        {
            try
            {
                if (RequiresAdminForEntry(entry) && !IsRunAsAdministrator())
                {
                    SendToast("当前未以管理员权限运行，无法恢复此系统启动项。请关闭程序后右键以管理员身份运行。", "error");
                    return false;
                }

                if (entry.Source == "registry")
                {
                    var (rootKey, subKey) = ParseRegistryPath(entry.SourceDisplay);
                    if (rootKey == null) return false;

                    using (var regKey = rootKey.OpenSubKey(subKey, true))
                    {
                        if (regKey != null && !string.IsNullOrEmpty(entry.OriginalValue))
                        {
                            regKey.SetValue(entry.Name, entry.OriginalValue);
                        }
                    }
                }
                else if (entry.Source == "folder")
                {
                    if (!string.IsNullOrEmpty(entry.BackupPath) && File.Exists(entry.BackupPath))
                    {
                        string targetPath = Path.Combine(entry.SourceDisplay, entry.Name);
                        if (File.Exists(targetPath))
                        {
                            File.Delete(entry.BackupPath);
                        }
                        else
                        {
                            File.Move(entry.BackupPath, targetPath);
                        }
                    }
                }
                else if (entry.Source == "task")
                {
                    string taskFullName = string.IsNullOrEmpty(entry.TaskPath) || entry.TaskPath == "\\"
                        ? entry.Name
                        : entry.TaskPath.TrimEnd('\\') + "\\" + entry.Name;

                    var psi = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Change /TN \"{taskFullName}\" /Enable",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var proc = Process.Start(psi))
                    {
                        proc?.WaitForExit();
                    }
                }

                entry.Disabled = false;
                entry.OriginalValue = null;
                entry.BackupPath = null;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                SendToast("需要管理员权限才能恢复此系统启动项", "error");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RestoreSystemStartup error: {ex.Message}");
                SendToast($"恢复失败: {ex.Message}", "error");
                return false;
            }
        }

        private void HandleRestoreSystemItem(JsonElement message)
        {
            string name = GetMessageValue<string>(message, "name");
            var item = systemItems.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (item != null && item.Disabled)
            {
                bool restored = RestoreSystemStartup(item);
                if (!restored) return;

                var startupItem = startupItems.FirstOrDefault(i =>
                    i.FilePath.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase));
                if (startupItem != null)
                {
                    startupItems.Remove(startupItem);
                    SaveConfig();
                    SendConfigLoaded();
                }

                foreach (var sysItem in systemItems)
                {
                    sysItem.IsManaged = startupItems.Any(i =>
                        i.FilePath.Equals(sysItem.FilePath, StringComparison.OrdinalIgnoreCase));
                }

                SaveScanResults();
                    SendSystemScanResult(showToast: false);
                SendToast("已恢复系统启动", "success");
            }
        }

        private void HandleGetOptimization()
        {
            var suggestions = new List<object>();
            int currentTotalDelay = startupItems.Sum(i => i.Delay);
            int optimizedTotalDelay = 0;

            foreach (var item in startupItems)
            {
                int suggestedDelay = GetSuggestedDelay(item);
                optimizedTotalDelay += suggestedDelay;
                string displayName = GetDisplayName(item);

                suggestions.Add(new
                {
                    Category = GetCategory(item),
                    OldDelay = item.Delay,
                    NewDelay = suggestedDelay,
                    Changed = suggestedDelay != item.Delay,
                    Reason = GetOptimizationReason(item),
                    Item = new { displayName = displayName, filePath = item.FilePath }
                });
            }

            int savedSeconds = currentTotalDelay - optimizedTotalDelay;
            // 修复评分BUG：saved为正表示优化后节省了时间，分数应该更高
            // 评分逻辑：基础100分，每节省1秒扣1分；如果优化后反而更慢（saved为负），每多1秒扣2分
            int score;
            if (savedSeconds >= 0)
            {
                // 优化后总延迟更短或相同 — 越省时间分数越高
                score = Math.Min(100, Math.Max(0, 100 - (int)Math.Sqrt(savedSeconds) * 3));
            }
            else
            {
                // 优化后总延迟更长 — 扣分更多
                score = Math.Min(100, Math.Max(0, 100 + savedSeconds * 2));
            }

            var result = new
            {
                score,
                oldTotal = currentTotalDelay,
                newTotal = optimizedTotalDelay,
                saved = savedSeconds,
                suggestions
            };

            SendMessage("optimizationResult", result);
        }

        private int GetSuggestedDelay(StartupItem item) => ClassifyItem(item).delay;
        private string GetCategory(StartupItem item) => ClassifyItem(item).category;
        private string GetOptimizationReason(StartupItem item) => ClassifyItem(item).reason;

        // ============================================================
        // 一键优化算法 v2 —— 按「软件类型 + 特定软件」分类
        // 优先级：1) 系统核心目录  2) 品牌/软件签名（特定软件）  3) 文件后缀兜底
        // 延迟档位：0=系统关键 / 2=系统辅助 / 3=网络 / 5=通讯 / 8=浏览器办公 / 10=工具 / 15=大型软件
        // ============================================================
        private static readonly (string category, int delay, string reason, string[] keywords)[] BrandRules = new[]
        {
            // 浏览器 / 办公（放最前，避免被「360/安全」等泛关键字误伤）
            ("office", 8, "浏览器/办公软件可延迟启动", new[] { "chrome","edge","firefox","opera","brave","vivaldi","safari","浏览器","browser","360se","360浏览器","360安全浏览器","搜狗浏览器","2345浏览器","百分浏览器","qq浏览器","excel","word","powerpoint","outlook","wps","office","onenote","access","project","visio","mindmanager","xmind","幕布","印象笔记","evernote","notion","语雀" }),
            // 压缩 / 解压
            ("tool", 10, "压缩/解压工具可延迟启动", new[] { "winrar","7-zip","7zip","7z","bandizip","peazip","winzip","好压","2345好压","360压缩","压缩" }),
            // 网络工具
            ("network", 3, "网络工具需尽早启动", new[] { "clash","vpn","proxy","v2ray","v2rayn","ssr","shadowsocks","trojan","wireguard","openvpn","netch","nps","frp","natfrp","ngrok","花生壳","zerotier","tailscale","tunnel","lanthing","proxifier","network" }),
            // 通讯社交
            ("comm", 5, "通讯社交软件无需过早启动", new[] { "wechat","微信","qq","tim","qqnt","dingtalk","钉钉","telegram","飞书","feishu","lark","slack","discord","skype","yy语音","yy","企业微信","wework","网易邮箱","foxmail","thunderbird","邮件","mail","whatsapp","line","kakaotalk" }),
            // 影音播放器
            ("tool", 10, "影音播放器可延迟启动", new[] { "potplayer","vlc","mpv","kmplayer","qq影音","iqiyi","爱奇艺","腾讯视频","优酷","bilibili","哔哩哔哩","foobar","网易云音乐","qq音乐","spotify","netease","kugou","酷狗","kuwo","酷我","music","player" }),
            // 下载 / 网盘
            ("tool", 10, "下载/网盘工具可延迟启动", new[] { "idm","fdm","download","下载","thunder","迅雷","aria2","qbittorrent","utorrent","比特彗星","百度网盘","baidunetdisk","阿里云盘","aliyun","terabox","115" }),
            // 编辑器 / 开发小工具
            ("tool", 10, "编辑器/开发小工具可延迟启动", new[] { "notepad","sublime","vscode","code","editor","notepad++","notepad--","typora","obsidian","vim","emacs","cursor","trae" }),
            // 截图 / 效率工具
            ("tool", 10, "截图/效率工具可延迟启动", new[] { "everything","listary","wox","utools","quicker","snipaste","capture","fscapture","fsrecorder","截图","hibit","localsend","geek","trafficmonitor","wallpaper","translator","有道","金山词霸","ditto","clipy" }),
            // 远程控制
            ("tool", 10, "远程控制工具可延迟启动", new[] { "向日葵","todesk","anydesk","teamviewer","parsec","rustdesk","remote","远程" }),
            // 大型软件 —— 设计 / 影音制作
            ("heavy", 15, "大型设计/影音制作软件启动较慢", new[] { "autocad","cad","corel","coreldraw","photoshop","illustrator","premiere","after effects","lightroom","indesign","dreamweaver","sketch","figma","blender","maya","3ds max","3dsmax","cinema4d","c4d","houdini","zbrush","达芬奇","davinci" }),
            // 大型软件 —— 开发工具 / IDE
            ("heavy", 15, "开发工具/IDE 启动较慢", new[] { "visual studio","eclipse","android studio","intellij","idea","pycharm","webstorm","rider","clion","goland","phpstorm","netbeans","devcpp","codeblocks","vs code" }),
            // 大型软件 —— 虚拟机 / 容器
            ("heavy", 15, "虚拟机/容器启动较慢", new[] { "docker","vmware","virtualbox","vagrant","hyperv","wsl","parallels","模拟器","emulator","雷电","ldplayer","mumu","夜神","nox" }),
            // 大型软件 —— 游戏平台
            ("heavy", 15, "游戏平台启动较慢", new[] { "steam","epic","epicgames","wegame","origin","uplay","battlenet","battle.net","ea app","eaapp","gog","rockstar","riot","英雄联盟","lol","腾讯游戏","xbox" }),
            // 大型软件 —— 科学计算 / 数据
            ("heavy", 15, "科学计算/数据工具启动较慢", new[] { "matlab","anaconda","jupyter","spss","stata","minitab","tableau","powerbi" }),
            // 系统安全软件（必须立即启动）
            ("system", 0, "系统安全软件需立即启动", new[] { "360安全","360杀毒","360卫士","360safe","360sd","安全","杀毒","防毒","firewall","defender","guard","火绒","kaspersky","avast","avg","bitdefender","mcafee","norton","eset","卡巴斯基","瑞星","毒霸","电脑管家","管家" }),
            // 驱动 / 硬件服务
            ("system", 0, "驱动/硬件服务需立即启动", new[] { "driver","驱动","realtek","rthdvor","nvidia","nvcontainer","amd","radeon","intel","bluetooth","btvstack","hotkey","hkcmd","igfx","touchpad","pointstick","lenovo","dolby","audio","sound","synaptics","elan","logitech","dell","asus","hp","msi","razer","thunderbolt","camera","指纹","fingerprint" }),
            // 系统 / 更新服务
            ("system", 0, "系统/更新服务需立即启动", new[] { "windows","microsoft","system","sysmain","svchost","update","更新","onedrive","geforce experience","nvidia geforce","armoury","armourycrate","adobe genuine","adobe gpu","rundll32" }),
            // 系统辅助（输入法 / 托盘等，尽快启动）
            ("system", 2, "输入法/系统辅助需尽快启动", new[] { "输入法","ime","sogou","搜狗输入法","搜狗拼音","百度输入法","百度拼音","qq输入法","qq拼音","微软拼音","wubi","五笔","ctfmon","tray","托盘","notify","通知","时钟","clock","volume","音量","powertoys","quickstep" }),
        };

        private (string category, int delay, string reason) ClassifyItem(StartupItem item)
        {
            string rawPath = item.FilePath ?? "";
            string resolved = ResolveEffectivePath(rawPath);
            string name = Path.GetFileName(resolved).ToLower();
            string dir = (Path.GetDirectoryName(resolved) ?? "").ToLower();
            string hay = name + " " + dir + " " + rawPath.ToLower();

            // 1) 系统核心目录（如 System32 / WindowsApps）一律立即启动
            if (dir.Contains("system32") || dir.Contains("windowsapps") ||
                dir.Contains("programdata\\microsoft") ||
                dir.Contains("program files\\windowsapps") ||
                dir.Contains("program files (x86)\\windowsapps"))
                return ("system", 0, "系统核心目录组件需立即启动");

            // 2) 品牌 / 软件签名（特定软件），按规则优先级匹配
            foreach (var rule in BrandRules)
            {
                foreach (var kw in rule.keywords)
                {
                    if (hay.Contains(kw) && IsWordMatch(hay, kw))
                        return (rule.category, rule.delay, rule.reason);
                }
            }

            // 3) 文件后缀兜底
            string ext = Path.GetExtension(resolved).ToLower();
            switch (ext)
            {
                case ".bat":
                case ".cmd":
                case ".ps1":
                case ".vbs":
                case ".js":
                case ".ahk":
                    return ("tool", 5, "脚本/批处理启动器可稍后启动");
                case ".cpl":
                case ".msc":
                case ".scr":
                    return ("system", 0, "系统控制组件需立即启动");
                case ".lnk":
                    return ("tool", 10, "快捷方式指向的程序可延迟启动");
                default:
                    return ("tool", 10, "常规应用程序可延迟启动");
            }
        }

        // 简单词边界匹配：仅当关键字前后不是 ASCII 字母/数字时才算命中，
        // 避免 "qq" 误命中 "sql"、"ad" 误命中 "adobe" 等问题（中文按边界处理）。
        private static bool IsWordMatch(string hay, string keyword)
        {
            int idx = hay.IndexOf(keyword, StringComparison.Ordinal);
            while (idx >= 0)
            {
                bool startOk = idx == 0 || !IsWordChar(hay[idx - 1]);
                int end = idx + keyword.Length;
                bool endOk = end >= hay.Length || !IsWordChar(hay[end]);
                if (startOk && endOk) return true;
                idx = hay.IndexOf(keyword, idx + 1, StringComparison.Ordinal);
            }
            return false;
        }

        private static bool IsWordChar(char c) =>
            (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

        // 取真正要启动的可执行文件路径：去掉引号/参数，必要时解析 .lnk
        private string ResolveEffectivePath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw ?? "";
            string s = raw.Trim().Trim('"');

            if (File.Exists(s)) return s;

            int space = s.IndexOf(' ');
            if (space > 0)
            {
                string first = s.Substring(0, space);
                if (File.Exists(first)) return first;
            }

            if (s.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string target = ResolveShortcut(s);
                if (!string.IsNullOrEmpty(target) && target != s)
                    return ResolveEffectivePath(target);
            }
            return s;
        }

        private void HandleApplyOptimization(JsonElement message)
        {
            // 记录「一键优化前」的延迟，供「恢复默认」还原
            foreach (var item in startupItems)
            {
                string key = item.FilePath ?? "";
                if (!string.IsNullOrEmpty(key) && !_optimizationBackup.ContainsKey(key))
                {
                    _optimizationBackup[key] = item.Delay;
                }
            }
            SaveOptimizationBackup();

            foreach (var item in startupItems)
            {
                item.Delay = GetSuggestedDelay(item);
            }

            SaveConfig();
            SendConfigLoaded();
            HandleGetOptimization();
            SendToast("优化已应用", "success");
        }

        private void HandleResetOptimization()
        {
            bool hasBackup = _optimizationBackup.Count > 0;
            foreach (var item in startupItems)
            {
                string key = item.FilePath ?? "";
                if (hasBackup && !string.IsNullOrEmpty(key) && _optimizationBackup.TryGetValue(key, out int original))
                {
                    item.Delay = original;
                }
                else
                {
                    item.Delay = 0;
                }
            }

            SaveConfig();
            SendConfigLoaded();
            HandleGetOptimization();
            SendToast(hasBackup ? "已恢复默认（优化前延迟）" : "已恢复默认（全部归零）", "success");
        }

        private void LoadOptimizationBackup()
        {
            try
            {
                if (File.Exists(optimizationBackupFilePath))
                {
                    string json = File.ReadAllText(optimizationBackupFilePath, Encoding.UTF8);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    if (dict != null)
                    {
                        _optimizationBackup = new Dictionary<string, int>(dict, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Load optimization backup error: {ex.Message}");
            }
        }

        private void SaveOptimizationBackup()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_optimizationBackup, options);
                File.WriteAllText(optimizationBackupFilePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Save optimization backup error: {ex.Message}");
            }
        }

        private void HandleToggleStartup(JsonElement message)
        {
            // 防御：列表为空时不允许设置开机启动，避免开机启动一个空配置
            if (startupItems == null || startupItems.Count == 0)
            {
                SendToast("请先添加启动项，再设置开机启动", "error");
                return;
            }

            bool isInStartup = IsInStartup();

            if (!isInStartup)
            {
                AddToStartup();
            }
            else
            {
                RemoveFromStartup();
            }
        }

        private void HandleToggleItemEnabled(JsonElement message)
        {
            int index = GetMessageValue<int>(message, "index");
            bool enabled = GetMessageValue<bool>(message, "enabled");
            if (index >= 0 && index < startupItems.Count)
            {
                startupItems[index].Enabled = enabled;
                SaveConfig();
                SendConfigLoaded();
            }
        }

        private string GenerateBatContent()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul 2>&1");
            sb.AppendLine("title 延迟启动工具 - Delayed Startup Tool Pro");
            sb.AppendLine("color 0F");
            sb.AppendLine("mode con: cols=60 lines=10");

            foreach (var item in startupItems.Where(i => i.Enabled))
            {
                string expandedPath = Environment.ExpandEnvironmentVariables(item.FilePath);
                string directory = Path.GetDirectoryName(expandedPath) ?? "";
                string displayName = GetDisplayName(item);

                sb.AppendLine($":: 准备启动: {displayName}");
                sb.AppendLine($"echo 正在准备启动: {displayName}");

                if (item.Delay > 0)
                {
                    sb.AppendLine($"echo 将在 {item.Delay} 秒后启动...");
                    sb.AppendLine($"for /l %%t in ({item.Delay},-1,1) do (");
                    sb.AppendLine("    echo 剩余时间: %%t 秒...");
                    sb.AppendLine("    timeout /t 1 >nul");
                    sb.AppendLine(")");
                }

                if (!string.IsNullOrEmpty(directory))
                {
                    sb.AppendLine($"cd /d \"{directory}\"");
                }

                if (expandedPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"call \"{expandedPath}\"");
                }
                else
                {
                    string args = string.IsNullOrEmpty(item.Arguments) ? "" : " " + item.Arguments;
                    sb.AppendLine($"start \"\" \"{expandedPath}\"{args}");
                }
            }

            sb.AppendLine("echo 所有任务已完成。");
            sb.AppendLine("timeout /t 1 >nul");
            sb.AppendLine("exit");

            return sb.ToString();
        }

        private void HandleTestStartup()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(exePath))
                {
                    SendToast("测试失败: 无法获取程序路径", "error");
                    return;
                }

                if (startupMode == "bat")
                {
                    string batContent = GenerateBatContent();
                    string testBatPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_startup.bat");
                    File.WriteAllText(testBatPath, batContent, new UTF8Encoding(false));
                    ProcessStartInfo psi = new ProcessStartInfo(testBatPath);
                    psi.UseShellExecute = true;
                    psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    testLaunchProcess = Process.Start(psi);
                    SendToast("测试启动已开始（命令行模式）", "success");
                }
                else
                {
                    string args = startupMode == "hidden" ? "/launch /hidden" : "/launch";
                    ProcessStartInfo psi = new ProcessStartInfo(exePath, args);
                    psi.UseShellExecute = true;
                    psi.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    testLaunchProcess = Process.Start(psi);
                    string modeText = startupMode == "gui" ? "图形界面" : "后台隐藏";
                    SendToast($"测试启动已开始（{modeText}模式）", "success");
                }
            }
            catch (Exception ex)
            {
                SendToast($"测试失败: {ex.Message}", "error");
            }
        }

        private void HandleStopStartup()
        {
            try
            {
                bool stopped = false;

                if (testLaunchProcess != null && !testLaunchProcess.HasExited)
                {
                    testLaunchProcess.Kill();
                    testLaunchProcess = null;
                    stopped = true;
                }

                foreach (var p in Process.GetProcessesByName("DelayedStartupTool"))
                {
                    try
                    {
                        if (p.Id != Process.GetCurrentProcess().Id)
                        {
                            p.Kill();
                            stopped = true;
                        }
                    }
                    catch { }
                }

                if (stopped)
                {
                    SendToast("已停止启动", "success");
                }
                else
                {
                    SendToast("没有正在运行的启动进程", "info");
                }
            }
            catch (Exception ex)
            {
                SendToast($"停止失败: {ex.Message}", "error");
            }
        }

        // ============================================================
        // WPF 层文件拖放 — 直接获取文件路径，无需读取文件内容
        // ============================================================
        private bool IsFileDrop(DragEventArgs e)
        {
            return e.Data.GetDataPresent(DataFormats.FileDrop);
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (IsFileDrop(e))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                SendMessage("fileDragEnter", null);
            }
            else
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = IsFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            SendMessage("fileDragLeave", null);
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            SendMessage("fileDragLeave", null);

            if (!IsFileDrop(e)) return;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            AddDroppedFiles(files.ToList());
        }

        /// <summary>
        /// 处理拖放文件路径列表 — 只记录路径，不读取文件内容
        /// </summary>
        private void AddDroppedFiles(List<string> files)
        {
            int added = 0;
            foreach (string path in files)
            {
                if (!startupItems.Any(i => i.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    startupItems.Add(new StartupItem { FilePath = path, Delay = 5, Enabled = true });
                    added++;
                }
            }
            if (added > 0)
            {
                SaveConfig();
                SendConfigLoaded();
                SendToast($"已添加 {added} 个文件", "success");
            }
        }

        private void HandleToggleTheme()
        {
            isDarkTheme = !isDarkTheme;
            SaveSettings();
            SendThemeState();
        }

        private void SendThemeState()
        {
            SendMessage("themeChanged", new { isDark = isDarkTheme });
        }

        private void HandleSetMode(JsonElement message)
        {
            string mode = GetMessageValue<string>(message, "mode");
            if (string.IsNullOrEmpty(mode)) return;

            string oldMode = startupMode;
            startupMode = mode;
            SaveSettings();

            // 互锁：如果已添加到开机启动，切换模式时清理其余方式，只保留当前模式对应的唯一启动方式
            // 列表为空时不应创建任何启动项（避免开机启动空配置）
            if (IsInStartup() && startupItems.Count > 0)
            {
                try
                {
                    // 清理所有旧的启动方式（计划任务 / 快捷方式 / bat 文件）
                    if (File.Exists(startupBatPath))
                        try { File.Delete(startupBatPath); } catch { }
                    if (File.Exists(startupShortcutPath))
                        try { File.Delete(startupShortcutPath); } catch { }
                    DeleteScheduledTask();

                    string modeText = mode == "gui" ? "图形界面" : (mode == "bat" ? "命令行" : "后台隐藏");

                    if (mode == "bat")
                    {
                        // 命令行模式：仅生成 bat，不建计划任务
                        string batContent = GenerateBatContent();
                        File.WriteAllText(startupBatPath, batContent, new UTF8Encoding(false));
                    }
                    else
                    {
                        // 图形界面 / 后台隐藏模式：仅建计划任务，不生成 bat
                        string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                        if (string.IsNullOrEmpty(exePath))
                            exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DelayedStartupTool.exe");

                        string arguments = mode == "hidden" ? "/launch /hidden" : "/launch";

                        var psi = new ProcessStartInfo("schtasks")
                        {
                            Arguments = $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\" {arguments}\" /sc onlogon /rl HIGHEST /f",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        using var proc = Process.Start(psi);
                        proc?.WaitForExit(10000);
                    }

                    SendToast($"已切换到{modeText}模式并更新启动项", "success");
                }
                catch (Exception ex)
                {
                    SendToast($"切换模式失败: {ex.Message}", "error");
                    // 恢复旧模式
                    startupMode = oldMode;
                    SaveSettings();
                }
            }
            else
            {
                string modeText = mode == "gui" ? "图形界面" : (mode == "bat" ? "命令行" : "后台隐藏");
                SendToast($"已切换到{modeText}模式", "success");
            }

            SendMessage("modeChanged", new { mode });
        }

        private void HandleEditPath(JsonElement message)
        {
            int index = GetMessageValue<int>(message, "index");
            string newPath = GetMessageValue<string>(message, "path");

            if (index >= 0 && index < startupItems.Count && !string.IsNullOrEmpty(newPath))
            {
                startupItems[index].FilePath = newPath;
                SaveConfig();
                SendConfigLoaded();
                SendToast("路径已更新", "success");
            }
        }

        private void HandleEditArgs(JsonElement message)
        {
            int index = GetMessageValue<int>(message, "index");
            string args = GetMessageValue<string>(message, "args");

            if (index >= 0 && index < startupItems.Count)
            {
                startupItems[index].Arguments = args;
                SaveConfig();
                SendConfigLoaded();
                SendToast("参数已更新", "success");
            }
        }

        private void HandleEditComment(JsonElement message)
        {
            int index = GetMessageValue<int>(message, "index");
            string comment = GetMessageValue<string>(message, "comment");

            if (index >= 0 && index < startupItems.Count)
            {
                startupItems[index].Comment = comment;
                SaveConfig();
                SendConfigLoaded();
                SendToast("备注已更新", "success");
            }
        }

        private void HandleCreateItemShortcut(JsonElement message)
        {
            try
            {
                string filePath = GetMessageValue<string>(message, "path") ?? "";
                string name = GetMessageValue<string>(message, "name") ?? "";

                if (string.IsNullOrEmpty(filePath))
                {
                    SendToast("无法创建快捷方式：路径为空", "error");
                    return;
                }

                string expandedPath = Environment.ExpandEnvironmentVariables(filePath);
                string shortcutName = !string.IsNullOrEmpty(name) ? name : Path.GetFileNameWithoutExtension(expandedPath);
                string shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), shortcutName + ".lnk");

                string targetPath = expandedPath;
                string arguments = "";

                // 如果是 .lnk 文件，解析原始目标
                if (expandedPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string resolvedTarget = ResolveShortcut(expandedPath);
                    if (!string.IsNullOrEmpty(resolvedTarget) && File.Exists(resolvedTarget))
                    {
                        targetPath = resolvedTarget;
                    }
                    else
                    {
                        // 无法解析则直接指向 .lnk 文件
                        targetPath = expandedPath;
                    }
                }

                string directory = Path.GetDirectoryName(targetPath) ?? "";
                CreateShortcut(shortcutPath, targetPath, arguments, directory);
                SendToast($"已为 {shortcutName} 创建桌面快捷方式", "success");
            }
            catch (Exception ex)
            {
                SendToast($"创建快捷方式失败: {ex.Message}", "error");
            }
        }

        private void HandleOpenDir(JsonElement message)
        {
            string path = GetMessageValue<string>(message, "path");

            if (!string.IsNullOrEmpty(path))
            {
                string expandedPath = Environment.ExpandEnvironmentVariables(path);
                if (expandedPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    expandedPath = ResolveShortcut(expandedPath);
                }

                string directory = Path.GetDirectoryName(expandedPath);
                if (Directory.Exists(directory))
                {
                    Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
                }
            }
        }

        /// <summary>
        /// 右键菜单“立即启动”：按项目配置（路径 + 参数 + 工作目录）立即启动该程序，
        /// 不等延迟启动队列。失败时在 UI 上提示，不影响主程序。
        /// </summary>
        private void HandleLaunchItem(JsonElement message)
        {
            try
            {
                int index = GetMessageValue<int>(message, "index");
                if (index < 0 || index >= startupItems.Count)
                {
                    SendToast("启动失败：无效的项目索引", "error");
                    return;
                }

                var item = startupItems[index];
                if (item == null || string.IsNullOrWhiteSpace(item.FilePath))
                {
                    SendToast("启动失败：路径为空", "error");
                    return;
                }

                string expandedPath = Environment.ExpandEnvironmentVariables(item.FilePath);
                string displayName = !string.IsNullOrEmpty(item.Comment)
                    ? item.Comment
                    : Path.GetFileName(expandedPath);

                var psi = new ProcessStartInfo
                {
                    FileName = expandedPath,
                    Arguments = item.Arguments ?? "",
                    WorkingDirectory = Path.GetDirectoryName(expandedPath) ?? "",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };

                using (Process.Start(psi))
                {
                    // 仅触发进程创建，立即释放句柄（不等待程序退出，降低资源占用）
                }

                SendToast($"已启动：{displayName}", "success");
            }
            catch (Exception ex)
            {
                SendToast($"启动失败：{ex.Message}", "error");
            }
        }

        private void HandleJsError(JsonElement message)
        {
            try
            {
                string msg = GetMessageValue<string>(message, "message") ?? "JS error";
                string? stack = GetMessageValue<string>(message, "stack");
                App.LogException(new Exception(msg), "JS.jsError");
            }
            catch { /* 日志失败静默 */ }
        }

        private void HandleCreateShortcut()
        {
            try
            {
                string shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "延迟启动工具.lnk");
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(exePath))
                    exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DelayedStartupTool.exe");
                CreateShortcut(shortcutPath, exePath, "", AppDomain.CurrentDomain.BaseDirectory);
                SendToast("已创建桌面快捷方式", "success");
            }
            catch (Exception ex)
            {
                SendToast($"创建快捷方式失败: {ex.Message}", "error");
            }
        }

        private void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new Exception("无法创建WScript.Shell对象");
            object shellObj = Activator.CreateInstance(shellType) ?? throw new Exception("无法创建WScript.Shell实例");
            object shortcutObj = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shellObj, new object[] { shortcutPath }) ?? throw new Exception("无法创建快捷方式对象");
            Type shortcutType = shortcutObj.GetType();
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcutObj, new object[] { targetPath });
            shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcutObj, new object[] { arguments });
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcutObj, new object[] { workingDirectory });
            shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcutObj, new object[] { "延迟启动工具" });
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcutObj, null);
        }

        private void HandleRemoveStartup()
        {
            RemoveFromStartup();
        }

        private void HandleOpenStartupDir()
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            Process.Start(new ProcessStartInfo { FileName = startupFolder, UseShellExecute = true });
        }

        private void HandleAbout()
        {
            MessageBox.Show("Delayed Startup Tool Pro V2.0.2\n\n延迟启动工具 - 管理开机自启动程序，为每个程序设置自定义延迟启动时间\n\n制作者：ShuiYuXiang", "关于", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private void HandleHelp()
        {
            MessageBox.Show("使用说明:\n\n【添加启动项】\n点击\"添加\"按钮选择要延迟启动的程序，或直接从资源管理器拖放文件到列表中\n\n【设置延迟时间】\n使用滑块或点击延迟值输入框设置延迟秒数\n\n【调整启动顺序】\n使用\"上移\"、\"下移\"按钮或拖拽列表项进行排序\n\n【编辑项】\n右键点击列表项可以编辑路径、参数或备注\n\n【一键优化】\n根据程序类型自动分配最佳延迟时间\n\n【启动模式】\n图形界面：美观的进度界面\n命令行：传统命令行窗口\n后台隐藏：无界面启动", "帮助", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ============================================================
        // 拖动 — WPF overlay 层直接捕获鼠标（在 WebView2 上方）
        // ============================================================
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // WM_NCCALCSIZE — 消除 WindowStyle=None 时顶部的白色边框
            // 阻止 WPF 默认处理此消息，让 Windows 不绘制非客户区边框
            if (msg == WM_NCCALCSIZE)
            {
                handled = true;
                return IntPtr.Zero;
            }

            // WM_DROPFILES 备用拖放方案 — 当 COM RegisterDragDrop 失败时生效
            if (msg == WM_DROPFILES)
            {
                handled = true;
                SendMessage("fileDragLeave", null);
                var files = ExtractDroppedFiles(wParam);
                if (files.Count > 0)
                {
                    Dispatcher.BeginInvoke(new Action(() => AddDroppedFiles(files)));
                }
                // 释放拖放内存
                DragFinish(wParam);
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        private List<string> ExtractDroppedFiles(IntPtr hDrop)
        {
            var files = new List<string>();
            uint fileCount = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < fileCount; i++)
            {
                uint chars = DragQueryFile(hDrop, i, null, 0);
                if (chars == 0) continue;
                var sb = new StringBuilder((int)chars + 1);
                DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                files.Add(sb.ToString());
            }
            return files;
        }

        private void SendStartupToggled(bool isInStartup, string message)
        {
            SendMessage("startupToggled", new { isInStartup, message });
        }

        private void SendToast(string message, string toastType = "success")
        {
            SendMessage("toast", new { message, toastType });
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveConfig();
            DisposeWebViewSafely();
        }

        /// <summary>
        /// 关闭窗口时尽量释放 WebView2 资源（浏览器进程、缓存句柄），降低内存占用。
        /// 全程 try/catch，绝不让释放失败阻止程序退出。
        /// </summary>
        private void DisposeWebViewSafely()
        {
            try
            {
                if (WebView?.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.Stop();
                }
            }
            catch { /* 停止导航失败不影响后续释放 */ }

            try
            {
                WebView?.Dispose();
            }
            catch (Exception ex)
            {
                App.LogException(ex, "DisposeWebView");
            }
        }


    }

    public class StartupItem
    {
        public string FilePath { get; set; } = "";
        public int Delay { get; set; } = 0;
        public string Comment { get; set; } = "";
        public string Arguments { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    public class SystemStartupEntry
    {
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string Source { get; set; } = "";
        public string SourceDisplay { get; set; } = "";
        public bool IsManaged { get; set; } = false;
        public bool IsSelf { get; set; } = false;
        public string? OriginalValue { get; set; }
        public string? BackupPath { get; set; }
        public bool Disabled { get; set; } = false;
        public string TaskPath { get; set; } = "";
    }

    public class ScanResultsData
    {
        public string ScanTime { get; set; } = "";
        public List<SystemStartupEntry> Entries { get; set; } = new();
    }

    public class OptimizationSuggestion
    {
        public string Category { get; set; } = "";
        public int OldDelay { get; set; } = 0;
        public int NewDelay { get; set; } = 0;
        public bool Changed { get; set; } = false;
        public string Reason { get; set; } = "";
        public object? Item { get; set; } = null;
    }

    public class Settings
    {
        public string Mode { get; set; } = "gui";
        public bool? IsDark { get; set; } = false;
    }
}