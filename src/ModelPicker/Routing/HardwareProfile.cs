using Microsoft.Extensions.Options;

namespace ModelPicker.Routing;

/// <summary>
/// Measured speed of the local GPU/CPU, updated after every local completion
/// (exponential moving average), so latency estimates follow the real hardware.
/// </summary>
public class HardwareProfile(IOptions<GatewayOptions> options)
{
    private const double Smoothing = 0.3;
    private readonly Lock _lock = new();

    public double PromptTokensPerSecond { get; private set; } = options.Value.Ollama.DefaultPromptTokensPerSecond;
    public double GenerationTokensPerSecond { get; private set; } = options.Value.Ollama.DefaultGenerationTokensPerSecond;
    public int Samples { get; private set; }

    /// <summary>True once the startup warm-up finished (models loaded, speed measured) or gave up.</summary>
    public bool WarmedUp { get; private set; }

    public void MarkWarmedUp() => WarmedUp = true;

    // Shorter runs are not measured: with one or two tokens Ollama reports a duration of microseconds,
    // which reads as hundreds of thousands of tokens per second and would make every task look instant locally.
    private const int MinPromptTokens = 32;
    private const int MinOutputTokens = 8;
    private static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(50);

    public void Record(int promptTokens, TimeSpan promptDuration, int outputTokens, TimeSpan outputDuration)
    {
        bool promptMeasurable = promptTokens >= MinPromptTokens && promptDuration >= MinDuration;
        bool outputMeasurable = outputTokens >= MinOutputTokens && outputDuration >= MinDuration;
        if (!promptMeasurable && !outputMeasurable) return;
        lock (_lock)
        {
            // Counted separately: a run may measure only one of the two speeds.
            if (promptMeasurable)
                PromptTokensPerSecond = Blend(PromptTokensPerSecond, promptTokens / promptDuration.TotalSeconds, _promptSamples++);
            if (outputMeasurable)
                GenerationTokensPerSecond = Blend(GenerationTokensPerSecond, outputTokens / outputDuration.TotalSeconds, _outputSamples++);
            Samples++;
        }
    }

    private int _promptSamples;
    private int _outputSamples;

    public double EstimateSeconds(int inputTokens, int outputTokens) =>
        inputTokens / PromptTokensPerSecond + outputTokens / GenerationTokensPerSecond;

    // The first real measurement of each kind replaces the configured default entirely.
    private static double Blend(double current, double measured, int earlierSamples) =>
        earlierSamples == 0 ? measured : current * (1 - Smoothing) + measured * Smoothing;
}
