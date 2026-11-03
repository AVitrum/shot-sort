using Microsoft.Extensions.Logging;
using ScreenSorter.Core.Idle;
using static ScreenSorter.Infrastructure.Win32.IdleNativeMethods;

namespace ScreenSorter.Infrastructure.Win32;

/// <summary>
/// <see cref="ISystemActivity"/> for Windows: <c>GetLastInputInfo</c>, fullscreen detection
/// (<c>SHQueryUserNotificationState</c> plus "foreground window covers its monitor"), and GPU load from
/// NVML where the NVIDIA driver provides it, with the Windows GPU counters for every other case. Thread-safe.
/// </summary>
public sealed class WindowsSystemActivity : ISystemActivity, IDisposable
{
    private static readonly TimeSpan NvmlRetry = TimeSpan.FromMinutes(5);
    private readonly Lock _gate = new();
    private readonly RecoveringGpuReader _nvml;
    private readonly PdhGpuLoad _counters;
    private nint[]? _nvmlDevices;
    private bool _nvmlStarted;

    /// <summary>Creates the reader. Nothing is loaded until the first GPU reading.</summary>
    /// <param name="time">The clock for retrying NVML.</param>
    /// <param name="logger">Receives one line per GPU reading problem.</param>
    public WindowsSystemActivity(TimeProvider time, ILogger<WindowsSystemActivity> logger)
    {
        _nvml = new RecoveringGpuReader(ReadNvml, ShutDownNvml, NvmlRetry, time, logger);
        _counters = new PdhGpuLoad(logger);
    }

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
            return _nvml.Read() ?? _counters.Read();
        }
    }

    /// <summary>Shuts NVML down if it was initialized.</summary>
    public void Dispose()
    {
        lock (_gate)
            ShutDownNvml();
    }

    private int ReadNvml()
    {
        if (_nvmlDevices is null)
        {
            Check(NvmlNativeMethods.Init(), "nvmlInit_v2");
            _nvmlStarted = true;
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

    private void ShutDownNvml()
    {
        if (_nvmlStarted)
            NvmlNativeMethods.Shutdown();
        _nvmlStarted = false;
        _nvmlDevices = null;
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
