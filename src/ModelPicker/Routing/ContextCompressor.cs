using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ModelPicker.Models;
using ModelPicker.Providers;

namespace ModelPicker.Routing;

public record CompressionInfo(
    [property: JsonPropertyName("applied")] bool Applied,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("original_tokens")] int OriginalTokens,
    [property: JsonPropertyName("compressed_tokens")] int CompressedTokens,
    [property: JsonPropertyName("seconds")] double Seconds,
    [property: JsonPropertyName("summary")] string? Summary)
{
    [JsonPropertyName("saved_tokens")] public int SavedTokens => OriginalTokens - CompressedTokens;
}

/// <summary>
/// Shrinks a conversation before it is sent to the cloud: the local model summarizes older messages,
/// while system prompts and the most recent messages (including the current task) stay verbatim,
/// so exact requirements, code and data in the task itself cannot be lost by summarization.
/// </summary>
public class ContextCompressor(
    OllamaClient ollama, HardwareProfile hardware, IOptions<GatewayOptions> options, ILogger<ContextCompressor> log)
{
    private const string SummarizerPrompt = """
        You compress a conversation so that another AI assistant can continue it without seeing the original.
        Write a dense summary in the same language as the conversation.
        Keep exactly: requirements and constraints, decisions made, names, identifiers, numbers, file names,
        error messages and open questions.
        Keep short code fragments only if they are still relevant, copied verbatim.
        Drop greetings, repetition and explanations whose conclusions are already captured.
        Use terse plain-text bullet points, no markdown formatting such as bold or headings.
        The summary must not exceed {0} words; prefer dropping details over being cut off.
        Output only the summary, without any preamble.
        """;

    // Room for the summarizer prompt and the summary itself inside the local context window.
    private const int PromptOverheadTokens = 300;

    public async Task<(ChatRequest Request, CompressionInfo Info)> CompressAsync(ChatRequest request, CancellationToken ct)
    {
        var opt = options.Value.Compression;
        int original = TokenEstimator.Estimate(request.Messages);
        (ChatRequest, CompressionInfo) Skip(string note) => (request, new(false, note, original, original, 0, null));

        if (!opt.Enabled)
            return Skip("disabled");

        var system = request.Messages.Where(m => m.Role == "system").ToList();
        var conversation = request.Messages.Where(m => m.Role != "system").ToList();

        // The verbatim tail must start with a user message, as the cloud API expects.
        int split = Math.Max(0, conversation.Count - opt.KeepRecentMessages);
        while (split > 0 && conversation[split].Role != "user") split--;
        var older = conversation.GetRange(0, split);
        var recent = conversation.GetRange(split, conversation.Count - split);

        int olderTokens = TokenEstimator.Estimate(older);
        if (olderTokens < opt.MinHistoryTokens)
            return Skip($"older history ~{olderTokens} tokens < {opt.MinHistoryTokens}, not worth compressing");
        if (olderTokens + opt.MaxSummaryTokens + PromptOverheadTokens > options.Value.Ollama.ContextWindow)
            return Skip($"older history ~{olderTokens} tokens does not fit the local context window");

        double estimated = hardware.EstimateSeconds(olderTokens, opt.MaxSummaryTokens);
        if (estimated > opt.MaxCompressionSeconds)
            return Skip($"estimated compression time {estimated:F0}s > {opt.MaxCompressionSeconds}s");

        var transcript = string.Join("\n\n", older.Select(m => $"[{m.Role}]\n{m.Content}"));
        var sw = Stopwatch.StartNew();
        string summary;
        try
        {
            // The model is asked for a length that fits MaxSummaryTokens (~2 tokens per Polish word);
            // the hard limit has headroom, since a summary cut mid-sentence loses whatever came last.
            var result = await ollama.CompleteAsync(new ChatRequest
            {
                Messages = [new("system", string.Format(SummarizerPrompt, opt.MaxSummaryTokens / 2)), new("user", transcript)],
                MaxTokens = opt.MaxSummaryTokens * 3 / 2,
                Temperature = 0.2,
            }, ct);
            summary = result.Content.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Local summarizer failed ({Message}); sending full history", ex.Message);
            return Skip("local summarizer failed");
        }
        double seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);

        List<ChatMessage> messages =
        [
            .. system,
            new("system", "Summary of the earlier part of this conversation (written by a local model; "
                + "the most recent messages follow verbatim):\n" + summary),
            .. recent,
        ];
        int compressed = TokenEstimator.Estimate(messages);
        if (summary.Length == 0 || compressed >= original)
            return Skip("summary was not shorter than the original history");

        return (request with { Messages = messages },
            new(true, $"summarized {older.Count} older messages", original, compressed, seconds, summary));
    }
}
