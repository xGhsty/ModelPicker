using System.Text.Json.Nodes;
using ModelPicker.Models;
using ModelPicker.Providers;

namespace ModelPicker.Routing;

/// <summary>
/// On startup, runs a short generation on the local model to measure this machine's speed
/// (and load the model into GPU memory so the first real request is not slowed down).
/// </summary>
public class HardwareBenchmark(OllamaClient ollama, HardwareProfile hardware, ILogger<HardwareBenchmark> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // A prompt of several hundred tokens: a tiny prompt makes prompt processing look much slower than it is.
        var context = string.Concat(Enumerable.Repeat(
            "A graphics processing unit is a specialized processor designed to accelerate parallel computations. ", 40));
        var request = new ChatRequest
        {
            Messages = [new("user", context + "\n\nBased on the text above, write a short paragraph (about 100 words) explaining what a GPU is.")],
            MaxTokens = 150,
        };
        try
        {
            // Twice: the first run includes model loading time, the second reflects steady-state speed.
            await ollama.CompleteAsync(request, ct);
            await ollama.CompleteAsync(request, ct);
            log.LogInformation(
                "Local hardware: prompt {Prompt:F0} tok/s, generation {Gen:F1} tok/s",
                hardware.PromptTokensPerSecond, hardware.GenerationTokensPerSecond);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Hardware benchmark failed ({Message}); using configured defaults", ex.Message);
        }

        // Load the classifier too, otherwise the first real request pays for loading it.
        try
        {
            await ollama.ClassifyAsync("Respond with JSON.", "hi", JsonNode.Parse("{ \"type\": \"object\" }")!, ct);
            log.LogInformation("Classifier model loaded");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Classifier warm-up failed ({Message})", ex.Message);
        }

        hardware.MarkWarmedUp();
        log.LogInformation("Warm-up finished, ready for requests");
    }
}
