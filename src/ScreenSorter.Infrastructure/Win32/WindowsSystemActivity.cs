using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ScreenSorter.Core.Idle;
using static ScreenSorter.Infrastructure.Win32.IdleNativeMethods;

namespace ScreenSorter.Infrastructure.Win32;

/// <summary>
/// <see cref="ISystemActivity"/> for Windows: <c>GetLastInputInfo</c>, fullscreen detection
/// (<c>SHQueryUserNotificationState</c> plus "foreground window covers its monitor"), and GPU load from
/// NVML with <c>nvidia-smi</c> as fallback. Thread-safe.
/// </summary>
public sealed class WindowsSystemActivity(ILogger<WindowsSystemActivity> logger) : ISystemActivity, IDisposable
{
    private static readonly TimeSpan SmiTimeout = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private nint[]? _nvmlDevices;
    private bool _nvmlFailed;
    private bool _smiFailed;

    /// <inheritdoc />
    public TimeSpan TimeSinceLastInput()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;
        // Both are 32-bit tick counts; unsigned subtraction stays correct across the 49.7-day wrap.
        return TimeSpan.FromMilliseconds(unchecked(GetTickCount() - info.dwTime));
    }

    /// <inheritdoc />
    public bool IsFullscreenAppFocused()
    {
        if (SHQueryUserNotificationState(out var state) == 0
            && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
            return true;

        // Borderless-windowed games don't always set the notification state.
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || hwnd == GetShellWindow() || hwnd == GetDesktopWindow() || IsDesktopClass(hwnd))
            return false;
        if (!GetWindowRect(hwnd, out var window))
            return false;
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info))
            return false;
        var m = info.rcMonitor;
        return window.Left <= m.Left && window.Top <= m.Top && window.Right >= m.Right && window.Bottom >= m.Bottom;
    }

    /// <inheritdoc />
    public int? GpuUtilizationPercent()
    {
        lock (_gate)
        {
            return ReadNvml() ?? ReadNvidiaSmi();
        }
    }

    /// <summary>Shuts NVML down if it was initialized.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_nvmlDevices is not null)
                NvmlNativeMethods.Shutdown();
            _nvmlDevices = null;
        }
    }

    private int? ReadNvml()
    {
        if (_nvmlFailed)
            return null;
        try
        {
            if (_nvmlDevices is null)
            {
                Check(NvmlNativeMethods.Init(), "nvmlInit_v2");
                Check(NvmlNativeMethods.DeviceGetCount(out var count), "nvmlDeviceGetCount_v2");
                var devices = new nint[count];
                for (uint i = 0; i < count; i++)
                    Check(NvmlNativeMethods.DeviceGetHandleByIndex(i, out devices[i]), "nvmlDeviceGetHandleByIndex_v2");
                _nvmlDevices = devices;
            }

            var max = 0;
            foreach (var device in _nvmlDevices)
            {
                Check(NvmlNativeMethods.DeviceGetUtilizationRates(device, out var u), "nvmlDeviceGetUtilizationRates");
                max = Math.Max(max, (int)u.Gpu);
            }
            return max;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            _nvmlFailed = true;
            logger.LogWarning(ex, "NVML unavailable; falling back to nvidia-smi");
            return null;
        }
    }

    private int? ReadNvidiaSmi()
    {
        if (_smiFailed)
            return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(SmiTimeout) || p.ExitCode != 0)
                throw new InvalidOperationException($"nvidia-smi exit code {(p.HasExited ? p.ExitCode : -1)}");
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => int.Parse(l, CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0)
                .Max();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FormatException)
        {
            _smiFailed = true;
            logger.LogWarning(ex, "nvidia-smi unavailable; GPU load is ignored by the idle check");
            return null;
        }
    }

    private static bool IsDesktopClass(nint hwnd)
    {
        var buffer = new char[64];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        var name = new string(buffer, 0, Math.Max(0, length));
        return name is "Progman" or "WorkerW";
    }

    private static void Check(int result, string call)
    {
        if (result != NvmlNativeMethods.NVML_SUCCESS)
            throw new InvalidOperationException($"{call} failed with NVML code {result}");
    }
}
