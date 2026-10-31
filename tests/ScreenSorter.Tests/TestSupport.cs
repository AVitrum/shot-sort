using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Models;

namespace ScreenSorter.Tests;

internal static class TestSupport
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);

    public static FocusSnapshot Focus(string process, string title = "", string exe = "", DateTimeOffset? at = null) =>
        new(at ?? T0, process, exe.Length > 0 ? exe : $@"C:\Apps\{process}", title);

    public static RulesOptions DefaultRules() => new()
    {
        BrowserProcesses = ["chrome.exe", "msedge.exe", "firefox.exe"],
        GameLibraryMarkers = [@"\steamapps\common\", @"\Epic Games\", @"\Riot Games\"],
        GameExcludeMarkers = [@"\Epic Games\Launcher\", @"\Riot Games\Riot Client\"],
        GameAliases = new(StringComparer.OrdinalIgnoreCase) { ["cs2"] = "Counter-Strike 2" },
        AppRules =
        [
            new() { Processes = ["Discord.exe"], Target = "Chat/Discord" },
            new() { Processes = ["Zoom.exe"], Target = "Calls", Name = "Zoom" },
            new() { Processes = [RulesOptions.BrowserToken], TitleContains = ["Meet"], Target = "Calls", Name = "Meet" },
            new() { Processes = [RulesOptions.BrowserToken], TitleContains = ["Discord"], Target = "Chat/Discord" },
            new() { Processes = [RulesOptions.BrowserToken], TitleContains = ["YouTube", "Twitch"], Target = "Streaming" },
            new() { Processes = [RulesOptions.BrowserToken], Target = "Browser" },
            new() { Processes = ["Code.exe"], Target = "Code" },
            new() { Processes = ["explorer.exe"], Target = "Desktop" },
        ],
    };

    public static IOptions<T> Opt<T>(T value) where T : class => Options.Create(value);
}

/// <summary>Creates and deletes a unique temp folder; never touches the real Screenshots folder.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ScreenSorterTests", Guid.NewGuid().ToString("N"));

    public string File(string name, int bytes = 64)
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)i).ToArray());
        return path;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
