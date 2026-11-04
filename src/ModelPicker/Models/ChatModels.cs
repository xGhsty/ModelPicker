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
