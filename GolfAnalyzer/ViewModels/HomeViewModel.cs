using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GolfAnalyzer.Services;
using GolfAnalyzer.Models;
using System.Collections.ObjectModel;
using System.Linq;
using System; // ensure present
using System.Collections.Generic; // if needed
using System.Threading.Tasks; // ensure present
using CommunityToolkit.Mvvm.Messaging; // ADD
using GolfAnalyzer.Messages;           // ADD

namespace GolfAnalyzer.ViewModels;

public partial class HomeViewModel : ObservableObject, IDisposable
{
    private VideoCapture capture1;
    private VideoCapture capture2;
    private VideoWriter? videoWriter1;
    private VideoWriter? videoWriter2;
    private Mat frame1;
    private Mat frame2;
    private WriteableBitmap bitmap1;
    private WriteableBitmap bitmap2;
    private volatile bool isRecording1 = false;
    private volatile bool isRecording2 = false;
    private bool isStreaming = true;

    private Thread? cameraStreamThread;
    private Dispatcher _dispatcher;
    private readonly MediaPlayer _player = new MediaPlayer();
    private readonly object _recordLock = new();

    private readonly ISerialSensorService _serialService;
    private readonly object[] _csvLocks = new object[] { new(), new(), new() };
    private readonly StreamWriter?[] _csvWriters = new StreamWriter?[3];
    private const int CsvTargetCount = 700;
    private readonly int[] _csvCounts = new int[3];

    // Prevent launching PoseTracking multiple times per recording session
    private int _poseTrackingLaunched; // 0 = not launched, 1 = launched

    // LED activity tracking
    private readonly DateTime[] _lastFrameUtc = new DateTime[3];
    private readonly TimeSpan _ledActiveTimeout = TimeSpan.FromMilliseconds(700);
    private DispatcherTimer? _ledTimer;

    [ObservableProperty] private WriteableBitmap? _cameraView1Source;
    [ObservableProperty] private WriteableBitmap? _cameraView2Source;
    [ObservableProperty] private bool _isSerialConnected;
    [ObservableProperty] private bool _isCsvLogging;

    // LED properties (true = ON, false = OFF)
    [ObservableProperty] private bool _device1Active;
    [ObservableProperty] private bool _device2Active;
    [ObservableProperty] private bool _device3Active;

    // Countdown overlay bindings
    [ObservableProperty] private bool _isCountdownVisible;
    [ObservableProperty] private string _countdownText = string.Empty;



    // ObservableCollection<ImageSource>
    [ObservableProperty] private ObservableCollection<HistoryItem> _historyItems = new();

    // Analyzing overlay state
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private string _analysisMessage = "Analyzing data... Please wait.";

    public HomeViewModel()
        : this(new SerialSensorService())
    {
    }

