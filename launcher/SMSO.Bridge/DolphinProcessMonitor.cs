using System;
using System.Diagnostics;
using System.IO;

namespace SMSO.Bridge;

public sealed class DolphinProcessMonitor : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private bool _wasRunning;
    private string? _expectedDolphinPath;
    private int? _trackedProcessId;

    public event Action? DolphinStarted;
    public event Action? DolphinStopped;
    public event Action<string>? Log;

    public bool IsDolphinRunning { get; private set; }
    public int? TrackedProcessId => _trackedProcessId;

    public DolphinProcessMonitor()
    {
        _timer = new System.Timers.Timer(500);
        _timer.Elapsed += (_, _) => Poll();
        _timer.AutoReset = true;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public void RegisterLaunchedProcess(int processId)
    {
        _trackedProcessId = processId;
        _wasRunning = false;
        _timer.Interval = 250;
        Poll();
    }

    public void ClearTrackedProcess()
    {
        _trackedProcessId = null;
        _wasRunning = false;
        IsDolphinRunning = false;
        _timer.Interval = 500;
    }

    private void Poll()
    {
        try
        {
            var running = IsTrackedDolphinRunning();
            IsDolphinRunning = running;
            _timer.Interval = _trackedProcessId.HasValue && !running ? 250 : 500;

            if (running == _wasRunning)
                return;

            _wasRunning = running;
            if (running)
            {
                _timer.Interval = 500;
                Log?.Invoke($"Dolphin started (PID {_trackedProcessId})");
                SafeRaise(DolphinStarted);
            }
            else
            {
                if (_trackedProcessId.HasValue)
                    Log?.Invoke("Dolphin stopped");
                SafeRaise(DolphinStopped);
                _trackedProcessId = null;
                _timer.Interval = 500;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Dolphin monitor error: {ex.Message}");
        }
    }

    private static void SafeRaise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Dolphin monitor handler error: {ex.Message}");
        }
    }

    private bool IsTrackedDolphinRunning()
    {
        if (!_trackedProcessId.HasValue)
            return false;

        try
        {
            using var proc = Process.GetProcessById(_trackedProcessId.Value);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Launches Dolphin for this launcher instance only. Other running Dolphin processes are ignored.
    /// </summary>
    public static bool TryLaunchDolphin(string dolphinPath, string? isoPath, out int processId, out string? error)
    {
        processId = 0;
        error = null;
        try
        {
            dolphinPath = Path.GetFullPath(dolphinPath.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            error = $"Invalid Dolphin path: {ex.Message}";
            return false;
        }

        if (!File.Exists(dolphinPath))
        {
            error = $"Dolphin executable not found: {dolphinPath}";
            return false;
        }

        string? arguments = null;
        if (!string.IsNullOrWhiteSpace(isoPath))
        {
            try
            {
                isoPath = Path.GetFullPath(isoPath.Trim().Trim('"'));
                if (File.Exists(isoPath))
                    arguments = $"-e \"{isoPath}\"";
            }
            catch (Exception ex)
            {
                error = $"Invalid ISO path: {ex.Message}";
                return false;
            }
        }

        var workDir = Path.GetDirectoryName(dolphinPath);
        if (string.IsNullOrEmpty(workDir))
        {
            error = "Could not determine Dolphin install directory.";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = dolphinPath,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                Arguments = arguments ?? string.Empty,
            };

            var proc = Process.Start(psi);
            if (proc == null)
            {
                error = "Process.Start returned null.";
                return false;
            }

            processId = proc.Id;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void SetExpectedDolphinPath(string path)
    {
        try
        {
            _expectedDolphinPath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            _expectedDolphinPath = path;
        }
    }

    public void Dispose() => _timer.Dispose();
}
