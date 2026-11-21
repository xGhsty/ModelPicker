using System.Text.Json.Serialization;

namespace ModelPicker.Models;

// OpenAI-compatible request/response, so existing tools (IDE plugins, SDKs) can point at the gateway.

public record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public record ChatRequest
{
    /// <summary>"auto" (default), "local" or "cloud".</summary>
    [JsonPropertyName("model")] public string Model { get; init; } = "auto";
    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; init; } = [];
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; init; }
    [JsonPropertyName("temperature")] public double? Temperature { get; init; }
    /// <summary>Server-sent events; the first event carries the routing decision, before the answer is ready.</summary>
    [JsonPropertyName("stream")] public bool? Stream { get; init; }
    /// <summary>
    /// The id of a cloud handoff this request follows, e.g. when the user chose to run the task locally after all:
    /// the gateway reuses that analysis instead of classifying again (which costs time and may file the task
    /// under another expected length, so the answer would teach the wrong length statistics).
    /// </summary>
    [JsonPropertyName("handoff_id")] public string? HandoffId { get; init; }
}

public record ChatChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] ChatMessage Message,
    [property: JsonPropertyName("finish_reason")] string FinishReason);

public record ChatUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

public record ChatResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] List<ChatChoice> Choices,
    [property: JsonPropertyName("usage")] ChatUsage Usage,
    [property: JsonPropertyName("routing")] RoutingInfo Routing);

public record RoutingInfo(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("analysis")] Routing.TaskAnalysis Analysis,
    [property: JsonPropertyName("compression")] Routing.CompressionInfo? Compression,
    [property: JsonPropertyName("fallback_used")] bool FallbackUsed);

/// <summary>Provider-agnostic result of a completion.</summary>
public record CompletionResult(string Model, string Content, int InputTokens, int OutputTokens);

/// <summary>
/// Returned instead of an answer when a task needs the cloud: the gateway does not call any cloud API itself.
/// The client asks the user which provider to use (Claude Code, Codex...), runs that provider's CLI with the
/// user's own login, and reports the tokens back to /v1/usage.
/// </summary>
public record CloudHandoff(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("providers")] List<string> Providers,
    [property: JsonPropertyName("messages")] List<ChatMessage> Messages,
    [property: JsonPropertyName("routing")] RoutingInfo Routing);

/// <summary>Tokens spent by a cloud provider the client ran on its own, counted against the monthly budget.</summary>
public record UsageReport
{
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("input_tokens")] public int InputTokens { get; init; }
    /// <summary>Input read from the prompt cache; counted against the budget only partly (Cloud:CacheReadWeight).</summary>
    [JsonPropertyName("cache_read_input_tokens")] public int CacheReadInputTokens { get; init; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; init; }
}
