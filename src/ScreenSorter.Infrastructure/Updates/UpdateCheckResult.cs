namespace ScreenSorter.Infrastructure.Updates;

/// <summary>What a look for a new version found.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>Nobody has looked since the app started.</summary>
    Unknown,

    /// <summary>This copy was not put here by the installer, so it cannot replace itself.</summary>
    NotInstalled,

    /// <summary>No newer version is published.</summary>
    UpToDate,

    /// <summary>A newer version is downloaded and verified, and can be installed.</summary>
    Ready,

    /// <summary>The look or the download failed; <see cref="UpdateCheckResult.Message"/> says why.</summary>
    Failed,
}

/// <summary>The answer to "is there a new version?", for display.</summary>
/// <param name="Outcome">What was found.</param>
/// <param name="Message">One sentence for the user. Empty for <see cref="UpdateCheckOutcome.Unknown"/>.</param>
/// <param name="Version">The version ready to install, three parts; set only for <see cref="UpdateCheckOutcome.Ready"/>.</param>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, string Message, string? Version = null);
