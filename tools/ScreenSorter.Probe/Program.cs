// Dev tool. Only --Probe:Mode=rename-sorted with --Probe:Apply=true changes anything.
// Default mode: classifies every image in a folder with the app's vision client and prints the answer, the
// folder it would map to, and the timing.
// --Probe:Mode=embed: embeds every image under a folder with CLIP and prints each image's nearest neighbours, to
// check Thresholds:ExampleSimilarity. Downloads the CLIP model if it is missing.
// --Probe:Mode=rule --Probe:Exe=<path>: prints what the rules do for a focused exe, with the real config and catalog.
// --Probe:Mode=rename-sorted: one-time cleanup for a sorted root (the folder argument). Lists generated names in
// category folders that differ from the name the decision log holds for their folder, and what they would become.
// With --Probe:Apply=true --Probe:MapFile=<csv> it renames them, logs each new path and writes old,new to the file.
// Stop the app first. Files the log does not know, or knows under another folder, are left to the app's correction
// step, and names the log expects, such as Zoom_ in Calls or process hints in the review folder, are kept.
// Usage: ScreenSorter.Probe <folder> [--Section:Key=value ...]
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Sorting;
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
var settingsProblem = ScreenSorter.Infrastructure.Files.UserSettingsFile.Apply(builder.Configuration, AppContext.BaseDirectory);
if (settingsProblem is not null)
    Console.Error.WriteLine(settingsProblem);
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
if (string.Equals(builder.Configuration["Probe:Mode"], "rename-sorted", StringComparison.OrdinalIgnoreCase))
    return await RenameSortedAsync();
return await ClassifyAsync();

async Task<int> RenameSortedAsync()
{
    var apply = builder.Configuration.GetValue<bool>("Probe:Apply");
    var mapFile = builder.Configuration["Probe:MapFile"];
    if (apply && string.IsNullOrWhiteSpace(mapFile))
    {
        Console.Error.WriteLine("--Probe:Apply=true needs --Probe:MapFile=<csv>");
        return 2;
    }

    var root = Path.GetFullPath(args[0]);
    var review = builder.Configuration[$"{PathsOptions.Section}:{nameof(PathsOptions.ReviewFolder)}"] ?? "_Review";
    var store = host.Services.GetRequiredService<IScreenshotStore>();
    var log = host.Services.GetRequiredService<IDecisionLog>();
    var files = Directory.EnumerateDirectories(root)
        .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
        .Where(f => IsImage(f) && CaptureTime.FromFileName(f) is not null)
        .Order()
        .ToList();

    var map = new List<string>();
    var perFolder = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    int renamed = 0, unknown = 0, otherFolder = 0, alreadyRight = 0;
    foreach (var file in files)
    {
        var (category, sub) = CorrectionProcessor.LabelOf(root, file)!.Value;
        var hash = await store.ComputeHashAsync(file, CancellationToken.None);
        var record = await log.FindLatestByHashAsync(hash, CancellationToken.None);
        if (record is null)
        {
            unknown++;
            continue;
        }
        if (!string.Equals(record.Classification.Category, category, StringComparison.OrdinalIgnoreCase)
            || SortedPathBuilder.MatchKey(record.Classification.SubFolder ?? "") != SortedPathBuilder.MatchKey(sub ?? ""))
        {
            otherFolder++;
            Console.WriteLine($"skip, logged under {record.Classification.Category}/{record.Classification.SubFolder}: {Path.GetRelativePath(root, file)}");
            continue;
        }

        // Older log rows for files moved into the review folder hold the folder's name instead of a file name.
        var name = string.Equals(record.Classification.Name, review, StringComparison.OrdinalIgnoreCase)
            ? ScreenSorter.Core.Models.Classification.Review(category).Name
            : record.Classification.Name;
        var target = Path.Combine(Path.GetDirectoryName(file)!,
            SortedPathBuilder.FileName(name, CaptureTime.FromFileName(file)!.Value, Path.GetExtension(file)));

        var stem = Path.GetFileNameWithoutExtension(file);
        var targetStem = Path.GetFileNameWithoutExtension(target);
        if (string.Equals(stem, targetStem, StringComparison.OrdinalIgnoreCase)
            || (stem.StartsWith(targetStem + "_", StringComparison.OrdinalIgnoreCase) && stem[(targetStem.Length + 1)..].All(char.IsDigit)))
        {
            alreadyRight++;
            continue;
        }

        if (apply)
        {
            target = store.Move(file, target);
            await log.RecordAsync(new ScreenSorter.Core.Models.SortRecord(file, target, hash,
                record.Classification with { Name = name }, record.Focus, DateTimeOffset.UtcNow), CancellationToken.None);
            map.Add($"\"{file}\",\"{target}\"");
            // Written as it goes, so an interrupted run still leaves a complete record of what moved.
            await File.WriteAllLinesAsync(mapFile!, map);
        }
        renamed++;
        var folder = sub is null ? category : $"{category}/{sub}";
        perFolder[folder] = perFolder.GetValueOrDefault(folder) + 1;
        if (renamed <= 8)
            Console.WriteLine($"{Path.GetRelativePath(root, file)} -> {Path.GetFileName(target)}");
    }

    foreach (var (folder, count) in perFolder)
        Console.WriteLine($"{count,5} {folder}");
    Console.WriteLine($"{(apply ? "renamed" : "would rename")} {renamed} of {files.Count}; already right: {alreadyRight}; not in the log: {unknown}; logged under another folder: {otherFolder}");
    return 0;
}

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
