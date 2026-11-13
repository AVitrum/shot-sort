// Dev tool. Only --Probe:Mode=rename-sorted with --Probe:Apply=true changes anything.
// Default mode: classifies every image in a folder with the app's vision client and prints the answer, the
// folder it would map to, and the timing.
// --Probe:Mode=embed: embeds every image under a folder with CLIP and prints each image's nearest neighbours, to
// check Thresholds:ExampleSimilarity. Downloads the CLIP model if it is missing.
// --Probe:Mode=evaluate: for a sorted root, holds out each screenshot the user confirmed and judges it by the others,
// at a range of thresholds, to tune Thresholds:ExampleSimilarity and ExampleAgreement. Reads only.
// --Probe:Mode=burst: for a sorted root, replays each screenshot the user confirmed in capture order and counts how often
// copying the folder of a near-identical earlier shot would be right, to tune the Burst settings. Reads only.
// --Probe:Mode=accuracy: reruns the vision model on screenshots whose folder the user confirmed and scores category and
//   name. Options: Probe:Categories (Games,Chat), Probe:Limit (60), Probe:Hint (true), Probe:Out (CSV). Reads only.
// --Probe:Mode=gpu: prints the GPU load from NVML and from the Windows counters, to compare them. Reads only.
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

ScreenSorter.Infrastructure.Win32.ScreenshotsFolder.PublishEnvironmentVariable();
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
if (string.Equals(builder.Configuration["Probe:Mode"], "evaluate", StringComparison.OrdinalIgnoreCase))
    return await EvaluateAsync();
if (string.Equals(builder.Configuration["Probe:Mode"], "burst", StringComparison.OrdinalIgnoreCase))
    return await EvaluateBurstsAsync();
if (string.Equals(builder.Configuration["Probe:Mode"], "accuracy", StringComparison.OrdinalIgnoreCase))
    return await VisionAccuracyAsync();
if (string.Equals(builder.Configuration["Probe:Mode"], "gpu", StringComparison.OrdinalIgnoreCase))
{
    var activity = host.Services.GetRequiredService<ScreenSorter.Core.Idle.ISystemActivity>();
    var counters = new ScreenSorter.Infrastructure.Win32.PdhGpuLoad(host.Services.GetRequiredService<ILogger<ScreenSorter.Infrastructure.Win32.PdhGpuLoad>>());
    for (var i = 0; i < 5; i++)
    {
        var watch = Stopwatch.StartNew();
        var fromCounters = counters.Read();
        Console.WriteLine($"NVML or fallback: {activity.GpuUtilizationPercent()}%  Windows counters: {fromCounters}% ({watch.ElapsedMilliseconds} ms)");
    }
    return 0;
}
return await ClassifyAsync();

// Every screenshot under the root that the user confirmed, with its label and embedding.
async Task<List<(string File, string Category, string? Sub, float[] Vector, DateTime Captured, string? Process)>> LoadConfirmedAsync(string root, bool alsoVision = false)
{
    var modelFile = host.Services.GetRequiredService<ClipModelFile>();
    await modelFile.StartAsync(CancellationToken.None);
    await modelFile.ExecuteTask!;

    var store = host.Services.GetRequiredService<IScreenshotStore>();
    var log = host.Services.GetRequiredService<IDecisionLog>();
    var embedder = host.Services.GetRequiredService<IImageEmbedder>();

    var confirmed = new List<(string File, string Category, string? Sub, float[] Vector, DateTime Captured, string? Process)>();
    foreach (var file in Directory.EnumerateDirectories(root).SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)).Where(IsImage).Order())
    {
        var record = await log.FindLatestByHashAsync(await store.ComputeHashAsync(file, CancellationToken.None), CancellationToken.None);
        var label = CorrectionProcessor.LabelOf(root, file);
        var stage = record?.Classification.Stage;
        if ((stage != ScreenSorter.Core.Models.DecisionStage.UserCorrection && !(alsoVision && stage == ScreenSorter.Core.Models.DecisionStage.Vision)) || label is null
            || string.Equals(label.Value.Category, builder.Configuration[$"{PathsOptions.Section}:{nameof(PathsOptions.ReviewFolder)}"], StringComparison.OrdinalIgnoreCase))
            continue;
        confirmed.Add((file, label.Value.Category, label.Value.SubFolder, await embedder.EmbedAsync(file, CancellationToken.None),
            CaptureTime.FromFileName(file) ?? store.GetCreationTime(file).LocalDateTime, record?.Focus?.ProcessName));
    }
    Console.WriteLine($"Confirmed examples: {confirmed.Count} in {confirmed.Select(c => $"{c.Category}/{c.Sub}").Distinct().Count()} folders");
    return confirmed;
}

