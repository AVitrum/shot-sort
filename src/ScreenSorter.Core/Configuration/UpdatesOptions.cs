namespace ScreenSorter.Core.Configuration;

/// <summary>Automatic update settings. Bound from <c>Updates</c>.</summary>
public sealed class UpdatesOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Updates";

    /// <summary>Whether to look for new versions and install them. Only an installed copy ever updates.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL of the GitHub API. Anything but a local test server must be <c>https</c>.</summary>
    public string ApiUrl { get; set; } = "https://api.github.com";

    /// <summary>Repository that publishes the releases, as <c>owner/name</c>.</summary>
    public string Repository { get; set; } = "AVitrum/shot-sort";

    /// <summary>
    /// The key releases must be signed with: base64 SubjectPublicKeyInfo of an ECDSA P-256 key. Empty means nothing can
    /// be verified, so nothing is installed.
    /// </summary>
    public string PublicKey { get; set; } = "";

    /// <summary>Time between looks for a new version.</summary>
    public int CheckIntervalHours { get; set; } = 24;

    /// <summary>Wait after the app starts before the first look, so a login is not slowed down.</summary>
    public int FirstCheckDelaySeconds { get; set; } = 120;

    /// <summary>Time between looks for a good moment to install a downloaded version.</summary>
    public int InstallCheckSeconds { get; set; } = 60;

    /// <summary>Timeout for asking which version is the latest. The download itself has no limit.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Where the installer is kept until it has run. Environment variables are expanded.</summary>
    public string DownloadFolder { get; set; } = @"%LOCALAPPDATA%\ScreenSorter\updates";
}
