using System.Runtime.InteropServices;
using System.Text;

namespace ScreenSorter.Infrastructure.Win32;

/// <summary>Win32 declarations for focus tracking.</summary>
/// <remarks>
/// Classic <c>DllImport</c> is used because <c>SetWinEventHook</c> takes a delegate callback,
/// which the <c>LibraryImport</c> generator does not marshal.
/// </remarks>
internal static class NativeMethods
{
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const uint WM_QUIT = 0x0012;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public delegate void WinEventProc(nint hWinEventHook, uint eventType, nint hwnd, int idObject,
        int idChild, uint idEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint hObject);

    /// <summary>Reads a window title; empty if the window is gone.</summary>
    public static string WindowTitle(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return "";
        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Returns the full exe path of the process that owns <paramref name="hwnd"/>, or empty.</summary>
    public static string ExePath(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
            return "";
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0)
            return "";
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            return QueryFullProcessImageName(process, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
