using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GolfAnalyzer.Services;

public static class AiValidationService
{
    public sealed record Result(double BestPercent, double BestCos, string Decision, string? BestKey, string? Error);

    public static async Task<Result?> RunAsync(CancellationToken ct = default)
    {
        var bin = AppContext.BaseDirectory;
        var root = FindSolutionRoot(bin);
        var aiPy = Path.Combine(root, "AIValidation", "AIValidation.py");
        if (!File.Exists(aiPy)) { Debug.WriteLine("AIValidation.py not found."); return null; }

        var pythonExe = await ResolvePythonExeAsync(root, ct);
        if (pythonExe is null)
        {
            Debug.WriteLine("No suitable Python interpreter found (torch missing?).");
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"\"{aiPy}\"",
            WorkingDirectory = Path.GetDirectoryName(aiPy)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Make stdout predictable
        psi.Environment.TryAdd("PYTHONIOENCODING", "utf-8");
        psi.Environment.TryAdd("PYTHONUNBUFFERED", "1");
        // In case relative imports are sensitive
        psi.Environment.TryAdd("PYTHONPATH", psi.WorkingDirectory);

        using var proc = new Process { StartInfo = psi };
        Debug.WriteLine($"[AIValidation] Using Python: {pythonExe}");
        Debug.WriteLine($"[AIValidation] CWD: {psi.WorkingDirectory}");

        proc.Start();

        // Read all output
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        await Task.WhenAll(proc.WaitForExitAsync(ct), stdoutTask, stderrTask);
        string stdout = stdoutTask.Result ?? string.Empty;
        string stderr = stderrTask.Result ?? string.Empty;

        Debug.WriteLine("[AIValidation stdout]");
        Debug.WriteLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Debug.WriteLine("[AIValidation stderr]");
            Debug.WriteLine(stderr);
        }

        // Look for the tagged JSON line
        const string tag = "__AIRESULT__";
        int idx = stdout.LastIndexOf(tag, StringComparison.Ordinal);
        if (idx < 0)
        {
            // As a fallback, try stderr (in case script printed there)
            idx = stderr.LastIndexOf(tag, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var jsonErr = stderr[(idx + tag.Length)..].Trim();
                if (TryParseResult(jsonErr, out var resFromErr)) return resFromErr;
            }
            Debug.WriteLine("Tagged JSON line not found. Ensure AIValidation.py prints __AIRESULT__ JSON.");
            return null;
        }

        string json = stdout[(idx + tag.Length)..].Trim();
        if (TryParseResult(json, out var res)) return res;

        Debug.WriteLine("Failed to parse AIValidation JSON result.");
        return null;
    }

    private static bool TryParseResult(string json, out Result? result)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            double bestPct = r.TryGetProperty("best_pct", out var bp) ? bp.GetDouble() : 0.0;
            double bestCos = r.TryGetProperty("best_cos", out var bc) ? bc.GetDouble() : -1.0;
            string decision = r.TryGetProperty("decision", out var d) ? (d.GetString() ?? "") : "";
            string? bestKey = r.TryGetProperty("best_key", out var bk) ? bk.GetString() : null;
            string? error = r.TryGetProperty("error", out var er) ? er.GetString() : null;
            result = new Result(bestPct, bestCos, decision, bestKey, error);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to parse AIValidation result: {ex}");
            result = null;
            return false;
        }
    }

    private static async Task<string?> ResolvePythonExeAsync(string root, CancellationToken ct)
    {
        // Candidate interpreters: prefer a venv that can import torch
        var candidates = new List<string>();

        // 1) AIValidation/env
        var aiVenv = Path.Combine(root, "AIValidation", "env", "Scripts", "python.exe");
        if (File.Exists(aiVenv)) candidates.Add(aiVenv);

        // 2) PoseTracking/env
        var poseVenv = Path.Combine(root, "PoseTracking", "env", "Scripts", "python.exe");
        if (File.Exists(poseVenv)) candidates.Add(poseVenv);

        // 3) System PATH
        candidates.AddRange(new[] { "python.exe", "python", "py" });

        foreach (var exe in candidates)
        {
            if (await SupportsTorchAsync(exe, ct))
            {
                return exe;
            }
            else
            {
                Debug.WriteLine($"[AIValidation] Skip interpreter (torch missing): {exe}");
            }
        }
        return null;
    }

    private static async Task<bool> SupportsTorchAsync(string exe, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-c \"import sys; ok=1\ntry:\n import torch, numpy\nexcept Exception as e:\n ok=0\nsys.stdout.write('OK' if ok else 'FAIL')\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment.TryAdd("PYTHONIOENCODING", "utf-8");
            using var p = Process.Start(psi);
            if (p is null) return false;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync(ct);
            return stdout.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindOnPath(IEnumerable<string> names)
    {
        foreach (var n in names)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = n,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p is null) continue;
                p.WaitForExit(3000);
                if (p.ExitCode == 0) return n;
            }
            catch { /* ignore */ }
        }
        return null;
    }

    private static string FindSolutionRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "AIValidation")) &&
                Directory.Exists(Path.Combine(dir.FullName, "GolfAnalyzer")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        return AppContext.BaseDirectory;
    }
}