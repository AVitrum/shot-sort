using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;

namespace ScreenSorter.Core.Idle;

/// <summary>Raw system readings the idle policy is based on.</summary>
public interface ISystemActivity
{
    /// <summary>Time since the last keyboard or mouse input in this session.</summary>
    TimeSpan TimeSinceLastInput();

    /// <summary>Whether the focused window is a fullscreen app (game, video, presentation).</summary>
    bool IsFullscreenAppFocused();

    /// <summary>Highest utilization across NVIDIA GPUs, in percent.</summary>
    /// <returns>The value, or <see langword="null"/> when no reading is available (no NVIDIA driver).</returns>
    int? GpuUtilizationPercent();
}

/// <summary>Result of an idle check.</summary>
/// <param name="IsIdle">Whether the vision model may run now.</param>
/// <param name="Reason">Why not, for logs and the tray tooltip; empty when idle.</param>
public sealed record IdleStatus(bool IsIdle, string Reason);

/// <summary>Decides whether the system is idle enough to run the vision model (plan section 4, "Idle detection").</summary>
public sealed class IdleDetector(ISystemActivity activity, IOptions<IdleOptions> options)
{
    /// <summary>Checks all idle conditions. Cheap conditions are checked first.</summary>
    /// <param name="ignoreGpu">
    /// Skip the GPU condition, used while a batch is running: the model's own inference keeps the GPU busy.
    /// </param>
    /// <returns>The status with the first failing condition as reason.</returns>
    public IdleStatus Check(bool ignoreGpu = false)
    {
        var o = options.Value;
        var sinceInput = activity.TimeSinceLastInput();
        if (sinceInput < TimeSpan.FromMinutes(o.NoInputMinutes))
            return new IdleStatus(false, $"input {sinceInput.TotalMinutes:0.#} min ago");

        if (o.SkipWhenFullscreen && activity.IsFullscreenAppFocused())
            return new IdleStatus(false, "fullscreen app");

        if (!ignoreGpu && activity.GpuUtilizationPercent() is { } gpu && gpu > o.MaxGpuUtilPercent)
            return new IdleStatus(false, $"GPU {gpu}%");

        return new IdleStatus(true, "");
    }
}
