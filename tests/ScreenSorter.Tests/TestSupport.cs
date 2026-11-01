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

    /// <summary>Writes a PNG with random gray noise (not blank); different seeds give different content.</summary>
    public string File(string name, int seed = 1, int width = 64, int height = 48)
    {
        var pixels = new byte[width * height];
        new Random(seed).NextBytes(pixels);
        return Png(name, width, height, pixels);
    }

    /// <summary>Writes a single-color PNG, e.g. a black failed fullscreen capture.</summary>
    public string Solid(string name, byte value, int width = 64, int height = 48) =>
        Png(name, width, height, Enumerable.Repeat(value, width * height).ToArray());

    /// <summary>Writes arbitrary bytes, e.g. a corrupt image.</summary>
    public string Raw(string name, byte[] bytes)
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Writes a PNG with exact gray pixels.</summary>
    public string Gray(string name, int width, int height, byte[] pixels) => Png(name, width, height, pixels);

    private string Png(string name, int width, int height, byte[] pixels)
    {
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Gray8, null, pixels, width);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        var path = System.IO.Path.Combine(Path, name);
        using (var stream = System.IO.File.Create(path))
            encoder.Save(stream);
        return path;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal sealed class FakeEmbedder : ScreenSorter.Core.Examples.IImageEmbedder
{
    public int Calls { get; private set; }

    public Task<float[]> EmbedAsync(string imagePath, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new float[] { 1, 0 });
    }
}

internal sealed class FakeExampleStore : ScreenSorter.Core.Examples.IExampleStore
{
    public bool Down { get; set; }
    public List<ScreenSorter.Core.Examples.ExampleHit> Hits { get; } = [];
    public Dictionary<string, ScreenSorter.Core.Examples.Example> Stored { get; } = [];
    public List<string> Deleted { get; } = [];

    public Task<long> CountAsync(CancellationToken ct) { Fail(); return Task.FromResult((long)Hits.Count); }

    public Task UpsertAsync(ScreenSorter.Core.Examples.Example example, float[] vector, CancellationToken ct)
    {
        Fail();
        Stored[example.FileHash] = example;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string fileHash, CancellationToken ct) { Fail(); Deleted.Add(fileHash); return Task.CompletedTask; }

    public Task<IReadOnlyList<ScreenSorter.Core.Examples.ExampleHit>> SearchAsync(float[] vector, int limit, CancellationToken ct)
    {
        Fail();
        return Task.FromResult<IReadOnlyList<ScreenSorter.Core.Examples.ExampleHit>>(Hits);
    }

    private void Fail()
    {
        if (Down)
            throw new ScreenSorter.Core.Examples.ExamplesUnavailableException("down");
    }
}

internal sealed class FixedCatalog(params string[] titles) : ScreenSorter.Core.Rules.IGameCatalog
{
    public IReadOnlyList<string> GetTitles() => titles;
}

internal sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
