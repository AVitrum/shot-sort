namespace ScreenSorter.Core.Idle;

/// <summary>Turns per-process, per-engine GPU counters into one load figure, the way Task Manager does.</summary>
public static class GpuEngineLoad
{
    private const string AdapterMarker = "luid_";
    private const string EngineTypeMarker = "engtype_";

    /// <summary>Adds up each engine's load across processes and returns the busiest engine.</summary>
    /// <param name="samples">
    /// Windows <c>GPU Engine</c> counter values, with instance names such as
    /// <c>pid_1234_luid_0x0_0xC5F1_phys_0_eng_0_engtype_3D</c>. Malformed names are ignored.
    /// </param>
    /// <returns>The load in percent, 0–100. Only 3D and compute engines count: video playback does not compete with a model.</returns>
    public static int Busiest(IEnumerable<(string Instance, double Percent)> samples)
    {
        var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, percent) in samples)
        {
            var adapter = instance.IndexOf(AdapterMarker, StringComparison.OrdinalIgnoreCase);
            var type = instance.IndexOf(EngineTypeMarker, StringComparison.OrdinalIgnoreCase);
            if (adapter < 0 || type < adapter)
                continue;
            var engineType = instance[(type + EngineTypeMarker.Length)..];
            if (!engineType.Equals("3D", StringComparison.OrdinalIgnoreCase) && !engineType.StartsWith("Compute", StringComparison.OrdinalIgnoreCase))
                continue;

            var engine = instance[adapter..];
            perEngine[engine] = perEngine.GetValueOrDefault(engine) + percent;
        }
        return perEngine.Count == 0 ? 0 : (int)Math.Round(Math.Clamp(perEngine.Values.Max(), 0, 100));
    }
}
