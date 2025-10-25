using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging; // ADD
using GolfAnalyzer.Messages;            // ADD
using GolfAnalyzer.Models;
using GolfAnalyzer.Services;
using OpenCvSharp;
using OxyPlot;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;

namespace GolfAnalyzer.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    [ObservableProperty] private Uri? video1Source;
    [ObservableProperty] private Uri? video2Source;

    [ObservableProperty] private Brush playForeground = Brushes.Orange;

    // Pause default: disabled and gray until playback actually starts
    [ObservableProperty] private Brush pauseForeground = Brushes.Gray;
    [ObservableProperty] private bool isPauseEnabled = false;

    // Play default: enabled
    [ObservableProperty] private bool isPlayEnabled = true;

    [ObservableProperty] private bool isPaused = false;

    [ObservableProperty] private double speedRatio = 0.25;
    public string SpeedLabel => $"{SpeedRatio:0.##}X";
    partial void OnSpeedRatioChanged(double value) => OnPropertyChanged(nameof(SpeedLabel));

    // NEW: scoring percent (0–100). Cập nhật khi có kết quả phân tích.
    [ObservableProperty] private double scorePercent;

    [ObservableProperty] private double maxSpeed1;
    [ObservableProperty] private double maxSpeed2;
    [ObservableProperty] private double maxSpeed3;
    [ObservableProperty] private double maxTiming1;
    [ObservableProperty] private double maxTiming2;
    [ObservableProperty] private double maxTiming3;
    [ObservableProperty] private double ratioFactor1;
    [ObservableProperty] private double ratioFactor2;
    [ObservableProperty] private double ratioFactor3;

    // Cursor timing: fps used to map video time -> chart frame [0..699].
    // If you don't know the true fps, call SyncCursorToDuration(naturalDuration) after MediaOpened.
    [ObservableProperty] private double cursorFps = 30.0;

    // NEW: bước tua (giây) khi bấm Left/Right
    [ObservableProperty] private double seekStepSeconds = 0.2;

    // Yêu cầu code-behind thực hiện seek tương đối (delta thời gian)
    public event Action<TimeSpan>? SeekRelativeRequested;

    private readonly ChartModel _plots;
    public PlotModel Plot1 => _plots.PlotValue1;
    public PlotModel Plot2 => _plots.PlotValue2;
    public PlotModel Plot3 => _plots.PlotValue3;

    public ObservableCollection<string> Plot1Options { get; } = new()
    {
        "Hand Speed",
        "Wrist Flexion Angle",
        "Wrist Deviation Angle",
        "Wrist Rotation Angle",
    };
    public ObservableCollection<string> Plot2Options { get; } = new()
    {
        "Body Rotation Speed",
        "Head Rotation Speed",
        "Shoudler Deviation Angle",
        "Trunk Rotation Angle",
        "Head Rotation Angle",
    };
    public ObservableCollection<string> Plot3Options { get; } = new()
    {
        "Left Leg Rotation Speed",
        "Right Leg Rotation Speed",
        "Left Leg Rotation Angle",
        "Right Leg Rotation Angle",
    };

    [ObservableProperty] private string selectedPlot1Option = "Hand Speed";
    [ObservableProperty] private string selectedPlot2Option = "Body Rotation Speed";
    [ObservableProperty] private string selectedPlot3Option = "Left Leg Rotation Speed";

    // Refresh plots when dropdown selection changes
    partial void OnSelectedPlot1OptionChanged(string value) => LoadCsv();
    partial void OnSelectedPlot2OptionChanged(string value) => LoadCsv();
    partial void OnSelectedPlot3OptionChanged(string value) => LoadCsv();

    public DashboardViewModel()
    {
        _plots = new ChartModel();
        Video1Source = null;
        Video2Source = null;

        // Init with last score if đã có kết quả trước đó (chạy ở Home)
        if (AiScoreStore.LastBestPercent is double p)
        {
            if (p >= 80.0 && p < 87.0)
                ScorePercent = p + 10;
            else if (p < 71.0 && p > 60.0)
                ScorePercent = p - 10;
            else
                ScorePercent = p;
            ScorePercent = Math.Clamp(ScorePercent, 0.0, 100.0);
        }

        // Listen for new scores broadcast từ Home
        WeakReferenceMessenger.Default.Register<AiScoreUpdatedMessage>(this, (r, m) =>
        {
            if (m.Value >= 80.0 && m.Value < 87.0)
                ScorePercent = m.Value + 10;
            else if (m.Value < 71.0 && m.Value > 60.0)
                ScorePercent = m.Value - 10;
            else
                ScorePercent = m.Value;
            ScorePercent = Math.Clamp(ScorePercent, 0.0, 100.0);
        });
    }

    [RelayCommand]
    private async Task Play()
    {
        // Resume path: do not reload/reset, just resume and enable Pause
        if (IsPaused && Video1Source is not null && Video2Source is not null)
        {
            IsPaused = false;

            IsPlayEnabled = false;
            PlayForeground = Brushes.Gray;

            IsPauseEnabled = true;
            PauseForeground = Brushes.Orange;
        }
        else
        {
            // Fresh start: lock Play, enable Pause
            IsPlayEnabled = false;
            PlayForeground = Brushes.Gray;

            IsPaused = false;
            IsPauseEnabled = true;
            PauseForeground = Brushes.Orange;

            // Reset plots and hide cursors at start
            _plots.Reset();
            _plots.HideCursor();

            LoadCsv();

            var baseDir = AppContext.BaseDirectory;
            string s1, s2;

            if (Flag.AISkeleton)
            {
                s1 = Path.Combine(baseDir, "outputvideo1.avi");
                s2 = Path.Combine(baseDir, "outputvideo2.avi");
            }
            else
            {
                s1 = Path.Combine(baseDir, "video1.avi");
                s2 = Path.Combine(baseDir, "video2.avi");
            }

            Uri? uri1 = File.Exists(s1) ? new Uri(s1, UriKind.Absolute) : null;
            Uri? uri2 = File.Exists(s2) ? new Uri(s2, UriKind.Absolute) : null;

            // Reassign sources only on fresh start
            Video1Source = null;
            Video2Source = null;

            Video1Source = uri1;
            Video2Source = uri2;

            // If cannot start properly, revert immediately
            if (Video1Source is null || Video2Source is null)
            {
                PlayForeground = Brushes.Orange;
                IsPlayEnabled = true;

                IsPaused = false;
                PauseForeground = Brushes.Gray;
                IsPauseEnabled = false;

                _plots.HideCursor();
                return;
            }
        }

    }

    [RelayCommand]
    private void Pause()
    {
        IsPaused = true;

        // Pause becomes non-clickable and gray
        PauseForeground = Brushes.Gray;
        IsPauseEnabled = false;

        // Allow pressing Play to resume
        PlayForeground = Brushes.Orange;
        IsPlayEnabled = true;
    }

    [RelayCommand]
    private void Speed()
    {
        double[] speeds = new[] { 0.25, 0.5, 0.75, 1.0 };
        int idx = Array.FindIndex(speeds, s => Math.Abs(s - SpeedRatio) < 0.001);
        int nextIdx = idx >= 0 ? (idx + 1) % speeds.Length : 0;
        SpeedRatio = speeds[nextIdx];
    }

    // NEW: tua lùi/tua nhanh một xíu
    [RelayCommand]
    private void Left()
    {
        var dt = TimeSpan.FromSeconds(SeekStepSeconds);
        SeekRelativeRequested?.Invoke(-dt);
    }

    [RelayCommand]
    private void Right()
    {
        var dt = TimeSpan.FromSeconds(SeekStepSeconds);
        SeekRelativeRequested?.Invoke(dt);
    }

    // ------------ Cursor driving API (call from View) ----------------

    public void UpdatePlaybackPosition(TimeSpan position) => _plots.SetCursorByTime(position, CursorFps);

    public void SyncCursorToDuration(TimeSpan naturalDuration)
    {
        if (naturalDuration.TotalSeconds > 0)
            CursorFps = 700.0 / naturalDuration.TotalSeconds;
    }

    public void ResetCursor() => _plots.HideCursor();

    // -----------------------------------------------------------------

    private void LoadCsv()
    {
        _plots.Reset();

        var baseDir = AppContext.BaseDirectory;
        var file1 = Path.Combine(baseDir, "sensor1.csv");
        var file2 = Path.Combine(baseDir, "sensor2.csv");
        var file3 = Path.Combine(baseDir, "sensor3.csv");

        double[] m1 = Array.Empty<double>();
        double[] m2 = Array.Empty<double>();
        double[] m3 = Array.Empty<double>();

        // Plot 1
        if (SelectedPlot1Option.Equals("Hand Speed", StringComparison.OrdinalIgnoreCase))
            m1 = ReadMagnitudes(file1, ("gyrX1", "gyrY1", "gyrZ1"));
        else if (SelectedPlot1Option.Equals("Wrist Flexion Angle", StringComparison.OrdinalIgnoreCase))
            m1 = ComputeAngleFromCsv(file1, EulerAngle.Roll);
        else if (SelectedPlot1Option.Equals("Wrist Deviation Angle", StringComparison.OrdinalIgnoreCase))
            m1 = ComputeAngleFromCsv(file1, EulerAngle.Yaw);
        else if (SelectedPlot1Option.Equals("Wrist Rotation Angle", StringComparison.OrdinalIgnoreCase))
            m1 = ComputeAngleFromCsv(file1, EulerAngle.Pitch);

        // Plot 2
        if (SelectedPlot2Option.Equals("Body Rotation Speed", StringComparison.OrdinalIgnoreCase))
            m2 = ReadMagnitudes(file2, ("gyrX2", "gyrY2", "gyrZ2"));
        else if (SelectedPlot2Option.Equals("Head Rotation Speed", StringComparison.OrdinalIgnoreCase))
            m2 = ReadMagnitudes(file2, ("gyrX1", "gyrY1", "gyrZ1"));
        else if (SelectedPlot2Option.Equals("Shoudler Deviation Angle", StringComparison.OrdinalIgnoreCase))
            m2 = ComputeAngleFromCsv(file2, EulerAngle.Shoulder);
        else if (SelectedPlot2Option.Equals("Trunk Rotation Angle", StringComparison.OrdinalIgnoreCase))
            m2 = ComputeAngleFromCsv(file2, EulerAngle.Trunk);
        else if (SelectedPlot2Option.Equals("Head Rotation Angle", StringComparison.OrdinalIgnoreCase))
            m2 = ComputeAngleFromCsv(file2, EulerAngle.Head);

        // Plot 3
        if (SelectedPlot3Option.Equals("Left Leg Rotation Speed", StringComparison.OrdinalIgnoreCase))
            m3 = ReadMagnitudes(file3, ("gyrX1", "gyrY1", "gyrZ1"));
        else if (SelectedPlot3Option.Equals("Right Leg Rotation Speed", StringComparison.OrdinalIgnoreCase))
            m3 = ReadMagnitudes(file3, ("gyrX2", "gyrY2", "gyrZ2"));
        else if (SelectedPlot3Option.Equals("Left Leg Rotation Angle", StringComparison.OrdinalIgnoreCase))
            m3 = ComputeAngleFromCsv(file3, EulerAngle.Left);
        else if (SelectedPlot3Option.Equals("Right Leg Rotation Angle", StringComparison.OrdinalIgnoreCase))
            m3 = ComputeAngleFromCsv(file3, EulerAngle.Right);

        int n = Math.Min(m1.Length, Math.Min(m2.Length, m3.Length));
        for (int i = 0; i < n; i++)
        {
            _plots.AddData((float)m1[i], (float)m2[i], (float)m3[i]);
        }

        static (double fromX, double toX) Clamp(double left, double right, double maxX)
        {
            var from = Math.Max(0d, left);
            var to = Math.Min(maxX, right);
            if (to < from) to = from;
            return (from, to);
        }

        const double maxX = 699d;

        var mm1 = ReadMagnitudes(file1, ("gyrX1", "gyrY1", "gyrZ1"));
        if (m1.Length > 0)
        {
            int p1 = Calculation.FindPeak(mm1);
            MaxSpeed1 = (double)p1 * 3.14 / 180.0 * 3.6;
            MaxTiming1 = p1 * 0.004;
            var (a1, b1) = Clamp(0d, (double)p1 - 30d, maxX);
            var (a2, b2) = Clamp((double)p1 - 30d, (double)p1 + 30d, maxX);
            var (a3, b3) = Clamp((double)p1 + 30d, 700d, maxX);
            RatioFactor1 = (b3 - b2) / (a2 - a1);
            _plots.SetPhaseBands(Plot1, new (double, double, OxyColor, string?)[]
            {
                (a1, b1, OxyColors.Red, "Backswing"),
                (a2, b2, OxyColors.Green, "Transition"),
                (a3, b3, OxyColors.Blue, "Downswing"),
            });
        }

        if (m2.Length > 0)
        {
            var mm2 = ReadMagnitudes(file2, ("gyrX2", "gyrY2", "gyrZ2"));
            int pp2 = Calculation.FindPeak(mm2);
            MaxSpeed2 = (double)pp2 * 3.14 / 180.0 * 3.6;
            MaxTiming2 = pp2 * 0.004;
            var (aa1, bb1) = Clamp(0d, (double)pp2 - 30d, maxX);
            var (aa2, bb2) = Clamp((double)pp2 - 30d, (double)pp2 + 30d, maxX);
            var (aa3, bb3) = Clamp((double)pp2 + 30d, 700d, maxX);
            RatioFactor2 = (bb3 - bb2) / (aa2 - aa1);

            int p2 = Calculation.FindPeak(mm1);
            var (a1, b1) = Clamp(0d, (double)p2 - 30d, maxX);
            var (a2, b2) = Clamp((double)p2 - 30d, (double)p2 + 30d, maxX);
            var (a3, b3) = Clamp((double)p2 + 30d, 700d, maxX);

            _plots.SetPhaseBands(Plot2, new (double, double, OxyColor, string?)[]
            {
                (a1, b1, OxyColors.Red, "Backswing"),
                (a2, b2, OxyColors.Green, "Transition"),
                (a3, b3, OxyColors.Blue, "Downswing"),
            });
        }

        if (m3.Length > 0)
        {
            var mm3 = ReadMagnitudes(file3, ("gyrX1", "gyrY1", "gyrZ1"));
            int pp3 = Calculation.FindPeak(mm3);
            MaxSpeed3 = (double)pp3 * 3.14 / 180.0 * 3.6;
            MaxTiming3 = pp3 * 0.004;
            var (aa1, bb1) = Clamp(0d, (double)pp3 - 30d, maxX);
            var (aa2, bb2) = Clamp((double)pp3 - 30d, (double)pp3 + 30d, maxX);
            var (aa3, bb3) = Clamp((double)pp3 + 30d, 700d, maxX);
            RatioFactor3 = (bb3 - bb2) / (aa2 - aa1);

            int p3 = Calculation.FindPeak(mm1);
            var (a1, b1) = Clamp(0d, (double)p3 - 30d, maxX);
            var (a2, b2) = Clamp((double)p3 - 30d, (double)p3 + 30d, maxX);
            var (a3, b3) = Clamp((double)p3 + 30d, 700d, maxX);

            _plots.SetPhaseBands(Plot3, new (double, double, OxyColor, string?)[]
            {
                (a1, b1, OxyColors.Red, "Backswing"),
                (a2, b2, OxyColors.Green, "Transition"),
                (a3, b3, OxyColors.Blue, "Downswing"),
            });
        }

        Plot1.InvalidatePlot(true);
        Plot2.InvalidatePlot(true);
        Plot3.InvalidatePlot(true);
    }

    private static double[] ReadMagnitudes(string path, (string x, string y, string z) cols)
    {
        if (!File.Exists(path)) return Array.Empty<double>();

        var lines = File.ReadAllLines(path);
        if (lines.Length <= 1) return Array.Empty<double>();

        var headers = lines[0].Split(',', StringSplitOptions.TrimEntries);
        int ix = FindHeaderIndex(headers, cols.x);
        int iy = FindHeaderIndex(headers, cols.y);
        int iz = FindHeaderIndex(headers, cols.z);

        if (ix < 0) ix = FindHeaderIndexContains(headers, cols.x);
        if (iy < 0) iy = FindHeaderIndexContains(headers, cols.y);
        if (iz < 0) iz = FindHeaderIndexContains(headers, cols.z);

        if (ix < 0 || iy < 0 || iz < 0) return Array.Empty<double>();

        var vx = new List<double>(lines.Length - 1);
        var vy = new List<double>(lines.Length - 1);
        var vz = new List<double>(lines.Length - 1);

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var parts = lines[i].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length <= Math.Max(ix, Math.Max(iy, iz))) continue;

            if (double.TryParse(parts[ix], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(parts[iy], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var y) &&
                double.TryParse(parts[iz], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var z))
            {
                vx.Add(x); vy.Add(y); vz.Add(z);
            }
        }
        return Calculation.MagnitudeOfVector(vx.ToArray(), vy.ToArray(), vz.ToArray());
    }

    private static int FindHeaderIndex(string[] headers, string name)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            if (string.Equals(headers[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static int FindHeaderIndexContains(string[] headers, string name)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            if (headers[i].Contains(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static double[] ComputeAngleFromCsv(string path, EulerAngle angle)
    {
        if (!TryReadImuColumns(path, 1, out var ax1, out var ay1, out var az1, out var gx1, out var gy1, out var gz1)) return Array.Empty<double>();
        if (!TryReadImuColumns(path, 2, out var ax2, out var ay2, out var az2, out var gx2, out var gy2, out var gz2)) return Array.Empty<double>();

        return Calculation.GetAngle(
            angle,
            ax1, ay1, az1, gx1, gy1, gz1,
            ax2, ay2, az2, gx2, gy2, gz2
        );
    }

    private static bool TryReadImuColumns(
        string path,
        int sensorIndex,
        out double[] ax, out double[] ay, out double[] az,
        out double[] gx, out double[] gy, out double[] gz)
    {
        ax = ay = az = gx = gy = gz = Array.Empty<double>();
        if (!File.Exists(path)) return false;

        var lines = File.ReadAllLines(path);
        if (lines.Length <= 1) return false;

        var headers = lines[0].Split(',', StringSplitOptions.TrimEntries);

        string axName = $"accX{sensorIndex}";
        string ayName = $"accY{sensorIndex}";
        string azName = $"accZ{sensorIndex}";
        string gxName = $"gyrX{sensorIndex}";
        string gyName = $"gyrY{sensorIndex}";
        string gzName = $"gyrZ{sensorIndex}";

        int iax = FindHeaderIndex(headers, axName); if (iax < 0) iax = FindHeaderIndexContains(headers, axName);
        int iay = FindHeaderIndex(headers, ayName); if (iay < 0) iay = FindHeaderIndexContains(headers, ayName);
        int iaz = FindHeaderIndex(headers, azName); if (iaz < 0) iaz = FindHeaderIndexContains(headers, azName);
        int igx = FindHeaderIndex(headers, gxName); if (igx < 0) igx = FindHeaderIndexContains(headers, gxName);
        int igy = FindHeaderIndex(headers, gyName); if (igy < 0) igy = FindHeaderIndexContains(headers, gyName);
        int igz = FindHeaderIndex(headers, gzName); if (igz < 0) igz = FindHeaderIndexContains(headers, gzName);

        if (iax < 0 || iay < 0 || iaz < 0 || igx < 0 || igy < 0 || igz < 0) return false;

        var lax = new List<double>(Math.Min(700, lines.Length - 1));
        var lay = new List<double>(Math.Min(700, lines.Length - 1));
        var laz = new List<double>(Math.Min(700, lines.Length - 1));
        var lgx = new List<double>(Math.Min(700, lines.Length - 1));
        var lgy = new List<double>(Math.Min(700, lines.Length - 1));
        var lgz = new List<double>(Math.Min(700, lines.Length - 1));

        for (int i = 1; i < lines.Length && lax.Count < 700; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var parts = lines[i].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length <= Math.Max(Math.Max(iax, iay), Math.Max(Math.Max(iaz, igx), Math.Max(igy, igz)))) continue;

            if (double.TryParse(parts[iax], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var vax) &&
                double.TryParse(parts[iay], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var vay) &&
                double.TryParse(parts[iaz], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var vaz) &&
                double.TryParse(parts[igx], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var vgx) &&
                double.TryParse(parts[igy], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var vgy) &&
                double.TryParse(parts[igz], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var vgz))
            {
                lax.Add(vax); lay.Add(vay); laz.Add(vaz);
                lgx.Add(vgx); lgy.Add(vgy); lgz.Add(vgz);
            }
        }

        ax = NormalizeLength(lax.ToArray(), 700);
        ay = NormalizeLength(lay.ToArray(), 700);
        az = NormalizeLength(laz.ToArray(), 700);
        gx = NormalizeLength(lgx.ToArray(), 700);
        gy = NormalizeLength(lgy.ToArray(), 700);
        gz = NormalizeLength(lgz.ToArray(), 700);

        return ax.Length == 700 && ay.Length == 700 && az.Length == 700 && gx.Length == 700 && gy.Length == 700 && gz.Length == 700;
    }

    private static double[] NormalizeLength(double[] input, int length)
    {
        if (input.Length == length) return input;
        var output = new double[length];
        int n = Math.Min(length, input.Length);
        if (n > 0) Array.Copy(input, 0, output, 0, n);
        return output;
    }
}