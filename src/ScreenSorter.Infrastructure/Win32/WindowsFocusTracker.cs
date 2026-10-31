using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenSorter.Core.Focus;
using ScreenSorter.Core.Models;

namespace ScreenSorter.Infrastructure.Win32;

/// <summary>
/// Feeds <see cref="FocusHistory"/> from <c>EVENT_SYSTEM_FOREGROUND</c>. The hook runs on a dedicated
/// thread with its own message loop, so it does not depend on the UI thread.
/// </summary>
/// <remarks>
/// Browser tab switches do not change the foreground window, so the title recorded when a window
/// gained focus can be stale. On every focus change the previous window's title is re-read and,
/// if it changed, recorded again just before the new window. That captures the title at the moment
/// the snipping overlay takes focus.
/// </remarks>
public sealed class WindowsFocusTracker(FocusHistory history, TimeProvider time, ILogger<WindowsFocusTracker> logger)
    : IHostedService
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NativeMethods.WinEventProc? _callback;
    private Thread? _thread;
    private uint _threadId;
    private FocusSnapshot? _last;

    /// <summary>Starts the hook thread and records the current foreground window.</summary>
    /// <param name="cancellationToken">Ignored; startup is fast.</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "FocusTracker" };
        _thread.Start();
        return _started.Task;
    }

    /// <summary>Stops the message loop and removes the hook.</summary>
    /// <param name="cancellationToken">Ignored.</param>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_threadId != 0)
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, 0, 0);
        _thread?.Join(TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    private void Run()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        // Kept in a field: the GC must not collect the delegate while native code holds it.
        _callback = OnForeground;
        var hook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            0, _callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        if (hook == 0)
        {
            logger.LogError("SetWinEventHook failed; focus-based rules are disabled");
            _started.TrySetResult();
            return;
        }

        Capture(NativeMethods.GetForegroundWindow());
        _started.TrySetResult();
        logger.LogInformation("Focus tracking started");

        while (NativeMethods.GetMessage(out _, 0, 0, 0) > 0)
        {
            // WinEvent callbacks are dispatched inside GetMessage; nothing else to pump.
        }

        NativeMethods.UnhookWinEvent(hook);
    }

    private void OnForeground(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint ms)
    {
        try
        {
            if (_last is { WindowHandle: not 0 } last && NativeMethods.IsWindow(last.WindowHandle))
            {
                var title = NativeMethods.WindowTitle(last.WindowHandle);
                if (title != last.WindowTitle)
                    history.Record(last with { Timestamp = time.GetUtcNow(), WindowTitle = title });
            }
            Capture(hwnd);
        }
        catch (Exception ex)
        {
            // An exception must never escape into native code.
            logger.LogWarning(ex, "Focus event failed");
        }
    }

    private void Capture(nint hwnd)
    {
        if (hwnd == 0)
            return;
        var exe = NativeMethods.ExePath(hwnd);
        var snapshot = new FocusSnapshot(time.GetUtcNow(), Path.GetFileName(exe), exe, NativeMethods.WindowTitle(hwnd), hwnd);
        history.Record(snapshot);
        _last = snapshot;
        logger.LogDebug("Focus: {Process} \"{Title}\"", snapshot.ProcessName, snapshot.WindowTitle);
    }
}
