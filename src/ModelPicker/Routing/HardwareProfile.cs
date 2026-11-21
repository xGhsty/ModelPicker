using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ModelPicker.Routing;

/// <summary>
/// Measured speed of the local GPU/CPU, updated after every local completion
/// (exponential moving average), so latency estimates follow the real hardware.
///
/// The speed of real answers is saved per machine in modelpicker-speed.json and used at the next start.
/// The short startup benchmark is only a first guess: it reads too high (a 32-token run with a small context
/// measured ~9 tok/s where real answers ran at ~5.5), so the first real answer replaces it entirely
/// and it is skipped when a saved speed exists.
/// </summary>
public class HardwareProfile
{
    public const string FileName = "modelpicker-speed.json";

    private const double Smoothing = 0.3;
    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly string _setup;
    private readonly ILogger<HardwareProfile> _log;

    public double PromptTokensPerSecond { get; private set; }
    public double GenerationTokensPerSecond { get; private set; }
    public int Samples { get; private set; }

    /// <summary>True when the speeds come from real answers (this run or a saved earlier one), not from defaults or the benchmark.</summary>
    public bool Measured => _promptSamples > 0 && _outputSamples > 0;

    /// <summary>True once the startup warm-up finished (models loaded, speed measured) or gave up.</summary>
    public bool WarmedUp { get; private set; }

    public void MarkWarmedUp() => WarmedUp = true;

    // Shorter runs are not measured: with one or two tokens Ollama reports a duration of microseconds,
    // which reads as hundreds of thousands of tokens per second and would make every task look instant locally.
    private const int MinPromptTokens = 32;
    private const int MinOutputTokens = 8;
    private static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(50);

    // Real (non-benchmark) measurements so far; the first one replaces the default or benchmark value.
    private int _promptSamples;
    private int _outputSamples;

    private sealed record Saved(string Setup, double PromptTokensPerSecond, double GenerationTokensPerSecond, int PromptSamples, int OutputSamples);

    public HardwareProfile(IOptions<GatewayOptions> options, IHostEnvironment env, ILogger<HardwareProfile> log)
    {
        var o = options.Value.Ollama;
        _log = log;
        _path = Path.Combine(env.ContentRootPath, FileName);
        // Another model, context size or CPU/GPU split runs at another speed.
        _setup = $"{o.Model}|ctx={o.ContextWindow}|classifierOnCpu={o.ClassifierOnCpu}";
        PromptTokensPerSecond = o.DefaultPromptTokensPerSecond;
        GenerationTokensPerSecond = o.DefaultGenerationTokensPerSecond;

        if (Load() is { } s)
        {
            PromptTokensPerSecond = s.PromptTokensPerSecond;
            GenerationTokensPerSecond = s.GenerationTokensPerSecond;
            _promptSamples = s.PromptSamples;
            _outputSamples = s.OutputSamples;
            Samples = Math.Max(s.PromptSamples, s.OutputSamples);
        }
    }

    public void Record(int promptTokens, TimeSpan promptDuration, int outputTokens, TimeSpan outputDuration)
    {
        bool promptMeasurable = promptTokens >= MinPromptTokens && promptDuration >= MinDuration;
        bool outputMeasurable = outputTokens >= MinOutputTokens && outputDuration >= MinDuration;
        if (!promptMeasurable && !outputMeasurable) return;
        lock (_lock)
        {
            if (!WarmedUp)
            {
                // The startup benchmark: a first guess only, never blended into real measurements or saved.
                if (promptMeasurable && _promptSamples == 0) PromptTokensPerSecond = promptTokens / promptDuration.TotalSeconds;
                if (outputMeasurable && _outputSamples == 0) GenerationTokensPerSecond = outputTokens / outputDuration.TotalSeconds;
                return;
            }
            // Counted separately: a run may measure only one of the two speeds.
            if (promptMeasurable)
                PromptTokensPerSecond = Blend(PromptTokensPerSecond, promptTokens / promptDuration.TotalSeconds, _promptSamples++);
            if (outputMeasurable)
                GenerationTokensPerSecond = Blend(GenerationTokensPerSecond, outputTokens / outputDuration.TotalSeconds, _outputSamples++);
            Samples++;
            Save();
        }
    }

    public double EstimateSeconds(int inputTokens, int outputTokens) =>
        inputTokens / PromptTokensPerSecond + outputTokens / GenerationTokensPerSecond;

    // The first real measurement of each kind replaces the default or benchmark value entirely.
    private static double Blend(double current, double measured, int earlierSamples) =>
        earlierSamples == 0 ? measured : current * (1 - Smoothing) + measured * Smoothing;

    private Saved? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path));
            if (s is not null && s.Setup == _setup && s.PromptTokensPerSecond > 0 && s.GenerationTokensPerSecond > 0) return s;
            _log.LogInformation("Local model or its settings changed; the speed is measured again");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not read {File} ({Message}); the speed is measured again", FileName, ex.Message);
        }
        return null;
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                new Saved(_setup, PromptTokensPerSecond, GenerationTokensPerSecond, _promptSamples, _outputSamples),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not save {File} ({Message})", FileName, ex.Message);
        }
    }
}
