using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelPicker.Models;

namespace ModelPicker.Providers;

/// <summary>Minimal client for the Claude Messages API.</summary>
public class AnthropicClient(HttpClient http, IOptions<GatewayOptions> options)
{
    private readonly CloudOptions _opt = options.Value.Cloud;

    public bool IsConfigured => _opt.Simulate || !string.IsNullOrEmpty(ApiKey);

    private string ApiKey => string.IsNullOrEmpty(_opt.ApiKey)
        ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? ""
        : _opt.ApiKey;

    public async Task<CompletionResult> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        if (_opt.Simulate)
            return Simulated(request);
        if (!IsConfigured)
            throw new InvalidOperationException("Cloud API key is not configured (set ANTHROPIC_API_KEY).");

        // The Messages API takes the system prompt as a separate field, not as a message.
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Content));
        var messages = new JsonArray();
        foreach (var m in request.Messages.Where(m => m.Role != "system"))
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        var body = new JsonObject
        {
            ["model"] = _opt.Model,
            ["max_tokens"] = request.MaxTokens ?? _opt.DefaultMaxTokens,
            ["messages"] = messages,
        };
        if (system.Length > 0) body["system"] = system;
        if (request.Temperature is { } t) body["temperature"] = t;

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{_opt.BaseUrl}/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("x-api-key", ApiKey);
        msg.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await http.SendAsync(msg, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic {(int)response.StatusCode}: {text}");

        var json = JsonNode.Parse(text)!;
        var content = string.Concat(
            json["content"]!.AsArray()
                .Where(b => b?["type"]?.GetValue<string>() == "text")
                .Select(b => b!["text"]!.GetValue<string>()));

        return new CompletionResult(
            json["model"]?.GetValue<string>() ?? _opt.Model,
            content,
            json["usage"]?["input_tokens"]?.GetValue<int>() ?? 0,
            json["usage"]?["output_tokens"]?.GetValue<int>() ?? 0);
    }

    // Counts what the cloud would have received, so budgets and compression savings can be tested for free.
    private CompletionResult Simulated(ChatRequest request)
    {
        int inputTokens = Routing.TokenEstimator.Estimate(request.Messages);
        return new CompletionResult(
            $"{_opt.Model} (simulated)",
            $"[SIMULATION] This request would be sent to {_opt.Model}: {request.Messages.Count} messages, ~{inputTokens} input tokens.",
            inputTokens,
            0);
    }
}
