using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    [property: JsonPropertyName("output_estimate_learned")] bool OutputEstimateLearned,
    /// <summary>
    /// The task is about files or a project on the user's computer that are not in the message.
    /// The local model cannot read them; only the cloud CLI, which runs in the project folder, can.
    /// </summary>
    [property: JsonPropertyName("needs_project_files")] bool NeedsProjectFiles);

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
        Set needs_files to true only when answering requires reading files, code or a project on the user's computer
        that are NOT included in the message, e.g. "describe this project", "what does Program.cs do",
        "why does my build fail" with no code given. Set it to false for general questions, and when the code or text
        is pasted in the message.
        Respond only with JSON.
        """;

    private static readonly JsonNode Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "difficulty": { "type": "integer", "minimum": 1, "maximum": 5 },
            "category": { "type": "string", "enum": ["chat", "qa", "writing", "code", "analysis", "math", "other"] },
            "expected_length": { "type": "string", "enum": ["short", "medium", "long"] },
            "needs_files": { "type": "boolean" }
          },
          "required": ["difficulty", "category", "expected_length", "needs_files"]
        }
        """)!;

    // The small classifier misses plain references such as "describe this project" or "this repository",
    // so these are also caught by words: "ten/mój/nasz projekt, kod, repo...", "this project...", or a file name.
    // Polish stems are cut short ("projek", not "projekt") to match declined forms like "w tym projekcie".
    private static readonly Regex ProjectReference = new(
        @"\b(ten|tego|tym|temu|tej|ta|naszym|naszego|nasz|nasza|moim|mojego|mój|moj|moja|mojej)\s+" +
        @"(projek\w*|repo\w*|kod\w*|gateway\w*|aplikacj\w*|program\w*|solucj\w*|katalog\w*|folder\w*|klas\w*|plik\w*)" +
        @"|\b(this|my|our)\s+(project|repo|repository|codebase|code|app|application|solution|folder|directory|gateway)\b" +
        @"|\b[\w-]+\.(cs|csproj|sln|ps1|psm1|py|js|ts|tsx|jsx|json|ya?ml|md|xml|config|java|kt|go|rs|cpp|hpp|h)\b" +
        @"|\b(w|we)\s+(projekcie|repo|repozytorium|kodzie)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The classifier's flag or a reference by words. Code pasted in the message ("what does this code do? ```...```")
    /// is in the message itself, so the words alone do not count then.
    /// </summary>
    private static bool NeedsProjectFiles(string text, bool classifierSaysSo) =>
        classifierSaysSo || (!text.Contains("```") && ProjectReference.IsMatch(text));

    public async Task<TaskAnalysis> AnalyzeAsync(ChatRequest request, CancellationToken ct)
    {
        int inputTokens = TokenEstimator.Estimate(request.Messages);

        int difficulty;
        string category;
        string expectedLength;
        bool classifierUsed;
        bool needsFiles = false;
        var lastUserMessage = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        try
        {
            var excerpt = lastUserMessage.Length > ClassifierPromptChars ? lastUserMessage[..ClassifierPromptChars] + "\n[...]" : lastUserMessage;
            var result = await ollama.ClassifyAsync(ClassifierSystemPrompt, excerpt, Schema, ct)
                ?? throw new InvalidOperationException("Empty classifier response");

            // Small models occasionally emit out-of-range numbers (e.g. 1e15), which would overflow an int.
            difficulty = (int)Math.Clamp(result["difficulty"]!.GetValue<double>(), 1, 5);
            category = result["category"]!.GetValue<string>();
            expectedLength = result["expected_length"]!.GetValue<string>();
            needsFiles = result["needs_files"]?.GetValue<bool>() ?? false;
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
            learned,
            NeedsProjectFiles(lastUserMessage, needsFiles));
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
