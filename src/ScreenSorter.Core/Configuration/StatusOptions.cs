namespace ScreenSorter.Core.Configuration;

/// <summary>How often the app checks its helpers. Bound from <c>Status</c>.</summary>
public sealed class StatusOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Status";

    /// <summary>Seconds between checks.</summary>
    public int CheckSeconds { get; set; } = 60;

    /// <summary>How long a helper may take to answer a status request, in seconds.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 3;
}