async Task<int> EvaluateBurstsAsync()
{
    // With Probe:AlsoVision, files the model placed and the user kept count too: closer to what reaches the model, but "right" then means "kept".
    var confirmed = (await LoadConfirmedAsync(Path.GetFullPath(args[0]), builder.Configuration.GetValue<bool>("Probe:AlsoVision"))).OrderBy(c => c.Captured).ToList();
    Console.WriteLine("window-s similarity  reused  coverage  right  precision");
    foreach (var window in new[] { 30, 60, 120, 300, 600 })
    {
        foreach (var similarity in new[] { 0.90, 0.92, 0.94, 0.95, 0.96, 0.97, 0.98, 0.99 })
        {
            // Same scheme as the app: a file nothing matches stands for a model answer, and only those can be copied.
            var anchors = new List<int>();
            int reused = 0, right = 0;
            for (var i = 0; i < confirmed.Count; i++)
            {
                var c = confirmed[i];
                var best = anchors
                    .Where(a => (c.Captured - confirmed[a].Captured).TotalSeconds <= window
                        && string.Equals(c.Process, confirmed[a].Process, StringComparison.OrdinalIgnoreCase))
                    .Select(a => (Anchor: a, Score: c.Vector.Zip(confirmed[a].Vector, (x, y) => (double)x * y).Sum()))
                    .Where(x => x.Score >= similarity)
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault(x => true, (Anchor: -1, Score: 0d));
                if (best.Anchor < 0)
                {
                    anchors.Add(i);
                    continue;
                }
                reused++;
                if (string.Equals(c.Category, confirmed[best.Anchor].Category, StringComparison.OrdinalIgnoreCase)
                    && SortedPathBuilder.MatchKey(c.Sub ?? "") == SortedPathBuilder.MatchKey(confirmed[best.Anchor].Sub ?? ""))
                    right++;
            }
            Console.WriteLine($"{window,8} {similarity,10:0.00} {reused,7} {(double)reused / Math.Max(1, confirmed.Count),9:P0} {right,6} {(double)right / Math.Max(1, reused),10:P1}");
        }
    }
    return 0;
}

async Task<int> EvaluateAsync()
{
    var topK = builder.Configuration.GetValue($"{ExamplesOptions.Section}:{nameof(ExamplesOptions.TopK)}", 5);
    var confirmed = await LoadConfirmedAsync(Path.GetFullPath(args[0]));

    // Each file is held out and judged by all the others, the way a new screenshot is judged by stored examples.
    var neighbours = confirmed.Select((c, i) => confirmed.Where((_, j) => j != i)
        .Select(o => new ExampleHit(c.Vector.Zip(o.Vector, (a, b) => (double)a * b).Sum(), o.Category, o.Sub))
        .OrderByDescending(h => h.Score).Take(topK).ToList()).ToList();

    Console.WriteLine("similarity agreement  answered  coverage  right  precision  same-category");
    foreach (var agreement in new[] { 0.6, 0.8, 1.0 })
    {
        foreach (var similarity in new[] { 0.80, 0.82, 0.84, 0.86, 0.88, 0.90, 0.92, 0.94, 0.96 })
        {
            int answered = 0, right = 0, sameCategory = 0;
            for (var i = 0; i < confirmed.Count; i++)
            {
                if (ExampleClassifier.Match(neighbours[i], similarity, agreement) is not { } match)
                    continue;
                answered++;
                var sameCat = string.Equals(match.Category, confirmed[i].Category, StringComparison.OrdinalIgnoreCase);
                if (sameCat)
                    sameCategory++;
                if (sameCat && SortedPathBuilder.MatchKey(match.SubFolder ?? "") == SortedPathBuilder.MatchKey(confirmed[i].Sub ?? ""))
                    right++;
            }
            Console.WriteLine($"{similarity,10:0.00} {agreement,9:0.0} {answered,9} {(double)answered / Math.Max(1, confirmed.Count),9:P0} {right,6} {(double)right / Math.Max(1, answered),10:P1} {(double)sameCategory / Math.Max(1, answered),14:P1}");
        }
    }

    // Would the nearest examples catch a wrong vision answer? For each held-out file, the score of its nearest example
    // with another label compared with its nearest example with the right one.
    int nearestRight = 0, nearestWrong = 0;
    for (var i = 0; i < confirmed.Count; i++)
    {
        if (neighbours[i].FirstOrDefault() is not { } top)
            continue;
        if (string.Equals(top.Category, confirmed[i].Category, StringComparison.OrdinalIgnoreCase)
            && SortedPathBuilder.MatchKey(top.SubFolder ?? "") == SortedPathBuilder.MatchKey(confirmed[i].Sub ?? ""))
            nearestRight++;
        else
            nearestWrong++;
    }
    Console.WriteLine($"Nearest example has the right label: {nearestRight} of {nearestRight + nearestWrong}");
    return 0;
}

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

