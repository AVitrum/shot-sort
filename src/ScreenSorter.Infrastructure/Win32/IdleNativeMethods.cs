using System.Runtime.InteropServices;

namespace ScreenSorter.Infrastructure.Win32;

/// <summary>Win32 declarations for idle detection.</summary>
internal static partial class IdleNativeMethods
{
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary><c>QUERY_USER_NOTIFICATION_STATE</c> values that mean "a fullscreen app is running".</summary>
    public const int QUNS_BUSY = 2;
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    public const int QUNS_PRESENTATION_MODE = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetTickCount();

    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out int pquns);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(nint hWnd, [Out] char[] lpClassName, int nMaxCount);
}

/// <summary>NVML declarations (NVIDIA Management Library, <c>nvml.dll</c> ships with the driver).</summary>
internal static partial class NvmlNativeMethods
{
    public const int NVML_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [LibraryImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    public static partial int Init();

    [LibraryImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    public static partial int Shutdown();

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    public static partial int DeviceGetCount(out uint count);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static partial int DeviceGetHandleByIndex(uint index, out nint device);

    [LibraryImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    public static partial int DeviceGetUtilizationRates(nint device, out NvmlUtilization utilization);
}
