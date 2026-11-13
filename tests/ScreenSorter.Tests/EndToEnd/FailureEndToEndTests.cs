using Microsoft.Extensions.DependencyInjection;
using ScreenSorter.Core.Vision;
using ScreenSorter.Infrastructure.Imaging;
using static ScreenSorter.Tests.EndToEnd.AppHarness;

namespace ScreenSorter.Tests.EndToEnd;

/// <summary>
/// One file that fails in a way nobody planned for (here: the image check or the JPEG step throws
/// <see cref="InvalidOperationException"/>, as a database or driver error might) must cost that file only: the app
/// keeps running and keeps sorting everything else.
/// </summary>
[Trait("Category", "EndToEnd")]
public sealed class FailureEndToEndTests : IAsyncLifetime
{
    private readonly TempFolder _temp = new();
    private FakeLmStudio _lmStudio = null!;

    public async Task InitializeAsync() => _lmStudio = await FakeLmStudio.StartAsync();

    public async Task DisposeAsync()
    {
        await _lmStudio.DisposeAsync();
        _temp.Dispose();
    }

    [Fact]
    public async Task Unexpected_error_in_intake_skips_that_file_and_keeps_sorting()
    {
        await using var app = await AppHarness.StartAsync(_temp.Path, _lmStudio,
            s => s.AddSingleton<IImageInspector>(new PoisonInspector(failCheck: true)));
        app.FocusOn("Discord.exe");
        app.NewScreenshot("poison 1.png");
        await Task.Delay(1000);
        app.FocusOn("Discord.exe");
        app.NewScreenshot("Screenshot 2026-11-13 103000.png", seed: 2);

        Assert.True(await Eventually(() => app.Files().Any(f => f.StartsWith("Chat/Discord/"))), app.Log);
        Assert.False(app.HostStopping, app.Log);
        Assert.Contains("poison 1.png", app.Files());
    }

    [Fact]
    public async Task Unexpected_error_in_the_vision_stage_skips_that_file_and_keeps_sorting()
    {
        _lmStudio.Answer = () => (200, FakeLmStudio.Json("Other"));
        await using var app = await AppHarness.StartAsync(_temp.Path, _lmStudio,
            s => s.AddSingleton<IImageInspector>(new PoisonInspector(failCheck: false)));
        app.FocusOn("Mystery.exe");
        app.NewScreenshot("poison 2.png");
        await Task.Delay(1000);
        app.FocusOn("Mystery.exe");
        app.NewScreenshot("Screenshot 2026-11-13 103100.png", seed: 2);

        Assert.True(await Eventually(() => app.Files().Any(f => f.StartsWith("Other/"))), app.Log);
        Assert.False(app.HostStopping, app.Log);
        Assert.Contains("poison 2.png", app.Files());
    }

    // The real inspector, except for files whose name starts with "poison".
    private sealed class PoisonInspector(bool failCheck) : IImageInspector
    {
        private readonly WicImageInspector _real = new();

        public double MeasureStdDev(string imagePath) =>
            failCheck && IsPoison(imagePath) ? throw new InvalidOperationException("planted failure") : _real.MeasureStdDev(imagePath);

        public byte[] ToJpeg(string imagePath, int maxSide, int jpegQuality) =>
            IsPoison(imagePath) ? throw new InvalidOperationException("planted failure") : _real.ToJpeg(imagePath, maxSide, jpegQuality);

        public byte[] ToCenterCropRgb(string imagePath, int size) => _real.ToCenterCropRgb(imagePath, size);

        private static bool IsPoison(string path) => Path.GetFileName(path).StartsWith("poison", StringComparison.Ordinal);
    }
}