// Reruns the vision model on screenshots whose right folder is known and scores category and name separately.
// Truth: files the user placed (latest decision a correction) and files the model placed that the user kept, both
// still where the log says. The model gets the focus hint it had at the first decision. Reads only.
async Task<int> VisionAccuracyAsync()
{
    var root = Path.GetFullPath(args[0]);
    var config = builder.Configuration;
    var categories = (config["Probe:Categories"] ?? "Games,Chat").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var limit = config.GetValue("Probe:Limit", 60);
    var useHint = config.GetValue("Probe:Hint", true);
    var review = config[$"{PathsOptions.Section}:{nameof(PathsOptions.ReviewFolder)}"] ?? "_Review";

    var log = host.Services.GetRequiredService<IDecisionLog>();
    var vision = host.Services.GetRequiredService<IVisionClassifier>();
    var mapper = host.Services.GetRequiredService<VisionMapper>();

    var known = new List<(string File, string Category, string? Sub, string Source, ScreenSorter.Core.Models.FocusSnapshot? Hint)>();
    foreach (var decisions in await log.ListFilesAsync(100_000, CancellationToken.None))
    {
        var stage = decisions.Latest.Classification.Stage;
        var source = stage == ScreenSorter.Core.Models.DecisionStage.UserCorrection ? "user"
            : stage == ScreenSorter.Core.Models.DecisionStage.Vision ? "kept" : null;
        var path = decisions.Latest.NewPath;
        if (source is null || !File.Exists(path) || CorrectionProcessor.LabelOf(root, path) is not { } label
            || string.Equals(label.Category, review, StringComparison.OrdinalIgnoreCase)
            || !categories.Contains(label.Category, StringComparer.OrdinalIgnoreCase))
            continue;
        known.Add((path, label.Category, label.SubFolder, source, decisions.First.Focus));
    }

    // Round-robin over folders, so a few large folders do not decide the score.
    var sample = known.GroupBy(k => $"{k.Category}/{k.Sub}", StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key)
        .SelectMany(g => g.OrderBy(k => k.File).Select((k, i) => (k, i)))
        .OrderBy(x => x.i).ThenBy(x => x.k.File).Select(x => x.k).Take(limit).ToList();
    Console.WriteLine($"Known: {known.Count} ({known.Count(k => k.Source == "user")} placed by the user); sample {sample.Count} from " +
                      $"{sample.Select(s => $"{s.Category}/{s.Sub}").Distinct().Count()} folders; hint {(useHint ? "on" : "off")}");
    Console.WriteLine($"Settings: MaxImageSide={config["LmStudio:MaxImageSide"]} MaxTokens={config["LmStudio:MaxTokens"]} Model={config["LmStudio:Model"]}");

    var rows = new List<(string Source, string Verdict)>();
    var csv = new List<string> { "file,source,truth,predicted,verdict,ms" };
    var total = Stopwatch.StartNew();
    foreach (var s in sample)
    {
        var sw = Stopwatch.StartNew();
        ScreenSorter.Core.Vision.VisionResult? result;
        try
        {
            result = await vision.ClassifyAsync(s.File, useHint ? s.Hint : null, CancellationToken.None);
        }
        catch (VisionUnavailableException ex)
        {
            Console.WriteLine($"UNAVAILABLE: {ex.Message}");
            return 1;
        }
        var c = mapper.Map(result);
        var truth = $"{s.Category}/{s.Sub}";
        var predicted = $"{c.Category}/{c.SubFolder}";
        var verdict = string.Equals(c.Category, review, StringComparison.OrdinalIgnoreCase) ? "review"
            : !string.Equals(c.Category, s.Category, StringComparison.OrdinalIgnoreCase) ? "wrong-category"
            : SortedPathBuilder.MatchKey(c.SubFolder ?? "") == SortedPathBuilder.MatchKey(s.Sub ?? "") ? "right"
            : "wrong-name";
        rows.Add((s.Source, verdict));
        csv.Add($"\"{Path.GetFileName(s.File)}\",{s.Source},\"{truth}\",\"{predicted}\",{verdict},{sw.ElapsedMilliseconds}");
        Console.WriteLine($"{verdict,-14} {s.Source} {truth} => {predicted} (game='{result?.GameTitle}' app='{result?.AppName}') {sw.ElapsedMilliseconds} ms");
    }
    await vision.ReleaseModelAsync(CancellationToken.None);

    foreach (var group in rows.GroupBy(r => r.Source).Append(rows.GroupBy(_ => "all").Single()))
    {
        var n = group.Count();
        string Share(string v) => $"{group.Count(r => r.Verdict == v)} ({(double)group.Count(r => r.Verdict == v) / n:P0})";
        Console.WriteLine($"{group.Key,-5} n={n,3}  right {Share("right")}  wrong-name {Share("wrong-name")}  wrong-category {Share("wrong-category")}  review {Share("review")}");
    }
    Console.WriteLine($"{rows.Count} file(s) in {total.Elapsed.TotalSeconds:0} s");
    if (config["Probe:Out"] is { Length: > 0 } outFile)
        await File.WriteAllLinesAsync(outFile, csv);
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
