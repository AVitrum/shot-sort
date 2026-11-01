namespace ScreenSorter.Core.Configuration;

/// <summary>Vector store settings. Bound from <c>Qdrant</c>.</summary>
public sealed class QdrantOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Qdrant";

    /// <summary>Base URL of the REST API.</summary>
    public string Url { get; set; } = "http://localhost:6333";

    /// <summary>Collection that holds confirmed examples.</summary>
    public string Collection { get; set; } = "screenshots";

    /// <summary>Number of nearest examples compared for a new screenshot.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Per-request timeout. The store is local, so a slow answer means it is down.</summary>
    public int TimeoutSeconds { get; set; } = 5;
}
