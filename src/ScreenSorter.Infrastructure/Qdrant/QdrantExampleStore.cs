using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;

namespace ScreenSorter.Infrastructure.Qdrant;

/// <summary>
/// <see cref="IExampleStore"/> over Qdrant's REST API. The collection is created on the first upsert.
/// Point ids are derived from the file hash, so the same file always maps to one example.
/// </summary>
public sealed class QdrantExampleStore(HttpClient http, IOptions<QdrantOptions> options, IOptions<ClipOptions> clip) : IExampleStore
{
    private readonly QdrantOptions _options = options.Value;
    private volatile bool _collectionReady;

    private string CollectionPath => $"{_options.Url.TrimEnd('/')}/collections/{Uri.EscapeDataString(_options.Collection)}";

    /// <inheritdoc />
    public async Task<long> CountAsync(CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, CollectionPath + "/points/count", new JsonObject { ["exact"] = true }, ct);
        if (status == HttpStatusCode.NotFound)
            return 0;
        EnsureOk(status, body);
        return Read(body, r => r["count"]!.GetValue<long>());
    }

    /// <inheritdoc />
    public async Task UpsertAsync(Example example, float[] vector, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);
        var point = new JsonObject
        {
            ["id"] = PointId(example.FileHash),
            ["vector"] = new JsonArray(vector.Select(v => (JsonNode)v).ToArray()),
            ["payload"] = new JsonObject
            {
                ["file_hash"] = example.FileHash,
                ["category"] = example.Category,
                ["sub_folder"] = example.SubFolder,
                ["source"] = example.Source,
                ["created_at"] = example.CreatedAt.ToString("O"),
            },
        };
        var (status, body) = await SendAsync(HttpMethod.Put, CollectionPath + "/points?wait=true",
            new JsonObject { ["points"] = new JsonArray(point) }, ct);
        if (status == HttpStatusCode.NotFound)
            _collectionReady = false;
        EnsureOk(status, body);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string fileHash, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, CollectionPath + "/points/delete?wait=true",
            new JsonObject { ["points"] = new JsonArray(PointId(fileHash)) }, ct);
        // No collection means no example to remove.
        if (status != HttpStatusCode.NotFound)
            EnsureOk(status, body);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExampleHit>> SearchAsync(float[] vector, int limit, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, CollectionPath + "/points/query",
            new JsonObject
            {
                ["query"] = new JsonArray(vector.Select(v => (JsonNode)v).ToArray()),
                ["limit"] = limit,
                ["with_payload"] = true,
            }, ct);
        if (status == HttpStatusCode.NotFound)
            return [];
        EnsureOk(status, body);
        return Read<IReadOnlyList<ExampleHit>>(body, r => r["points"]!.AsArray().Select(p => new ExampleHit(
            p!["score"]!.GetValue<double>(),
            p["payload"]!["category"]!.GetValue<string>(),
            p["payload"]!["sub_folder"]?.GetValue<string>())).ToList());
    }

    /// <summary>Derives the point id: Qdrant accepts UUIDs, and the first 128 bits of a SHA-256 are as unique as needed.</summary>
    /// <param name="fileHash">Lower-case hex SHA-256.</param>
    /// <returns>A hyphenated UUID string.</returns>
    internal static string PointId(string fileHash) => new Guid(Convert.FromHexString(fileHash[..32])).ToString();

    private async Task EnsureCollectionAsync(CancellationToken ct)
    {
        if (_collectionReady)
            return;
        var (status, body) = await SendAsync(HttpMethod.Get, CollectionPath, null, ct);
        if (status == HttpStatusCode.NotFound)
        {
            (status, body) = await SendAsync(HttpMethod.Put, CollectionPath, new JsonObject
            {
                ["vectors"] = new JsonObject { ["size"] = clip.Value.EmbeddingSize, ["distance"] = "Cosine" },
            }, ct);
        }
        EnsureOk(status, body);
        _collectionReady = true;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string url, JsonObject? json, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (json is not null)
                request.Content = JsonContent.Create(json);
            using var response = await http.SendAsync(request, timeout.Token);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (HttpRequestException ex)
        {
            throw new ExamplesUnavailableException($"Qdrant unreachable at {_options.Url}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ExamplesUnavailableException($"Qdrant did not answer within {_options.TimeoutSeconds} s", ex);
        }
    }

    private static void EnsureOk(HttpStatusCode status, string body)
    {
        if ((int)status is < 200 or >= 300)
            throw new ExamplesUnavailableException($"Qdrant returned {(int)status}: {(body.Length > 300 ? body[..300] : body)}");
    }

    private static T Read<T>(string body, Func<JsonNode, T> read)
    {
        try
        {
            return read(JsonNode.Parse(body)!["result"]!);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            throw new ExamplesUnavailableException("Unexpected Qdrant response", ex);
        }
    }
}
