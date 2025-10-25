using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GolfAnalyzer.Models;
using System.Windows.Media;

namespace GolfAnalyzer.ViewModels;

public enum DrawingMode
{
    None,
    Line,
    Circle,
    Rectangle
}

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableObject? currentViewModel;

    private readonly HomeViewModel _homeViewModel;
    private readonly DashboardViewModel _dashboardViewModel;

    public MainViewModel()
    {
        _homeViewModel = new HomeViewModel();
        _dashboardViewModel = new DashboardViewModel();
        CurrentViewModel = _homeViewModel;

        // Initialize drawing brush
        _palette = new[]
        {
            Brushes.Orange,
            Brushes.Lime,
            Brushes.DeepSkyBlue,
            Brushes.Red,
            Brushes.Yellow,
            Brushes.Magenta,
            Brushes.Cyan,
            Brushes.White
        };
        _paletteIndex = 3;
        DrawingBrush = _palette[_paletteIndex];
        SkeletonBrush = Brushes.Orange;
        MouseBrush = Brushes.Orange;
        CircleBrush = Brushes.Orange;
        RectangleBrush = Brushes.Orange;
        LineBrush = Brushes.Orange;
    }

    [RelayCommand]
    private void ShowHome() => CurrentViewModel = _homeViewModel;

    [RelayCommand]
    private void ShowDashboard() => CurrentViewModel = _dashboardViewModel;


    [RelayCommand]
    private void Quit()
    {
        _homeViewModel.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    // ===== Drawing support =====
    [ObservableProperty]
    private DrawingMode currentDrawingMode = DrawingMode.None;

    public bool IsDrawingActive => CurrentDrawingMode != DrawingMode.None;

    partial void OnCurrentDrawingModeChanged(DrawingMode value)
    {
        OnPropertyChanged(nameof(IsDrawingActive));
    }

    // Brush used for new shapes
    [ObservableProperty]
    private SolidColorBrush drawingBrush;

    [ObservableProperty]
    private SolidColorBrush skeletonBrush;

    [ObservableProperty]
    private SolidColorBrush mouseBrush;

    [ObservableProperty]
    private SolidColorBrush circleBrush;

    [ObservableProperty]
    private SolidColorBrush rectangleBrush;

    [ObservableProperty]
    private SolidColorBrush lineBrush;

    private readonly SolidColorBrush[] _palette;
    private int _paletteIndex;

    // Increment when Erase is invoked so the View can react.
    [ObservableProperty]
    private int eraseTick;

    [RelayCommand]
    private void AISkeleton()
    {
        Flag.AISkeleton = !Flag.AISkeleton;
        if (!Flag.AISkeleton)
        {
            SkeletonBrush = Brushes.Gray;
        }
        else
        {
            SkeletonBrush = Brushes.Orange;
        }
    }

    [RelayCommand]
    private void Mouse()
    {
        CurrentDrawingMode = DrawingMode.None;
        MouseBrush = Brushes.Orange;
        Flag.IsLineDrawing = false;
        Flag.IsCircleDrawing = false;
        Flag.IsRectangleDrawing = false;
        LineBrush = Brushes.Orange;
        CircleBrush = Brushes.Orange;
        RectangleBrush = Brushes.Orange;
    }

    [RelayCommand]
    private void Color()
    {
        _paletteIndex = (_paletteIndex + 1) % _palette.Length;
        DrawingBrush = _palette[_paletteIndex];
    }

    [RelayCommand]
    private void DrawLine() 
    {
        Flag.IsLineDrawing = !Flag.IsLineDrawing;
        Flag.IsCircleDrawing = false;
        Flag.IsRectangleDrawing = false;
        if (Flag.IsLineDrawing)
        {
            CurrentDrawingMode = DrawingMode.Line;
            MouseBrush = Brushes.Gray;
            CircleBrush = Brushes.Orange;
            RectangleBrush = Brushes.Orange;
            LineBrush = DrawingBrush;
        }
        else
        {
            CurrentDrawingMode = DrawingMode.None;
            MouseBrush = Brushes.Orange;
            LineBrush = Brushes.Orange;
        }
        
    }

    [RelayCommand]
    private void DrawCircle() { 
        Flag.IsCircleDrawing = !Flag.IsCircleDrawing;
        Flag.IsLineDrawing = false;
        Flag.IsRectangleDrawing = false;
        if (Flag.IsCircleDrawing)
        {
            CurrentDrawingMode = DrawingMode.Circle;
            MouseBrush = Brushes.Gray;
            LineBrush = Brushes.Orange;
            RectangleBrush = Brushes.Orange;
            CircleBrush = DrawingBrush;
        }
        else
        {
            CurrentDrawingMode = DrawingMode.None;
            MouseBrush = Brushes.Orange;
            CircleBrush = Brushes.Orange;
        }
    }

    [RelayCommand]
    private void DrawRectangle()
    {
        Flag.IsRectangleDrawing = !Flag.IsRectangleDrawing;
        Flag.IsLineDrawing = false;
        Flag.IsCircleDrawing = false;
        if (Flag.IsRectangleDrawing)
        {
            CurrentDrawingMode = DrawingMode.Rectangle;
            MouseBrush = Brushes.Gray;
            CircleBrush = Brushes.Orange;
            LineBrush = Brushes.Orange;
            RectangleBrush = DrawingBrush;
        }
        else
        {
            CurrentDrawingMode = DrawingMode.None;
            MouseBrush = Brushes.Orange;
            RectangleBrush = Brushes.Orange;
        }
    }

    [RelayCommand]
    private void Erase()
    {
        EraseTick++;
        // Optionally exit drawing mode:
        // CurrentDrawingMode = DrawingMode.None;
    }
}