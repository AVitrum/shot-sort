using Microsoft.Extensions.Logging.Abstractions;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Vision;
using ScreenSorter.Infrastructure.Imaging;
using ScreenSorter.Infrastructure.Vision;
using static ScreenSorter.Tests.TestSupport;

namespace ScreenSorter.Tests;

/// <summary>Screenshots go to the LM Studio address, so only this machine may be reached without https and consent.</summary>
public sealed class LmStudioAddressTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("http://localhost:1234/v1", false)]
    [InlineData("http://127.0.0.1:1234/v1", false)]
    [InlineData("http://127.5.6.7:1234/v1", false)]
    [InlineData("http://[::1]:1234/v1", false)]
    [InlineData("https://localhost:1234/v1", false)]
    [InlineData("https://gpu-box.lan:1234/v1", true)]
    public void Allowed(string url, bool allowRemote) => Assert.Null(LmStudioAddress.Problem(url, allowRemote));

    [Theory]
    [InlineData("http://192.168.1.20:1234/v1", true, "https")]
    [InlineData("http://gpu-box.lan:1234/v1", false, "https")]
    [InlineData("https://gpu-box.lan:1234/v1", false, "another computer")]
    [InlineData("http://localhost.evil.example:1234/v1", false, "https")]
    [InlineData("ftp://localhost/v1", false, "http://localhost:1234/v1")]
    [InlineData("localhost:1234", false, "http://localhost:1234/v1")]
    [InlineData("", false, "http://localhost:1234/v1")]
    public void Refused(string url, bool allowRemote, string hint) =>
        Assert.Contains(hint, LmStudioAddress.Problem(url, allowRemote), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Settings_refuse_another_computer_without_consent()
    {
        var model = SettingsEditModelTests.Model();
        model.LmStudioUrl = "https://gpu-box.lan:1234/v1";

        Assert.Contains(model.Validate(), p => p.Contains("another computer", StringComparison.OrdinalIgnoreCase));
        model.LmStudioAllowRemote = true;
        Assert.Empty(model.Validate());
    }

    [Fact]
    public void Settings_keep_the_consent()
    {
        var model = SettingsEditModelTests.Model();
        model.LmStudioUrl = "https://gpu-box.lan:1234/v1";
        model.LmStudioAllowRemote = true;

        var saved = System.Text.Json.Nodes.JsonNode.Parse(model.ToJson())!;
        Assert.True(saved["LmStudio"]!["AllowRemote"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Client_sends_nothing_to_a_refused_address()
    {
        var handler = new CountingHandler();
        var client = new LmStudioVisionClient(new HttpClient(handler), new WicImageInspector(), new FixedCatalog(),
            Opt(new LmStudioOptions
            {
                BaseUrl = "http://192.168.1.20:1234/v1",
                ManagementUrl = "http://192.168.1.20:1234/api/v1",
                Model = "m",
                Categories = SortPipelineTests.LmStudio().Categories,
            }),
            NullLogger<LmStudioVisionClient>.Instance);

        var error = await Assert.ThrowsAsync<VisionUnavailableException>(
            () => client.ClassifyAsync(_temp.File("a.png"), null, CancellationToken.None));
        Assert.Contains("https", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.Requests);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
