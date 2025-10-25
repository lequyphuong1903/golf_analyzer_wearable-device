using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Point = System.Windows.Point;

namespace GolfAnalyzer
{
    public partial class MainWindow : Window
    {
        private bool _isDrawing;
        private System.Windows.Point _startPoint;
        private Shape? _currentShape;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new ViewModels.MainViewModel();

            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(vm.EraseTick))
                    {
                        DrawCanvas.Children.Clear();
                        _currentShape = null;
                    }
                };
            }
        }

        private void DrawCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not ViewModels.MainViewModel vm) return;
            if (vm.CurrentDrawingMode == ViewModels.DrawingMode.None) return;

            _isDrawing = true;
            _startPoint = e.GetPosition(DrawCanvas);

            var strokeBrush = vm.DrawingBrush;
            const double thickness = 3.0;

            switch (vm.CurrentDrawingMode)
            {
                case ViewModels.DrawingMode.Line:
                    var line = new Line
                    {
                        X1 = _startPoint.X,
                        Y1 = _startPoint.Y,
                        X2 = _startPoint.X,
                        Y2 = _startPoint.Y,
                        Stroke = strokeBrush,
                        StrokeThickness = thickness,
                        SnapsToDevicePixels = true
                    };
                    _currentShape = line;
                    DrawCanvas.Children.Add(line);
                    break;

                case ViewModels.DrawingMode.Circle:
                    var ellipse = new Ellipse
                    {
                        Stroke = strokeBrush,
                        StrokeThickness = thickness,
                        Fill = System.Windows.Media.Brushes.Transparent
                    };
                    _currentShape = ellipse;
                    Canvas.SetLeft(ellipse, _startPoint.X);
                    Canvas.SetTop(ellipse, _startPoint.Y);
                    ellipse.Width = 0;
                    ellipse.Height = 0;
                    DrawCanvas.Children.Add(ellipse);
                    break;

                case ViewModels.DrawingMode.Rectangle:
                    var rect = new System.Windows.Shapes.Rectangle
                    {
                        Stroke = strokeBrush,
                        StrokeThickness = thickness,
                        Fill = System.Windows.Media.Brushes.Transparent
                    };
                    _currentShape = rect;
                    Canvas.SetLeft(rect, _startPoint.X);
                    Canvas.SetTop(rect, _startPoint.Y);
                    rect.Width = 0;
                    rect.Height = 0;
                    DrawCanvas.Children.Add(rect);
                    break;
            }

            DrawCanvas.CaptureMouse();
        }

        private void DrawCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isDrawing || _currentShape is null) return;
            if (DataContext is not ViewModels.MainViewModel vm) return;

            Point pos = e.GetPosition(DrawCanvas);

            switch (vm.CurrentDrawingMode)
            {
                case ViewModels.DrawingMode.Line:
                    if (_currentShape is Line line)
                    {
                        line.X2 = pos.X;
                        line.Y2 = pos.Y;
                    }
                    break;

                case ViewModels.DrawingMode.Circle:
                    if (_currentShape is Ellipse ellipse)
                    {
                        double dx = pos.X - _startPoint.X;
                        double dy = pos.Y - _startPoint.Y;
                        double diameter = System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy));
                        double left = _startPoint.X + (dx < 0 ? -diameter : 0);
                        double top = _startPoint.Y + (dy < 0 ? -diameter : 0);

                        Canvas.SetLeft(ellipse, left);
                        Canvas.SetTop(ellipse, top);
                        ellipse.Width = diameter;
                        ellipse.Height = diameter;
                    }
                    break;

                case ViewModels.DrawingMode.Rectangle:
                    if (_currentShape is System.Windows.Shapes.Rectangle rect)
                    {
                        double x = System.Math.Min(pos.X, _startPoint.X);
                        double y = System.Math.Min(pos.Y, _startPoint.Y);
                        double w = System.Math.Abs(pos.X - _startPoint.X);
                        double h = System.Math.Abs(pos.Y - _startPoint.Y);

                        Canvas.SetLeft(rect, x);
                        Canvas.SetTop(rect, y);
                        rect.Width = w;
                        rect.Height = h;
                    }
                    break;
            }
        }

        private void DrawCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDrawing = false;
            _currentShape = null;
            DrawCanvas.ReleaseMouseCapture();
        }
    }
}