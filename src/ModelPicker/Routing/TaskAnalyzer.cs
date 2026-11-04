using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelPicker.Models;
using ModelPicker.Providers;

namespace ModelPicker.Routing;

public record TaskAnalysis(
    [property: JsonPropertyName("estimated_input_tokens")] int EstimatedInputTokens,
    [property: JsonPropertyName("estimated_output_tokens")] int EstimatedOutputTokens,
    [property: JsonPropertyName("difficulty")] int Difficulty,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("estimated_local_seconds")] double EstimatedLocalSeconds,
    [property: JsonPropertyName("classifier_used")] bool ClassifierUsed,
    [property: JsonPropertyName("expected_length")] string ExpectedLength,
    /// <summary>True when the output estimate comes from measured answers, not the fixed default.</summary>
    [property: JsonPropertyName("output_estimate_learned")] bool OutputEstimateLearned);

/// <summary>
/// Estimates how big and how hard a task is: token counts from heuristics,
/// difficulty and expected answer length from the local classifier model.
/// </summary>
public class TaskAnalyzer(
    OllamaClient ollama, HardwareProfile hardware, OutputLengthProfile lengths, IOptions<GatewayOptions> options, ILogger<TaskAnalyzer> log)
{
    // Only the start of the prompt is sent to the classifier, to keep classification fast.
    private const int ClassifierPromptChars = 3000;

    private const string ClassifierSystemPrompt = """
        You are a router that estimates how hard a request is for a language model.
        Rate difficulty from 1 to 5:
        1 = trivial (greeting, simple fact, translation of a sentence, formatting)
        2 = easy (short explanation, simple code snippet, summary of short text)
        3 = moderate (typical coding task, multi-paragraph writing, analysis of a document)
        4 = hard (multi-step reasoning, non-trivial algorithms, debugging across files, architecture)
        5 = very hard (research-level reasoning, complex proofs, large system design)
        Estimate the expected answer length: short (< 200 tokens), medium (200-1000), long (> 1000).
        Respond only with JSON.
        """;

    private static readonly JsonNode Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "difficulty": { "type": "integer", "minimum": 1, "maximum": 5 },
            "category": { "type": "string", "enum": ["chat", "qa", "writing", "code", "analysis", "math", "other"] },
            "expected_length": { "type": "string", "enum": ["short", "medium", "long"] }
          },
          "required": ["difficulty", "category", "expected_length"]
        }
        """)!;

    public async Task<TaskAnalysis> AnalyzeAsync(ChatRequest request, CancellationToken ct)
    {
        int inputTokens = TokenEstimator.Estimate(request.Messages);

        int difficulty;
        string category;
        string expectedLength;
        bool classifierUsed;
        try
        {
            var lastUser = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
            var excerpt = lastUser.Length > ClassifierPromptChars ? lastUser[..ClassifierPromptChars] + "\n[...]" : lastUser;
            var result = await ollama.ClassifyAsync(ClassifierSystemPrompt, excerpt, Schema, ct)
                ?? throw new InvalidOperationException("Empty classifier response");

            // Small models occasionally emit out-of-range numbers (e.g. 1e15), which would overflow an int.
            difficulty = (int)Math.Clamp(result["difficulty"]!.GetValue<double>(), 1, 5);
            category = result["category"]!.GetValue<string>();
            expectedLength = result["expected_length"]!.GetValue<string>();
            classifierUsed = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Classifier failed, falling back to heuristics");
            (difficulty, category, expectedLength) = HeuristicGuess(request, inputTokens);
            classifierUsed = false;
        }

        // Learned from this machine's real answers once there is enough data, otherwise 150/600/2000.
        var (outputTokens, learned) = lengths.Estimate(category, expectedLength);
        if (request.MaxTokens is { } max) outputTokens = Math.Min(outputTokens, max);

        // Reasoning tokens are generated locally too, so they count towards local latency.
        // Learned lengths already include them (they are measured from the model's real output).
        var ollamaOptions = options.Value.Ollama;
        int localOutputTokens = outputTokens + (ollamaOptions.Think && !learned ? ollamaOptions.ThinkingTokenEstimate : 0);

        return new TaskAnalysis(
            inputTokens,
            outputTokens,
            difficulty,
            category,
            Math.Round(hardware.EstimateSeconds(inputTokens, localOutputTokens), 1),
            classifierUsed,
            expectedLength,
            learned);
    }

    private static (int Difficulty, string Category, string ExpectedLength) HeuristicGuess(ChatRequest request, int inputTokens)
    {
        var text = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        bool hasCode = text.Contains("```") || text.Contains("{") && text.Contains("}");
        int difficulty = inputTokens switch
        {
            < 50 => 1,
            < 500 => 2,
            < 3000 => 3,
            _ => 4,
        };
        if (hasCode) difficulty = Math.Min(5, difficulty + 1);
        return (difficulty, hasCode ? "code" : "other", difficulty >= 3 ? "long" : "medium");
    }
}
