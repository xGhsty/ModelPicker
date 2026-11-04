using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelPicker.Models;
using ModelPicker.Routing;

namespace ModelPicker.Providers;

public class OllamaClient(HttpClient http, IOptions<GatewayOptions> options, HardwareProfile hardware)
{
    private readonly OllamaOptions _opt = options.Value.Ollama;

    public async Task<CompletionResult> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _opt.Model,
            ["messages"] = JsonSerializer.SerializeToNode(request.Messages),
            ["stream"] = false,
            ["think"] = _opt.Think,
            ["keep_alive"] = _opt.KeepAlive,
            ["options"] = BuildOptions(request.MaxTokens, request.Temperature),
        };
        var json = await PostChatAsync(body, ct);

        int promptTokens = json["prompt_eval_count"]?.GetValue<int>() ?? 0;
        int outputTokens = json["eval_count"]?.GetValue<int>() ?? 0;
        hardware.Record(
            promptTokens, Nanoseconds(json["prompt_eval_duration"]),
            outputTokens, Nanoseconds(json["eval_duration"]));

        return new CompletionResult(
            _opt.Model,
            json["message"]?["content"]?.GetValue<string>() ?? "",
            promptTokens,
            outputTokens);
    }

    /// <summary>Runs a prompt on the classifier model, forcing the output to match a JSON schema.</summary>
    public async Task<JsonNode?> ClassifyAsync(string systemPrompt, string userPrompt, JsonNode schema, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _opt.ClassifierModel,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt },
            },
            ["stream"] = false,
            ["think"] = false,
            ["keep_alive"] = _opt.KeepAlive,
            // A JsonNode can belong to only one parent, so a shared schema must be copied per request.
            ["format"] = schema.DeepClone(),
            ["options"] = new JsonObject { ["temperature"] = 0 },
        };
        if (_opt.ClassifierOnCpu) body["options"]!["num_gpu"] = 0;
        var json = await PostChatAsync(body, ct);
        var content = json["message"]?["content"]?.GetValue<string>();
        return content is null ? null : JsonNode.Parse(content);
    }

    private JsonObject BuildOptions(int? maxTokens, double? temperature)
    {
        var o = new JsonObject { ["num_ctx"] = _opt.ContextWindow };
        if (maxTokens is { } m) o["num_predict"] = m;
        if (temperature is { } t) o["temperature"] = t;
        return o;
    }

    private async Task<JsonNode> PostChatAsync(JsonObject body, CancellationToken ct)
    {
        using var response = await http.PostAsync(
            $"{_opt.BaseUrl}/api/chat",
            new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama {(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private static TimeSpan Nanoseconds(JsonNode? node) =>
        node is null ? TimeSpan.Zero : TimeSpan.FromTicks(node.GetValue<long>() / 100);
}
