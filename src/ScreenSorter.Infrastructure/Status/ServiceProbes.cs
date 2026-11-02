using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Idle;
using ScreenSorter.Core.Sorting;
using ScreenSorter.Core.Status;
using ScreenSorter.Core.Vision;
using ScreenSorter.Infrastructure.Clip;
using ScreenSorter.Infrastructure.Updates;

namespace ScreenSorter.Infrastructure.Status;

/// <summary>Reports whether sorting is running and how many files wait for the vision model, and why.</summary>
public sealed class SortingProbe(VisionQueue queue, SortActivity activity, IdleDetector idle) : IServiceProbe
{
    /// <inheritdoc />
    public Task<ServiceCheck> CheckAsync(CancellationToken ct)
    {
        const string name = "Sorting";
        var waiting = queue.Count;
        var files = waiting == 1 ? "1 screenshot waits" : $"{waiting} screenshots wait";
        ServiceCheck check;
        if (activity.IsHeld)
            check = new(name, ServiceState.Waiting, "Starts when you close the guide.");
        else if (activity.IsPaused)
            check = new(name, ServiceState.Waiting, waiting > 0 ? $"Paused. {files} for the vision model." : "Paused.", "Choose Resume from the tray menu.");
        else if (waiting == 0)
            check = new(name, ServiceState.Ok, "Running. Nothing is waiting.");
        else if (idle.Check() is { IsIdle: false } status)
            check = new(name, ServiceState.Waiting, $"{files} for the vision model until the PC is idle (now: {status.Reason}).", "Choose Sort now from the tray menu to skip the wait.");
        else
            check = new(name, ServiceState.Ok, $"Running. {files} for the vision model.");
        return Task.FromResult(check);
    }
}

/// <summary>Checks that LM Studio's server answers and that it has the configured model.</summary>
public sealed class LmStudioProbe(HttpClient http, VisionQueue queue, IVisionServerStarter starter, IOptions<LmStudioOptions> options, IOptions<StatusOptions> status) : IServiceProbe
{
    private const string Name = "LM Studio";

    /// <inheritdoc />
    public async Task<ServiceCheck> CheckAsync(CancellationToken ct)
    {
        var model = options.Value.Model;
        var waiting = queue.Count;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(status.Value.ProbeTimeoutSeconds));
            using var response = await http.GetAsync(options.Value.ManagementUrl.TrimEnd('/') + "/models", timeout.Token);
            // An older server without this endpoint is still a server; the vision request itself reports anything worse.
            if (!response.IsSuccessStatusCode)
                return new ServiceCheck(Name, ServiceState.Ok, "Running.");

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (HasModel(body, model) == false)
            {
                return new ServiceCheck(Name, ServiceState.Problem, $"Running, but it does not have the model {model}.",
                    "Download the model in LM Studio, or name another one in Settings under Vision model.", NeedsAttention: waiting > 0);
            }
            return new ServiceCheck(Name, ServiceState.Ok, $"Running, with the model {model}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            if (starter.CanStart && !starter.LastStartFailed)
            {
                return new ServiceCheck(Name, ServiceState.Waiting,
                    "Not running. ScreenSorter starts its server when a screenshot needs the vision model and the PC is idle.");
            }

            var detail = waiting == 0
                ? "Not running. Screenshots that need the vision model will wait for it."
                : $"Not running. {(waiting == 1 ? "1 screenshot waits" : $"{waiting} screenshots wait")} for it.";
            return new ServiceCheck(Name, ServiceState.Problem, detail, "Open LM Studio and start its server.", NeedsAttention: waiting > 0);
        }
    }

    // Null when the answer has an unexpected shape, which says nothing about the model.
    private static bool? HasModel(string body, string model)
    {
        try
        {
            return JsonNode.Parse(body)?["models"]?.AsArray().Any(m => m?["key"]?.GetValue<string>() == model);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Reports how many examples the store holds, and whether it can be read.</summary>
public sealed class ExampleStoreProbe(IExampleStore store) : IServiceProbe
{
    private const string Name = "Examples";

    /// <inheritdoc />
    public async Task<ServiceCheck> CheckAsync(CancellationToken ct)
    {
        try
        {
            var count = await store.CountAsync(ct);
            return new ServiceCheck(Name, ServiceState.Ok, count == 1 ? "1 example learned." : $"{count} examples learned.");
        }
        catch (ExamplesUnavailableException)
        {
            return new ServiceCheck(Name, ServiceState.Problem,
                "The database cannot be read. Sorting goes on, but examples are not used and files you move are learned later.",
                "Restart the app. If this stays, check that the disk with your user profile has free space.",
                NeedsAttention: true);
        }
    }
}

/// <summary>Reports whether the CLIP model file is ready.</summary>
public sealed class ClipModelProbe(ClipModelFile model) : IServiceProbe
{
    /// <inheritdoc />
    public Task<ServiceCheck> CheckAsync(CancellationToken ct) => Task.FromResult(model.ReadyPath is null
        ? new ServiceCheck("Image model", ServiceState.Waiting, "Being checked or downloaded. Examples are used once it is ready.")
        : new ServiceCheck("Image model", ServiceState.Ok, "Ready."));
}

/// <summary>Reports this copy's version and what the updater is doing.</summary>
public sealed class UpdateProbe(UpdateService updates, IOptions<UpdatesOptions> options) : IServiceProbe
{
    /// <inheritdoc />
    public Task<ServiceCheck> CheckAsync(CancellationToken ct)
    {
        var version = $"Version {UpdateService.CurrentVersion.ToString(3)}.";
        var detail = !options.Value.Enabled ? $"{version} Automatic updates are off."
            : !updates.IsActive ? $"{version} This copy was not installed, so it does not update itself."
            : updates.WaitingVersion is { } waiting ? $"{version} Version {waiting} is ready and installs when the PC is idle."
            : $"{version} New versions install by themselves.";
        return Task.FromResult(new ServiceCheck("Updates", ServiceState.Ok, detail));
    }
}

/// <summary>Checks that the watch folders exist.</summary>
public sealed class WatchFolderProbe(IOptions<PathsOptions> paths) : IServiceProbe
{
    /// <inheritdoc />
    public Task<ServiceCheck> CheckAsync(CancellationToken ct)
    {
        const string name = "Watch folders";
        var folders = paths.Value.WatchFolders.Select(PathsOptions.Expand).Where(f => f.Length > 0).ToList();
        var missing = folders.Where(f => !Directory.Exists(f)).ToList();
        return Task.FromResult(missing.Count > 0
            ? new ServiceCheck(name, ServiceState.Problem, $"Missing: {string.Join(", ", missing)}.",
                "Reconnect the drive and restart the app, or choose another folder in Settings.", NeedsAttention: true)
            : new ServiceCheck(name, ServiceState.Ok, string.Join(", ", folders)));
    }
}
