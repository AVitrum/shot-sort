using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ScreenSorter.Tests.EndToEnd;

/// <summary>
/// A real HTTP server on a free loopback port that answers like LM Studio: chat completions, the model list and
/// unload. The app talks to it through its normal client, so request building, parsing and errors are all exercised.
/// </summary>
internal sealed class FakeLmStudio : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<string> _prompts = [];

    private FakeLmStudio(WebApplication app) => _app = app;

    /// <summary>What the next chat completion returns: HTTP status and the message content.</summary>
    public Func<(int Status, string Content)> Answer { get; set; } = () => (200, Json("Other"));

    /// <summary>Base URL for <c>LmStudio:BaseUrl</c>, e.g. <c>http://127.0.0.1:50123/v1</c>.</summary>
    public string BaseUrl => Root + "/v1";

    /// <summary>Base URL for <c>LmStudio:ManagementUrl</c>.</summary>
    public string ManagementUrl => Root + "/api/v1";

    /// <summary>User prompt text of every chat completion received, in order.</summary>
    public IReadOnlyList<string> Prompts
    {
        get { lock (_prompts) return [.. _prompts]; }
    }

    private string Root => _app.Urls.First();

    /// <summary>Starts the server on a free port.</summary>
    public static async Task<FakeLmStudio> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var fake = new FakeLmStudio(app);

        app.MapPost("/v1/chat/completions", async (HttpRequest request) =>
        {
            var body = JsonNode.Parse(await new StreamReader(request.Body).ReadToEndAsync());
            var text = body?["messages"]?[1]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
            lock (fake._prompts)
                fake._prompts.Add(text);
            var (status, content) = fake.Answer();
            if (status != 200)
                return Results.StatusCode(status);
            var response = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                    ["finish_reason"] = "stop",
                }),
            };
            return Results.Text(response.ToJsonString(), "application/json");
        });
        app.MapGet("/api/v1/models", () => Results.Text("""{"models":[]}""", "application/json"));
        app.MapPost("/api/v1/models/unload", () => Results.Ok());

        await app.StartAsync();
        return fake;
    }

    /// <summary>A model answer in the shape the app's JSON schema asks for.</summary>
    public static string Json(string category, string gameTitle = "", string appName = "", double confidence = 0.95) =>
        new JsonObject
        {
            ["category"] = category,
            ["game_title"] = gameTitle,
            ["app_name"] = appName,
            ["confidence"] = confidence,
        }.ToJsonString();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
