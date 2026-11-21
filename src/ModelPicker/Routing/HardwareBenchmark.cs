using System.Diagnostics;
using ModelPicker.Models;
using ModelPicker.Providers;

namespace ModelPicker.Routing;

/// <summary>
/// On startup, loads the local models into memory (so the first real request is not slowed down)
/// and runs one short generation to measure this machine's speed.
/// </summary>
public class HardwareBenchmark(OllamaClient ollama, HardwareProfile hardware, ILogger<HardwareBenchmark> log) : BackgroundService
{
    // Enough for a stable tokens/s figure. Every real answer refines the measurement anyway,
    // so a longer benchmark only delays startup: on a model split between GPU and CPU (~6 tok/s)
    // each extra 100 tokens is ~15 s of waiting.
    private const int MeasuredOutputTokens = 32;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Loading only, no generation, so model loading time does not distort the measurement.
        // Both at once: the classifier runs on the CPU, so it loads alongside the main model.
        var main = Load(classifier: false);
        var classifier = Load(classifier: true);
        await Task.WhenAll(main, classifier);

        if (await main && hardware.Measured)
        {
            // Real answers from earlier runs are a better measure than a short benchmark (which reads too high).
            log.LogInformation(
                "Local hardware (saved from earlier answers): prompt {Prompt:F0} tok/s, generation {Gen:F1} tok/s",
                hardware.PromptTokensPerSecond, hardware.GenerationTokensPerSecond);
        }
        else if (await main)
        {
            // A prompt of several hundred tokens: a tiny prompt makes prompt processing look much slower than it is.
            var context = string.Concat(Enumerable.Repeat(
                "A graphics processing unit is a specialized processor designed to accelerate parallel computations. ", 40));
            var request = new ChatRequest
            {
                Messages = [new("user", context + "\n\nBased on the text above, write a short paragraph (about 100 words) explaining what a GPU is.")],
                MaxTokens = MeasuredOutputTokens,
            };
            try
            {
                await ollama.CompleteAsync(request, ct);
                log.LogInformation(
                    "Local hardware (first guess from a short benchmark, replaced by the first real answer): prompt {Prompt:F0} tok/s, generation {Gen:F1} tok/s",
                    hardware.PromptTokensPerSecond, hardware.GenerationTokensPerSecond);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Hardware benchmark failed ({Message}); using configured defaults", ex.Message);
            }
        }

        hardware.MarkWarmedUp();
        log.LogInformation("Warm-up finished in {Seconds:F1}s, ready for requests", sw.Elapsed.TotalSeconds);

        async Task<bool> Load(bool classifier)
        {
            var name = classifier ? "Classifier" : "Main";
            try
            {
                var loadSw = Stopwatch.StartNew();
                await ollama.LoadAsync(classifier, ct);
                log.LogInformation("{Name} model loaded in {Seconds:F1}s", name, loadSw.Elapsed.TotalSeconds);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("{Name} model warm-up failed ({Message})", name, ex.Message);
                return false;
            }
        }
    }
}
