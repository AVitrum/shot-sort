using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Idle;
using ScreenSorter.Infrastructure.Win32;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

public class GpuEngineLoadTests
{
    private const string Engine3D = "luid_0x0_0xC5F1_phys_0_eng_0_engtype_3D";

    [Fact]
    public void AddsProcessesOnOneEngine()
    {
        Assert.Equal(50, GpuEngineLoad.Busiest([($"pid_1_{Engine3D}", 20), ($"pid_2_{Engine3D}", 30)]));
    }

    [Fact]
    public void ReportsTheBusiestEngine_NotTheSum()
    {
        Assert.Equal(40, GpuEngineLoad.Busiest(
        [
            ($"pid_1_{Engine3D}", 40),
            ("pid_2_luid_0x0_0xC5F1_phys_0_eng_1_engtype_Compute_0", 30),
        ]));
    }

    [Fact]
    public void CountsComputeEngines_BecauseModelsRunThere()
    {
        Assert.Equal(90, GpuEngineLoad.Busiest([("pid_1_luid_0x0_0xC5F1_phys_0_eng_1_engtype_Compute_1", 90)]));
    }

    [Theory]
    [InlineData("VideoDecode")]
    [InlineData("VideoEncode")]
    [InlineData("Copy")]
    public void IgnoresEnginesThatDoNotCompeteWithAModel(string type)
    {
        Assert.Equal(0, GpuEngineLoad.Busiest([($"pid_1_luid_0x0_0xC5F1_phys_0_eng_2_engtype_{type}", 95)]));
    }

    [Fact]
    public void KeepsAdaptersApart()
    {
        Assert.Equal(60, GpuEngineLoad.Busiest(
        [
            ("pid_1_luid_0x0_0xAAAA_phys_0_eng_0_engtype_3D", 60),
            ("pid_2_luid_0x0_0xBBBB_phys_0_eng_0_engtype_3D", 30),
        ]));
    }

    [Fact]
    public void StaysWithinZeroAndAHundred()
    {
        Assert.Equal(100, GpuEngineLoad.Busiest([($"pid_1_{Engine3D}", 70), ($"pid_2_{Engine3D}", 60)]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("engtype_3D_luid_0x0")]
    public void IgnoresOddNames(string instance)
    {
        Assert.Equal(0, GpuEngineLoad.Busiest([(instance, 80)]));
    }

    [Fact]
    public void NoSamples_IsZero() => Assert.Equal(0, GpuEngineLoad.Busiest([]));
}

public class RecoveringGpuReaderTests
{
    private readonly FakeTime _clock = new(T0);
    private int _resets;
    private int _reads;
    private Func<int> _source = () => 42;

    private RecoveringGpuReader Reader() => new(() => { _reads++; return _source(); }, () => _resets++, TimeSpan.FromMinutes(5), _clock, NullLogger.Instance);

    [Fact]
    public void Works_ReturnsTheReading() => Assert.Equal(42, Reader().Read());

    [Fact]
    public void TemporaryFailure_ReleasesWaitsAndRecovers()
    {
        var reader = Reader();
        _source = () => throw new InvalidOperationException("driver busy");

        Assert.Null(reader.Read());
        Assert.Equal(1, _resets);

        _source = () => 42;
        _clock.Now = T0.AddMinutes(4);
        Assert.Null(reader.Read());
        Assert.Equal(1, _reads);

        _clock.Now = T0.AddMinutes(5);
        Assert.Equal(42, reader.Read());
    }

    [Fact]
    public void FailureThatLasts_KeepsWaitingBetweenTries()
    {
        var reader = Reader();
        _source = () => throw new InvalidOperationException("driver busy");
        reader.Read();

        _clock.Now = T0.AddMinutes(5);
        reader.Read();
        _clock.Now = T0.AddMinutes(6);
        reader.Read();

        Assert.Equal(2, _reads);
    }

    [Fact]
    public void MissingLibrary_StopsForGood()
    {
        var reader = Reader();
        _source = () => throw new DllNotFoundException("nvml.dll");

        Assert.Null(reader.Read());
        _clock.Now = T0.AddDays(1);
        Assert.Null(reader.Read());

        Assert.Equal(1, _reads);
    }
}

public class PdhGpuLoadTests
{
    [Fact]
    public void RealCounters_GiveAPercentOrNoReading()
    {
        var load = new PdhGpuLoad(NullLogger.Instance).Read();

        // A build machine may have no GPU, so no reading is fine; a wrong number is not.
        Assert.True(load is null or >= 0 and <= 100, $"load was {load}");
    }
}