    public HomeViewModel(ISerialSensorService serialService)
    {
        _serialService = serialService;
        _serialService.FrameReceived += SerialService_FrameReceived;

        InitializeCamera();
        StartCameraStream();

        // Init last seen
        for (int i = 0; i < _lastFrameUtc.Length; i++) _lastFrameUtc[i] = DateTime.MinValue;

        // Start LED UI timer
        _dispatcher = Application.Current.Dispatcher;
        _ledTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, __) => UpdateLedStates(), _dispatcher);
        _ledTimer.Start();

        // Load fixed 6-history thumbnails
        LoadFixedHistory();
    }

    private void UpdateLedStates()
    {
        var now = DateTime.UtcNow;
        Device1Active = (now - _lastFrameUtc[0]) <= _ledActiveTimeout;
        Device2Active = (now - _lastFrameUtc[1]) <= _ledActiveTimeout;
        Device3Active = (now - _lastFrameUtc[2]) <= _ledActiveTimeout;
    }

    private void SerialService_FrameReceived(object? sender, SensorFrameEventArgs e)
    {
        // Mark activity for LED by sensor id (1..3)
        int idx = Math.Clamp(e.SensorId - 1, 0, 2);
        _lastFrameUtc[idx] = DateTime.UtcNow;

        // Convert raw shorts to real units
        float accX1 = e.Frame.aX1 * 32.0f / 32768.0f;
        float accY1 = e.Frame.aY1 * 32.0f / 32768.0f;
        float accZ1 = e.Frame.aZ1 * 32.0f / 32768.0f;
        float gyrX1 = e.Frame.gX1 * 4000.0f / 32768.0f;
        float gyrY1 = e.Frame.gY1 * 4000.0f / 32768.0f;
        float gyrZ1 = e.Frame.gZ1 * 4000.0f / 32768.0f;

        float accX2 = e.Frame.aX2 * 32.0f / 32768.0f;
        float accY2 = e.Frame.aY2 * 32.0f / 32768.0f;
        float accZ2 = e.Frame.aZ2 * 32.0f / 32768.0f;
        float gyrX2 = e.Frame.gX2 * 4000.0f / 32768.0f;
        float gyrY2 = e.Frame.gY2 * 4000.0f / 32768.0f;
        float gyrZ2 = e.Frame.gZ2 * 4000.0f / 32768.0f;

        if (!IsCsvLogging) return;

        var w = _csvWriters[idx];
        if (w is null) return;

        // Ensure each sensor writes at most 700 rows
        int newCount = Interlocked.Increment(ref _csvCounts[idx]);
        if (newCount > CsvTargetCount)
            return;

        var ts = DateTime.UtcNow;
        var line = string.Format(CultureInfo.InvariantCulture,
            "{0:O},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12}",
            ts, accX1, accY1, accZ1, gyrX1, gyrY1, gyrZ1, accX2, accY2, accZ2, gyrX2, gyrY2, gyrZ2);

        lock (_csvLocks[idx])
        {
            w.WriteLine(line);
        }

        // Stop when all three sensors have reached the target
        if (Volatile.Read(ref _csvCounts[0]) >= CsvTargetCount &&
            Volatile.Read(ref _csvCounts[1]) >= CsvTargetCount &&
            Volatile.Read(ref _csvCounts[2]) >= CsvTargetCount)
        {
            _dispatcher?.BeginInvoke(() =>
            {
                StopCsvLogging();   // flush and close CSVs
                StopRecording();    // stop cameras

                // Show analyzing overlay and launch PoseTracking once
                if (Interlocked.Exchange(ref _poseTrackingLaunched, 1) == 0)
                {
                    AnalysisMessage = "Analyzing data... Please wait.";
                    IsAnalyzing = true;
                    _ = RunPoseTrackingAsync();
                }
            });
        }
    }

    public void InitializeCamera()
    {
        capture1 = new VideoCapture(0);
        frame1 = new Mat();
        if (!capture1.IsOpened())
            MessageBox.Show("Webcam not detected!");

        capture2 = new VideoCapture(1);
        frame2 = new Mat();

        if (capture1.IsOpened())
        {
            bitmap1 = new WriteableBitmap(
                (int)capture1.FrameWidth,
                (int)capture1.FrameHeight,
                96, 96, PixelFormats.Bgr24, null);
            CameraView1Source = bitmap1;
        }

        if (capture2.IsOpened())
        {
            bitmap2 = new WriteableBitmap(
                (int)capture2.FrameWidth,
                (int)capture2.FrameHeight,
                96, 96, PixelFormats.Bgr24, null);
            CameraView2Source = bitmap2;
        }
    }

    public void StartCameraStream()
    {
        _dispatcher = Application.Current.Dispatcher;
        cameraStreamThread = new Thread(() =>
        {
            while (isStreaming)
            {
                if (capture1.IsOpened())
                {
                    capture1.Read(frame1);
                    if (isRecording1 && videoWriter1 is not null && !frame1.Empty())
                    {
                        lock (_recordLock) videoWriter1.Write(frame1);
                    }
                    if (CameraView1Source is not null)
                    {
                        _dispatcher.Invoke(() =>
                        {
                            bitmap1.Lock();
                            bitmap1.WritePixels(
                                new Int32Rect(0, 0, frame1.Width, frame1.Height),
                                frame1.Data,
                                (int)frame1.Step() * frame1.Rows,
                                (int)frame1.Step());
                            bitmap1.Unlock();
                        });
                    }
                }

                if (capture2.IsOpened())
                {
                    capture2.Read(frame2);
                    if (isRecording2 && videoWriter2 is not null && !frame2.Empty())
                    {
                        lock (_recordLock) videoWriter2.Write(frame2);
                    }
                    if (CameraView2Source is not null)
                    {
                        _dispatcher.Invoke(() =>
                        {
                            bitmap2.Lock();
                            bitmap2.WritePixels(
                                new Int32Rect(0, 0, frame2.Width, frame2.Height),
                                frame2.Data,
                                (int)frame2.Step() * frame2.Rows,
                                (int)frame2.Step());
                            bitmap2.Unlock();
                        });
                    }
                }

                Thread.Sleep(1);
            }
        })
        { IsBackground = true };
        cameraStreamThread.Start();
    }

    [RelayCommand]
    private void Connect()
    {
        if (IsSerialConnected) return;
        try
        {
            _serialService.Connect("COM29", "COM30", "COM31");
            IsSerialConnected = _serialService.IsConnected;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Connect failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async void StartRecord()
    {
        if (isRecording1 || isRecording2) return;

        if (!capture1.IsOpened() && !capture2.IsOpened())
        {
            MessageBox.Show("No camera opened to record.");
            return;
        }
        if (!IsSerialConnected)
        {
            MessageBox.Show("Serial is not connected.");
            return;
        }

        // Reset launch guard for a new session
        Interlocked.Exchange(ref _poseTrackingLaunched, 0);

        var soundTask = PlaySoundAndWaitAsync("assets/counter.mp3");
        var countdownTask = ShowCountdownAsync();
        await Task.WhenAll(soundTask, countdownTask);

        if (File.Exists("sensor1.csv"))
            File.Delete("sensor1.csv");
        if (File.Exists("sensor2.csv"))
            File.Delete("sensor2.csv");
        if (File.Exists("sensor3.csv"))
            File.Delete("sensor3.csv");

        // Start video writers
        OpenWriters();
        isRecording1 = capture1.IsOpened() && videoWriter1 is not null && videoWriter1.IsOpened();
        isRecording2 = capture2.IsOpened() && videoWriter2 is not null && videoWriter2.IsOpened();

        // Start CSV logging for 700 samples per sensor
        Array.Clear(_csvCounts, 0, _csvCounts.Length);
        OpenCsvWriters();
        IsCsvLogging = true;

        await Task.CompletedTask;

        // PoseTracking now runs after recording stops (see SerialService_FrameReceived).
    }

    private Task PlaySoundAndWaitAsync(string resourcePath)
    {
        var tcs = new TaskCompletionSource<bool>();
        _player.Stop();
        _player.Position = TimeSpan.Zero;
        _player.Volume = 1.0;
        void Handler(object? s, EventArgs e)
        {
            _player.MediaEnded -= Handler;
            tcs.TrySetResult(true);
        }
        _player.MediaEnded += Handler;
        var uri = new Uri($"{resourcePath}", UriKind.Relative);
        _player.Open(uri);
        _player.Play();
        return tcs.Task;
    }

    private void OpenWriters()
    {
        lock (_recordLock)
        {
            if (capture1.IsOpened())
            {
                var size1 = new OpenCvSharp.Size((int)capture1.FrameWidth, (int)capture1.FrameHeight);
                double fps1 = capture1.Fps; if (fps1 <= 0 || double.IsNaN(fps1) || double.IsInfinity(fps1)) fps1 = 30;
                videoWriter1?.Release(); videoWriter1?.Dispose();
                videoWriter1 = new VideoWriter("video1.avi", FourCC.MJPG, fps1, size1, true);
            }
            if (capture2.IsOpened())
            {
                var size2 = new OpenCvSharp.Size((int)capture2.FrameWidth, (int)capture2.FrameHeight);
                double fps2 = capture2.Fps; if (fps2 <= 0 || double.IsNaN(fps2) || double.IsInfinity(fps2)) fps2 = 30;
                videoWriter2?.Release(); videoWriter2?.Dispose();
                videoWriter2 = new VideoWriter("video2.avi", FourCC.MJPG, fps2, size2, true);
            }
        }
    }

    private void StopRecording()
    {
        lock (_recordLock)
        {
            isRecording1 = false;
            isRecording2 = false;
            videoWriter1?.Release(); videoWriter1?.Dispose(); videoWriter1 = null;
            videoWriter2?.Release(); videoWriter2?.Dispose(); videoWriter2 = null;
        }
    }

    private void StopCsvLogging()
    {
        if (!IsCsvLogging) return;
        IsCsvLogging = false;
        CloseCsvWriters();
    }

    private void OpenCsvWriters()
    {
        for (int i = 0; i < 3; i++)
        {
            string path = $"sensor{i + 1}.csv";
            bool writeHeader = !File.Exists(path);
            _csvWriters[i] = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (writeHeader)
            {
                _csvWriters[i]!.WriteLine("timestamp,accX1,accY1,accZ1,gyrX1,gyrY1,gyrZ1,accX2,accY2,accZ2,gyrX2,gyrY2,gyrZ2");
            }
        }
    }

    private void CloseCsvWriters()
    {
        for (int i = 0; i < _csvWriters.Length; i++)
        {
            try { _csvWriters[i]?.Flush(); _csvWriters[i]?.Dispose(); }
            catch { /* ignore */ }
            finally { _csvWriters[i] = null; }
        }
    }

    // Load exactly the 6 fixed videos from History and create thumbnails
    private void LoadFixedHistory()
    {
        try
        {
            HistoryItems.Clear();

            string historyRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "History");
            if (!Directory.Exists(historyRoot)) return;

            string[] targets =
            {
                "video1_h1.avi",
                "video1_h2.avi",
                "video1_h3.avi",
                "video1_h4.avi",
                "video1_h5.avi",
                "video1_h6.avi"
            };

            foreach (var fileName in targets)
            {
                string path = Path.Combine(historyRoot, fileName);
                if (!File.Exists(path))
                {
                    var found = Directory.GetFiles(historyRoot, fileName, SearchOption.AllDirectories).FirstOrDefault();
                    if (string.IsNullOrEmpty(found))
                        continue;
                    path = found;
                }

                var thumb = TryCreateVideoThumbnail(path);
                if (thumb is not null)
                {
                    var ts = File.GetLastWriteTimeUtc(path);
                    HistoryItems.Add(new HistoryItem(thumb, ts));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LoadFixedHistory failed: {ex}");
        }
    }

    private async Task ShowCountdownAsync()
    {
        try
        {
            await Task.Delay(1000);
            await _dispatcher.InvokeAsync(() =>
            {
                CountdownText = "3";
                IsCountdownVisible = true;
            });
            await Task.Delay(700);

            await _dispatcher.InvokeAsync(() => CountdownText = "2");
            await Task.Delay(1000);

            await _dispatcher.InvokeAsync(() => CountdownText = "1");
            await Task.Delay(1000);

            await _dispatcher.InvokeAsync(() => CountdownText = "PLAY");
            await Task.Delay(700);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                IsCountdownVisible = false;
                CountdownText = string.Empty;
            });
        }
    }

    private ImageSource? TryCreateVideoThumbnail(string videoPath)
    {
        try
        {
            using var cap = new VideoCapture(videoPath);
            if (!cap.IsOpened()) return null;

            // Seek near 1s to avoid black first frame when possible
            double fps = cap.Fps;
            if (fps > 0 && double.IsFinite(fps))
            {
                long frameIndex = (long)Math.Min(Math.Max(0, fps), Math.Max(0, cap.FrameCount - 1));
                cap.Set(VideoCaptureProperties.PosFrames, frameIndex);
            }

            using var frame = new Mat();
            if (!cap.Read(frame) || frame.Empty()) return null;

            Cv2.ImEncode(".png", frame, out var bytes);
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private async Task RunPoseTrackingAsync()
    {
        // Ensure overlay is visible even if invoked directly
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            AnalysisMessage = "Analyzing data... Please wait.";
            IsAnalyzing = true;
        });

        try
        {
            string? scriptPath = GetPoseTrackingScriptPath();
            if (scriptPath is null)
            {
                MessageBox.Show("PoseTracking.py not found. Ensure the file exists under the PoseTracking folder or is copied to the output directory.");
                return;
            }

            string fileName;
            string arguments = $"\"{scriptPath}\"";

            // Prefer a venv interpreter next to the script or at the repo root
            var venvPython = GetPythonInterpreterPath(scriptPath);
            if (venvPython is not null)
            {
                fileName = venvPython;
            }
            else if (IsCommandAvailable("python"))
            {
                fileName = "python";
            }
            else if (IsCommandAvailable("py"))
            {
                fileName = "py";
                arguments = $"-3 {arguments}";
            }
            else
            {
                MessageBox.Show("Python interpreter not found. Install Python or create/keep a venv under PoseTracking.");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!
            };

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            proc.OutputDataReceived += (s, e) => { if (e.Data is not null) Debug.WriteLine("[PoseTracking] " + e.Data); };
            proc.ErrorDataReceived += (s, e) => { if (e.Data is not null) Debug.WriteLine("[PoseTracking][ERR] " + e.Data); };
            proc.Exited += (s, e) => tcs.TrySetResult(proc.ExitCode);

            if (!proc.Start())
            {
                MessageBox.Show("Failed to start PoseTracking process.");
                return;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            int exitCode = await tcs.Task.ConfigureAwait(false);
            Debug.WriteLine($"PoseTracking finished with exit code {exitCode}");

            // After Python finishes, update History
            try
            {
                RotateAndCopyHistory();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RotateAndCopyHistory failed: {ex}");
            }

            // Now run AI validation to compute score BEFORE navigating
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                AnalysisMessage = "Scoring swing similarity...";
                IsAnalyzing = true;
            });
            await RunAiValidationAndPublishAsync();

            // Hide overlay and navigate to Dashboard after everything completes
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                IsAnalyzing = false;
                NavigateToDashboard();
            });
        }
        catch (Exception ex)

        {
            Debug.WriteLine($"RunPoseTrackingAsync failed: {ex}");
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                IsAnalyzing = false;
                MessageBox.Show($"PoseTracking failed: {ex.Message}");
            });
        }
        finally
        {
            // Safety: ensure overlay is hidden in all paths (NavigateToDashboard path resets it too)
            Application.Current?.Dispatcher?.BeginInvoke(() => IsAnalyzing = false);
        }
    }

    // Run AIValidation.py and broadcast result so Dashboard can update immediately
    private async Task RunAiValidationAndPublishAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await AiValidationService.RunAsync(ct);
            if (result is not null)
            {
                AiScoreStore.LastBestPercent = result.BestPercent;
                WeakReferenceMessenger.Default.Send(new AiScoreUpdatedMessage(result.BestPercent));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"AI validation failed: {ex}");
        }
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(); } catch { }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetPoseTrackingScriptPath()
    {
        const string scriptName = "PoseTracking.py";

        // 1) Try output folder: ./PoseTracking/PoseTracking.py
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string candidate = Path.Combine(baseDir, "PoseTracking", scriptName);
        if (File.Exists(candidate)) return candidate;

        // 2) Walk up a few levels to locate repository root then PoseTracking folder
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 5 && dir.Parent is not null; i++)
        {
            dir = dir.Parent;
            candidate = Path.Combine(dir.FullName, "PoseTracking", scriptName);
            if (File.Exists(candidate)) return candidate;
        }

        // 3) Fallback: search under baseDir
        var found = Directory.GetFiles(baseDir, scriptName, SearchOption.AllDirectories)
                             .FirstOrDefault(p => p.EndsWith(Path.Combine("PoseTracking", scriptName), StringComparison.OrdinalIgnoreCase));
        return found;
    }

    private static string? GetPythonInterpreterPath(string scriptPath)
    {
        // Try common venv folder names next to the script and up to a few parent folders
        string scriptDir = Path.GetDirectoryName(scriptPath)!;
        string[] venvNames = new[] { ".venv", "venv", "env", ".env" };

        static IEnumerable<string> Candidates(string root, string venvName)
        {
            yield return Path.Combine(root, venvName, "Scripts", "python.exe"); // Windows
            yield return Path.Combine(root, venvName, "bin", "python3");        // Linux/macOS
            yield return Path.Combine(root, venvName, "bin", "python");         // Linux/macOS
        }

        // Look in script folder first
        foreach (var name in venvNames)
        {
            foreach (var c in Candidates(scriptDir, name))
                if (File.Exists(c)) return c;
        }

        // Then walk up parents (e.g., solution root)
        var dir = new DirectoryInfo(scriptDir).Parent;
        for (int depth = 0; depth < 5 && dir is not null; depth++, dir = dir.Parent)
        {
            foreach (var name in venvNames)
            {
                foreach (var c in Candidates(dir.FullName, name))
                    if (File.Exists(c)) return c;
            }
        }

        return null;
    }

    private static string GetHistoryRoot()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "History");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Delete failed: {path} -> {ex.Message}");
        }
    }

    private void RotateAndCopyHistory()
    {
        string historyRoot = GetHistoryRoot();
        Directory.CreateDirectory(historyRoot);

        // Define prefixes and extensions in order
        string[] prefixes = { "outputvideo1", "outputvideo2", "video1", "video2", "sensor1", "sensor2", "sensor3" };
        string[] exts = { ".avi", ".avi", ".avi", ".avi", ".csv", ".csv", ".csv" };

        // 1) Delete *_h6
        for (int i = 0; i < prefixes.Length; i++)
        {
            string path = Path.Combine(historyRoot, $"{prefixes[i]}_h6{exts[i]}");
            TryDelete(path);
        }

        // 2) Move h5..h1 to h6..h2 (descending to avoid overwrite)
        for (int h = 5; h >= 1; h--)
        {
            for (int i = 0; i < prefixes.Length; i++)
            {
                string src = Path.Combine(historyRoot, $"{prefixes[i]}_h{h}{exts[i]}");
                string dst = Path.Combine(historyRoot, $"{prefixes[i]}_h{h + 1}{exts[i]}");
                try
                {
                    if (File.Exists(src))
                    {
                        if (File.Exists(dst)) TryDelete(dst);
                        File.Move(src, dst);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Move failed: {src} -> {dst} : {ex.Message}");
                }
            }
        }

        // 3) Copy current files to *_h1
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] currentFiles = { "outputvideo1.avi", "outputvideo2.avi", "video1.avi", "video2.avi", "sensor1.csv", "sensor2.csv", "sensor3.csv" };

        for (int i = 0; i < prefixes.Length; i++)
        {
            string src = Path.Combine(baseDir, currentFiles[i]);
            string dst = Path.Combine(historyRoot, $"{prefixes[i]}_h1{exts[i]}");
            try
            {
                if (File.Exists(src))
                {
                    File.Copy(src, dst, overwrite: true);
                }
                else
                {
                    Debug.WriteLine($"Source not found (skipped): {src}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Copy failed: {src} -> {dst} : {ex.Message}");
            }
        }

        // 4) Reload thumbnails on UI thread
        (_dispatcher ?? Application.Current.Dispatcher)?.BeginInvoke(() => LoadFixedHistory());
    }

    [RelayCommand]
    private async Task PlayHistory(object? param)
    {
        // param may be int or string depending on converter; normalize it.
        int hIndex = TryParseIndex(param);
        if (hIndex < 1 || hIndex > 6) return;

        var disp = _dispatcher ?? Application.Current.Dispatcher;

        // Show analyzing overlay immediately
        disp?.Invoke(() =>
        {
            AnalysisMessage = "Loading selected history...";
            IsAnalyzing = true;
        });

        try
        {
            // Copy the selected history set to root in background
            await Task.Run(() => CopyHistorySetToRoot(hIndex));

            // Run AI scoring before navigating
            disp?.Invoke(() => AnalysisMessage = "Scoring swing similarity...");
            await RunAiValidationAndPublishAsync();

            // Navigate after scoring completes
            disp?.BeginInvoke(NavigateToDashboard);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PlayHistory failed: {ex}");
            disp?.BeginInvoke(() =>
            {
                MessageBox.Show($"Failed to load history set h{hIndex}: {ex.Message}");
            });
        }
        finally
        {
            // Ensure overlay is hidden
            disp?.BeginInvoke(() => IsAnalyzing = false);
        }
    }

    private static int TryParseIndex(object? param)
    {
        if (param is int i) return i;
        if (param is string s && int.TryParse(s, out var v)) return v;
        return -1;
    }

    private void CopyHistorySetToRoot(int hIndex)
    {
        string historyRoot = GetHistoryRoot();
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // Files set to copy: remove the _h{index} suffix on destination
        string[] prefixes = { "outputvideo1", "outputvideo2", "video1", "video2", "sensor1", "sensor2", "sensor3" };
        string[] exts = { ".avi", ".avi", ".avi", ".avi", ".csv", ".csv", ".csv" };

        for (int i = 0; i < prefixes.Length; i++)
        {
            string src = Path.Combine(historyRoot, $"{prefixes[i]}_h{hIndex}{exts[i]}");
            string dst = Path.Combine(baseDir, $"{prefixes[i]}{exts[i]}");

            try
            {
                if (File.Exists(src))
                {
                    File.Copy(src, dst, overwrite: true);
                    Debug.WriteLine($"Copied: {src} -> {dst}");
                }
                else
                {
                    Debug.WriteLine($"History source missing (skipped): {src}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Copy history set failed: {src} -> {dst} : {ex.Message}");
            }
        }
    }

    private void NavigateToDashboard()
    {
        try
        {
            var main = Application.Current?.MainWindow?.DataContext as MainViewModel;
            if (main is null) return;

            // Switch to Dashboard
            main.ShowDashboardCommand.Execute(null);

            // Immediately load latest CSVs/videos and refresh plots
            if (main.CurrentViewModel is DashboardViewModel dash)
            {
                dash.PlayCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"NavigateToDashboard failed: {ex}");
        }
    }

    public void Dispose()
    {
        isStreaming = false;
        try { cameraStreamThread?.Join(100); } catch { }
        capture1?.Release();
        capture2?.Release();
        videoWriter1?.Release();
        videoWriter2?.Release();
        _serialService.Dispose();
        CloseCsvWriters();
        _ledTimer?.Stop(); // stop LED timer
    }
}