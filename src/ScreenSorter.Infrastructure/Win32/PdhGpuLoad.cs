using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ScreenSorter.Core.Idle;
using static ScreenSorter.Infrastructure.Win32.PdhNativeMethods;

namespace ScreenSorter.Infrastructure.Win32;

/// <summary>
/// GPU load from the Windows <c>GPU Engine</c> performance counters, the source Task Manager shows. Works with any
/// vendor's driver and starts no process. Blocks for <see cref="SampleWindow"/>, because a rate needs two samples. Not thread-safe.
/// </summary>
/// <param name="logger">Receives one line while the counters cannot be read, not one per read.</param>
public sealed class PdhGpuLoad(ILogger logger)
{
    private static readonly TimeSpan SampleWindow = TimeSpan.FromMilliseconds(500);
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private bool _failing;

    /// <summary>Measures the load over a short window.</summary>
    /// <returns>The load in percent, or <see langword="null"/> if the counters cannot be read.</returns>
    public int? Read()
    {
        // A fresh query each time: processes come and go, and a long-open query would average over hours.
        var status = PdhOpenQuery(null, 0, out var query);
        if (status != PDH_OK)
            return Failed(status);
        try
        {
            status = PdhAddEnglishCounter(query, CounterPath, 0, out var counter);
            if (status != PDH_OK)
                return Failed(status);
            status = PdhCollectQueryData(query);
            if (status != PDH_OK)
                return Failed(status);
            Thread.Sleep(SampleWindow);
            status = PdhCollectQueryData(query);
            if (status != PDH_OK)
                return Failed(status);

            var samples = ReadSamples(counter, out status);
            if (samples is null)
                return Failed(status);
            _failing = false;
            return GpuEngineLoad.Busiest(samples);
        }
        finally
        {
            _ = PdhCloseQuery(query);
        }
    }

    private static List<(string Instance, double Percent)>? ReadSamples(nint counter, out uint status)
    {
        uint size = 0, count = 0;
        status = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, ref count, 0);
        // No instance at all means no GPU work is being tracked, which is a reading of zero.
        if (status == PDH_NO_DATA)
            return [];
        if (status != PDH_MORE_DATA)
            return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, ref count, buffer);
            if (status != PDH_OK)
                return null;

            var samples = new List<(string, double)>((int)count);
            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + (i * itemSize));
                // A process that just ended has no valid value; the others still count.
                if (item.Value.CStatus is PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA)
                    samples.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.DoubleValue));
            }
            return samples;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private int? Failed(uint status)
    {
        if (!_failing)
            logger.LogWarning("Windows GPU counters cannot be read (0x{Status:X8}); GPU load is ignored by the idle check", status);
        _failing = true;
        return null;
    }
}

/// <summary>PDH declarations (Windows performance counters, <c>pdh.dll</c>).</summary>
internal static partial class PdhNativeMethods
{
    public const uint PDH_OK = 0;
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_NO_DATA = 0x800007D5;
    public const uint PDH_CSTATUS_VALID_DATA = 0;
    public const uint PDH_CSTATUS_NEW_DATA = 1;
    public const uint PDH_FMT_DOUBLE = 0x200;
    public const uint PDH_FMT_NOCAP100 = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PdhFmtCounterValueItem
    {
        public nint Name;
        public PdhFmtCounterValue Value;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, ref uint itemCount, nint itemBuffer);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(nint query);
}
