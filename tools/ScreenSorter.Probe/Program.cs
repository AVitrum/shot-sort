// Dev tool. Never moves or changes files.
// Default mode: classifies every image in a folder with the app's vision client and prints the answer, the
// folder it would map to, and the timing.
// --Probe:Mode=embed: embeds every image under a folder with CLIP and prints each image's nearest neighbours, to
// check Thresholds:ExampleSimilarity. Downloads the CLIP model if it is missing.
// --Probe:Mode=rule --Probe:Exe=<path>: prints what the rules do for a focused exe, with the real config and catalog.
// Usage: ScreenSorter.Probe <folder> [--Section:Key=value ...]
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Vision;
using ScreenSorter.Infrastructure;
using ScreenSorter.Infrastructure.Clip;

if (args.Length == 0 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: ScreenSorter.Probe <folder> [--Section:Key=value ...]");
    return 2;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args[1..],
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information);
builder.Services.AddScreenSorter(builder.Configuration);
using var host = builder.Build();

var extensions = builder.Configuration.GetSection($"{IntakeOptions.Section}:Extensions").Get<string[]>() ?? [".png"];
bool IsImage(string f) => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase);

if (string.Equals(builder.Configuration["Probe:Mode"], "embed", StringComparison.OrdinalIgnoreCase))
    return await EmbedAsync();
if (string.Equals(builder.Configuration["Probe:Mode"], "rule", StringComparison.OrdinalIgnoreCase))
{
    var exe = builder.Configuration["Probe:Exe"] ?? "";
    var rule = host.Services.GetRequiredService<ScreenSorter.Core.Rules.RulesEngine>()
        .Classify(new ScreenSorter.Core.Models.FocusSnapshot(DateTimeOffset.UtcNow, Path.GetFileName(exe), exe, ""));
    Console.WriteLine(rule is null ? $"{exe} => no rule (vision queue)" : $"{exe} => {rule.Category}{(rule.SubFolder is null ? "" : "/" + rule.SubFolder)}");
    return 0;
}
return await ClassifyAsync();

async Task<int> ClassifyAsync()
{
    var vision = host.Services.GetRequiredService<IVisionClassifier>();
    var mapper = host.Services.GetRequiredService<VisionMapper>();
    var images = host.Services.GetRequiredService<IImageInspector>();
    Console.WriteLine($"Model: {builder.Configuration[$"{LmStudioOptions.Section}:Model"]}");
    Console.WriteLine($"Known games: {string.Join("; ", host.Services.GetRequiredService<ScreenSorter.Core.Rules.IGameCatalog>().GetTitles())}");
    var total = Stopwatch.StartNew();
    var count = 0;
    foreach (var file in Directory.EnumerateFiles(args[0]).Where(IsImage).Order())
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var stdDev = images.MeasureStdDev(file);
            var result = await vision.ClassifyAsync(file, null, CancellationToken.None);
            var c = mapper.Map(result);
            Console.WriteLine($"{Path.GetFileName(file)} | {sw.ElapsedMilliseconds,6} ms | std {stdDev,5:0.0} | " +
                              $"{result?.Category ?? "-"} game='{result?.GameTitle}' app='{result?.AppName}' conf={result?.Confidence:0.00} " +
                              $"=> {c.Category}{(c.SubFolder is null ? "" : "/" + c.SubFolder)}");
        }
        catch (VisionUnavailableException ex)
        {
            Console.WriteLine($"{Path.GetFileName(file)} | UNAVAILABLE: {ex.Message}");
            return 1;
        }
        count++;
    }
    await vision.ReleaseModelAsync(CancellationToken.None);
    Console.WriteLine($"{count} file(s) in {total.Elapsed.TotalSeconds:0.0} s");
    return 0;
}

async Task<int> EmbedAsync()
{
    var modelFile = host.Services.GetRequiredService<ClipModelFile>();
    await modelFile.StartAsync(CancellationToken.None);
    await modelFile.ExecuteTask!;

    var embedder = host.Services.GetRequiredService<IImageEmbedder>();
    var files = Directory.EnumerateFiles(args[0], "*", SearchOption.AllDirectories).Where(IsImage).Order().ToList();
    var vectors = new List<float[]>();
    var times = new List<long>();
    foreach (var file in files)
    {
        var sw = Stopwatch.StartNew();
        vectors.Add(await embedder.EmbedAsync(file, CancellationToken.None));
        times.Add(sw.ElapsedMilliseconds);
    }

    var sorted = times.Order().ToList();
    Console.WriteLine($"Embedded {files.Count} file(s), vector length {vectors.FirstOrDefault()?.Length}: first {times.FirstOrDefault()} ms, median {sorted.ElementAtOrDefault(sorted.Count / 2)} ms, max {sorted.LastOrDefault()} ms");

    var pairs = new List<double>();
    for (var i = 0; i < files.Count; i++)
    {
        var scores = Enumerable.Range(0, files.Count).Where(j => j != i)
            .Select(j => (Index: j, Score: vectors[i].Zip(vectors[j], (a, b) => (double)a * b).Sum()))
            .OrderByDescending(s => s.Score).ToList();
        pairs.AddRange(scores.Where(s => s.Index > i).Select(s => s.Score));
        var near = string.Join(" | ", scores.Take(3).Select(s => $"{s.Score:0.000} {Name(s.Index)}"));
        Console.WriteLine($"{Name(i)} -> {near}");
    }

    foreach (var limit in new[] { 0.95, 0.90, 0.85, 0.80, 0.70 })
        Console.WriteLine($"pairs >= {limit:0.00}: {pairs.Count(p => p >= limit)} of {pairs.Count}");
    return 0;

    string Name(int i) => Path.GetRelativePath(args[0], files[i]);
}
