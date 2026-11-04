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

    public void Record(int promptTokens, TimeSpan promptDuration, int outputTokens, TimeSpan outputDuration)
    {
        lock (_lock)
        {
            if (promptTokens > 0 && promptDuration > TimeSpan.Zero)
                PromptTokensPerSecond = Blend(PromptTokensPerSecond, promptTokens / promptDuration.TotalSeconds);
            if (outputTokens > 0 && outputDuration > TimeSpan.Zero)
                GenerationTokensPerSecond = Blend(GenerationTokensPerSecond, outputTokens / outputDuration.TotalSeconds);
            Samples++;
        }
    }

    public double EstimateSeconds(int inputTokens, int outputTokens) =>
        inputTokens / PromptTokensPerSecond + outputTokens / GenerationTokensPerSecond;

    // The first real measurement replaces the configured default entirely.
    private double Blend(double current, double measured) =>
        Samples == 0 ? measured : current * (1 - Smoothing) + measured * Smoothing;
}
