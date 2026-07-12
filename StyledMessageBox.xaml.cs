using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DelayedStartupTool
{
    public partial class StyledMessageBox : Window
    {
        private MessageBoxButton _buttonType;

        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        public StyledMessageBox()
        {
            InitializeComponent();
            LoadTitleIcon();
        }

        private void LoadTitleIcon()
        {
            try
            {
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "datashui", "max.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    TitleIcon.Source = new BitmapImage(new Uri(iconPath, UriKind.Absolute));
                }
                else
                {
                    TitleIcon.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                TitleIcon.Visibility = Visibility.Collapsed;
            }
        }

        public static MessageBoxResult Show(
            string message,
            string title,
            MessageBoxButton button = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.None,
            string? okText = null,
            string? cancelText = null)
        {
            var window = new StyledMessageBox
            {
                _buttonType = button,
                TitleText = { Text = title },
                MessageText = { Text = message }
            };

            // Load icon safely (pack URI for runtime reliability)
            try
            {
                window.SetIconSource(image);
            }
            catch
            {
                window.IconPath.Visibility = System.Windows.Visibility.Collapsed;
            }

            // Configure buttons
            if (button == MessageBoxButton.OK)
            {
                window.CancelButton.Visibility = System.Windows.Visibility.Collapsed;
                window.OkButton.Content = okText ?? "确定";
                window.OkButton.Background = new SolidColorBrush(Color.FromRgb(59, 130, 246));
            }
            else if (button == MessageBoxButton.OKCancel)
            {
                window.OkButton.Content = okText ?? "确定";
                window.CancelButton.Content = cancelText ?? "取消";
                window.OkButton.Background = new SolidColorBrush(Color.FromRgb(59, 130, 246));
            }
            else if (button == MessageBoxButton.YesNo)
            {
                window.OkButton.Content = okText ?? "是";
                window.CancelButton.Content = cancelText ?? "否";
                window.OkButton.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            }
            else
            {
                // YesNoCancel fallback
                window.OkButton.Content = okText ?? "是";
                window.CancelButton.Content = cancelText ?? "否";
                window.OkButton.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            }

            window.ShowDialog();
            return window.Result;
        }

        private void SetIconSource(MessageBoxImage image)
        {
            Brush fill;
            Geometry geometry;

            switch (image)
            {
                case MessageBoxImage.Warning: // 48, same as Exclamation
                    fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                    geometry = CreateWarningGeometry();
                    break;
                case MessageBoxImage.Error: // 16, same as Hand
                    fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    geometry = CreateErrorGeometry();
                    break;
                case MessageBoxImage.Information: // 64, same as Asterisk
                    fill = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                    geometry = CreateInfoGeometry();
                    break;
                case MessageBoxImage.Question:
                    fill = new SolidColorBrush(Color.FromRgb(59, 130, 246));
                    geometry = CreateQuestionGeometry();
                    break;
                default:
                    IconPath.Visibility = System.Windows.Visibility.Collapsed;
                    return;
            }

            IconPath.Data = geometry;
            IconPath.Fill = fill;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Result = _buttonType == MessageBoxButton.YesNo || _buttonType == MessageBoxButton.YesNoCancel
                ? MessageBoxResult.Yes
                : MessageBoxResult.OK;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Result = _buttonType == MessageBoxButton.YesNo || _buttonType == MessageBoxButton.YesNoCancel
                ? MessageBoxResult.No
                : MessageBoxResult.Cancel;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Result = _buttonType == MessageBoxButton.YesNo || _buttonType == MessageBoxButton.YesNoCancel
                ? MessageBoxResult.No
                : MessageBoxResult.Cancel;
            Close();
        }

        private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private static Geometry CreateWarningGeometry()
        {
            var group = new GeometryGroup();

            // Triangle outline
            var triangle = new PathGeometry();
            var triangleFigure = new PathFigure { StartPoint = new Point(12, 2) };
            triangleFigure.Segments.Add(new LineSegment(new Point(2, 22), true));
            triangleFigure.Segments.Add(new LineSegment(new Point(22, 22), true));
            triangleFigure.Segments.Add(new LineSegment(new Point(12, 2), true));
            triangleFigure.IsClosed = true;
            triangle.Figures.Add(triangleFigure);
            group.Children.Add(triangle);

            // Exclamation dot
            group.Children.Add(new EllipseGeometry(new Point(12, 18), 1.2, 1.2));

            // Exclamation stem
            var stem = new PathGeometry();
            var stemFigure = new PathFigure { StartPoint = new Point(12, 8) };
            stemFigure.Segments.Add(new LineSegment(new Point(12, 15), true));
            stemFigure.IsClosed = false;
            stem.Figures.Add(stemFigure);
            group.Children.Add(stem);

            return group;
        }

        private static Geometry CreateErrorGeometry()
        {
            var group = new GeometryGroup();
            group.Children.Add(new EllipseGeometry(new Point(12, 12), 10, 10));

            var x = new PathGeometry();
            var f1 = new PathFigure { StartPoint = new Point(7, 7) };
            f1.Segments.Add(new LineSegment(new Point(17, 17), true));
            f1.IsClosed = false;
            var f2 = new PathFigure { StartPoint = new Point(17, 7) };
            f2.Segments.Add(new LineSegment(new Point(7, 17), true));
            f2.IsClosed = false;
            x.Figures.Add(f1);
            x.Figures.Add(f2);
            group.Children.Add(x);

            return group;
        }

        private static Geometry CreateInfoGeometry()
        {
            var group = new GeometryGroup();
            group.Children.Add(new EllipseGeometry(new Point(12, 12), 10, 10));

            var info = new PathGeometry();
            var f1 = new PathFigure { StartPoint = new Point(12, 6) };
            f1.Segments.Add(new LineSegment(new Point(12, 12), true));
            f1.Segments.Add(new LineSegment(new Point(12, 18), true));
            f1.IsClosed = false;
            info.Figures.Add(f1);
            group.Children.Add(info);

            return group;
        }

        private static Geometry CreateQuestionGeometry()
        {
            var group = new GeometryGroup();
            group.Children.Add(new EllipseGeometry(new Point(12, 12), 10, 10));

            var q = new PathGeometry();
            // dot
            var f1 = new PathFigure { StartPoint = new Point(12, 17) };
            f1.Segments.Add(new LineSegment(new Point(12, 17), true));
            f1.IsClosed = false;
            // vertical
            var f2 = new PathFigure { StartPoint = new Point(12, 13) };
            f2.Segments.Add(new LineSegment(new Point(12, 8), true));
            f2.IsClosed = false;
            // horizontal top
            var f3 = new PathFigure { StartPoint = new Point(12, 8) };
            f3.Segments.Add(new LineSegment(new Point(15, 8), true));
            f3.IsClosed = false;
            q.Figures.Add(f1);
            q.Figures.Add(f2);
            q.Figures.Add(f3);
            group.Children.Add(q);

            return group;
        }
    }
}
