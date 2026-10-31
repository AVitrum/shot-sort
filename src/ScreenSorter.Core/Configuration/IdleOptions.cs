namespace ScreenSorter.Core.Configuration;

/// <summary>When the vision model may run. Bound from <c>Idle</c>.</summary>
public sealed class IdleOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Idle";

    /// <summary>Minutes without keyboard/mouse input before the system counts as idle.</summary>
    public double NoInputMinutes { get; set; } = 5;

    /// <summary>The system is busy while NVIDIA GPU utilization is above this.</summary>
    public int MaxGpuUtilPercent { get; set; } = 20;

    /// <summary>Whether a focused fullscreen app (game, video, presentation) blocks the model.</summary>
    public bool SkipWhenFullscreen { get; set; } = true;

    /// <summary>How often the idle conditions are re-checked while work is waiting.</summary>
    public int CheckIntervalSeconds { get; set; } = 30;
}
