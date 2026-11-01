using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Examples;
using ScreenSorter.Core.Vision;

namespace ScreenSorter.Infrastructure.Clip;

/// <summary>
/// <see cref="IImageEmbedder"/> running the CLIP vision model in-process with ONNX Runtime. The session loads on first
/// use and is released after <see cref="ClipOptions.UnloadAfterMinutes"/> without use. Calls are serialized because
/// the DirectML provider allows only one <c>Run</c> at a time per session.
/// </summary>
public sealed class ClipImageEmbedder : IImageEmbedder, IDisposable
{
    private readonly ClipModelFile _model;
    private readonly IImageInspector _images;
    private readonly ClipOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ClipImageEmbedder> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ITimer _unloadTimer;
    private InferenceSession? _session;
    private DateTimeOffset _lastUsed;

    /// <summary>Creates the embedder; nothing is loaded until the first call.</summary>
    /// <param name="model">Provides the verified model path.</param>
    /// <param name="images">Decodes and crops screenshots.</param>
    /// <param name="options">Model settings.</param>
    /// <param name="time">Clock for idle unloading.</param>
    /// <param name="logger">Logger.</param>
    public ClipImageEmbedder(ClipModelFile model, IImageInspector images, IOptions<ClipOptions> options, TimeProvider time, ILogger<ClipImageEmbedder> logger)
    {
        _model = model;
        _images = images;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _unloadTimer = time.CreateTimer(_ => UnloadIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc />
    public async Task<float[]> EmbedAsync(string imagePath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // ONNX Runtime and WIC calls block, so they leave the caller's thread.
            return await Task.Run(() =>
            {
                var session = _session ??= Load();
                _lastUsed = _time.GetUtcNow();
                var size = _options.InputSize;
                var rgb = _images.ToCenterCropRgb(imagePath, size);
                var tensor = new DenseTensor<float>(ToTensorData(rgb, size, _options.ImageMean, _options.ImageStd), [1, 3, size, size]);
                try
                {
                    using var results = session.Run([NamedOnnxValue.CreateFromTensor(_options.InputName, tensor)]);
                    return Normalize(results.First(r => r.Name == _options.OutputName).AsTensor<float>().ToArray());
                }
                catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException)
                {
                    throw new ExamplesUnavailableException($"CLIP inference failed: {ex.Message}", ex);
                }
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the session and the timer.</summary>
    public void Dispose()
    {
        _unloadTimer.Dispose();
        _session?.Dispose();
        _session = null;
    }

    /// <summary>Converts interleaved RGB bytes to the model's planar NCHW input.</summary>
    /// <param name="rgb"><c>size * size * 3</c> bytes.</param>
    /// <param name="size">Image side.</param>
    /// <param name="mean">Per-channel mean on the 0..1 scale.</param>
    /// <param name="std">Per-channel standard deviation on the 0..1 scale.</param>
    /// <returns><c>3 * size * size</c> values: all R, then G, then B, each <c>(value / 255 - mean) / std</c>.</returns>
    internal static float[] ToTensorData(byte[] rgb, int size, float[] mean, float[] std)
    {
        var pixels = size * size;
        var data = new float[3 * pixels];
        for (var i = 0; i < pixels; i++)
        {
            for (var c = 0; c < 3; c++)
                data[c * pixels + i] = (rgb[i * 3 + c] / 255f - mean[c]) / std[c];
        }
        return data;
    }

    /// <summary>Scales a vector to unit length so that dot product equals cosine similarity.</summary>
    /// <param name="vector">The vector.</param>
    /// <returns>A new unit-length vector; a zero vector stays zero.</returns>
    internal static float[] Normalize(float[] vector)
    {
        var length = Math.Sqrt(vector.Sum(v => (double)v * v));
        return length == 0 ? vector : vector.Select(v => (float)(v / length)).ToArray();
    }

    private InferenceSession Load()
    {
        var path = _model.ReadyPath
            ?? throw new ExamplesUnavailableException("CLIP model is not available yet");

        var watch = Stopwatch.StartNew();
        if (string.Equals(_options.Provider, "DirectML", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var dml = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
                dml.AppendExecutionProvider_DML(0);
                var session = new InferenceSession(path, dml);
                _logger.LogInformation("CLIP loaded on DirectML in {Ms} ms", watch.ElapsedMilliseconds);
                return session;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
            {
                _logger.LogWarning(ex, "DirectML failed; using the CPU");
            }
        }

        try
        {
            using var cpu = new SessionOptions();
            var session = new InferenceSession(path, cpu);
            _logger.LogInformation("CLIP loaded on CPU in {Ms} ms", watch.ElapsedMilliseconds);
            return session;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new ExamplesUnavailableException($"CLIP failed to load: {ex.Message}", ex);
        }
    }

    private void UnloadIfIdle()
    {
        if (!_gate.Wait(0))
            return;
        try
        {
            if (_session is not null && _time.GetUtcNow() - _lastUsed >= TimeSpan.FromMinutes(_options.UnloadAfterMinutes))
            {
                _session.Dispose();
                _session = null;
                _logger.LogInformation("CLIP unloaded after {Minutes} idle minutes", _options.UnloadAfterMinutes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
