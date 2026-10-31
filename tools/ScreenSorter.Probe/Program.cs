// Dev tool: classifies every image in a folder with the app's vision client and prints the
// answer, the folder it would map to, and the timing. Never moves or changes files.
// Usage: ScreenSorter.Probe <folder> [--Section:Key=value ...]
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Vision;
using ScreenSorter.Infrastructure;

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
builder.Logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning);
builder.Services.AddScreenSorter(builder.Configuration);
using var host = builder.Build();

var vision = host.Services.GetRequiredService<IVisionClassifier>();
var mapper = host.Services.GetRequiredService<VisionMapper>();
var images = host.Services.GetRequiredService<IImageInspector>();
var model = builder.Configuration[$"{LmStudioOptions.Section}:Model"];
var extensions = builder.Configuration.GetSection($"{IntakeOptions.Section}:Extensions").Get<string[]>() ?? [".png"];

Console.WriteLine($"Model: {model}");
Console.WriteLine($"Known games: {string.Join("; ", host.Services.GetRequiredService<ScreenSorter.Core.Rules.IGameCatalog>().GetTitles())}");
var total = Stopwatch.StartNew();
var count = 0;
foreach (var file in Directory.EnumerateFiles(args[0]).Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Order())
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
Console.WriteLine($"{count} file(s) in {total.Elapsed.TotalSeconds:0.0} s");
return 0;
