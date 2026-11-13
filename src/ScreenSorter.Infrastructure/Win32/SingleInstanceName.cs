namespace ScreenSorter.Infrastructure.Win32;

/// <summary>
/// Names the mutex that keeps one copy of the app running. <c>--App:InstanceName=&lt;name&gt;</c> gives a copy its own
/// mutex, so a test build can run beside the installed app. It changes nothing else: paths still come from the
/// configuration as usual.
/// </summary>
public static class SingleInstanceName
{
    /// <summary>The configuration key, as written on the command line without the leading dashes.</summary>
    public const string ArgumentKey = "App:InstanceName";

    /// <summary>Mutex name of the normal app. The installer checks this exact name before it installs.</summary>
    public const string Default = @"Local\ScreenSorter.SingleInstance";

    private const int MaxNameLength = 64;

    /// <summary>Picks the mutex name from the command line.</summary>
    /// <param name="args">Command-line arguments in the host's form: <c>--App:InstanceName=x</c> or <c>--App:InstanceName x</c>.</param>
    /// <returns>
    /// <see cref="Default"/> with <c>.&lt;name&gt;</c> appended when a valid name is given (letters, digits, <c>-</c>,
    /// <c>_</c>, <c>.</c>, up to 64 characters); otherwise <see cref="Default"/>, so a bad name never starts a second copy.
    /// </returns>
    public static string FromArgs(IReadOnlyList<string> args)
    {
        var prefix = "--" + ArgumentKey;
        string? name = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase))
                name = arg[(prefix.Length + 1)..];
            else if (string.Equals(arg, prefix, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                name = args[++i];
        }
        return IsValid(name) ? $"{Default}.{name}" : Default;
    }

    private static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
