namespace ScreenSorter.Core.Configuration;

/// <summary>Tray notifications. Bound from <c>Notifications</c>.</summary>
public sealed class NotificationsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Notifications";

    /// <summary>Value of <see cref="Mode"/> that shows a summary after each batch.</summary>
    public const string BatchSummary = "BatchSummary";

    /// <summary>Value of <see cref="Mode"/> that shows no notifications.</summary>
    public const string Off = "Off";

    /// <summary><see cref="BatchSummary"/> or <see cref="Off"/>.</summary>
    public string Mode { get; set; } = BatchSummary;
}
