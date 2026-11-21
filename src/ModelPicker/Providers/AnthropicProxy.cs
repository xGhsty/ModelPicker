using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelPicker.Routing;
using ModelPicker.Usage;

namespace ModelPicker.Providers;

/// <summary>Tokens of one cloud call, as the cloud reported them.</summary>
public record CloudUsage(string Model, int InputTokens, int CacheCreationTokens, int CacheReadTokens, int OutputTokens)
{
    /// <summary>Tokens counted against the monthly budget: cache reads count only partly (they cost far less).</summary>
    public int BudgetTokens(double cacheReadWeight) =>
        InputTokens + CacheCreationTokens + (int)Math.Round(CacheReadTokens * cacheReadWeight) + OutputTokens;
}

/// <summary>
/// Forwards Claude Code's API requests to Anthropic unchanged, with the login Claude Code itself sent
/// (the user's own subscription or API key), and streams the answer back as it arrives.
/// The gateway never stores or logs that login. On the way back it reads the token usage, so monthly budgets keep working.
/// </summary>
public class AnthropicProxy(HttpClient http, IOptions<GatewayOptions> options, UsageTracker usage, ILogger<AnthropicProxy> log)
{
    /// <summary>The header carrying the gateway's own key, so the gateway knows whose budget to use. Never forwarded.</summary>
    public const string KeyHeader = "x-modelpicker-key";

    // Hop-by-hop headers and the ones the HTTP client sets itself. Accept-Encoding is dropped so the answer comes
    // uncompressed: the usage is read from it on the way through.
    private static readonly HashSet<string> SkippedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "Upgrade", "TE", "Expect",
        "Content-Length", "Accept-Encoding", KeyHeader,
    };
    private static readonly HashSet<string> SkippedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Content-Length",
    };

    public async Task ForwardAsync(HttpContext ctx, EmployeeKey employee, byte[] body, CancellationToken ct)
    {
        var opt = options.Value.Cloud;
        var target = opt.AnthropicBaseUrl.TrimEnd('/') + ctx.Request.Path + ctx.Request.QueryString;
        using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
        if (body.Length > 0 || !HttpMethods.IsGet(ctx.Request.Method)) request.Content = new ByteArrayContent(body);
        foreach (var (name, values) in ctx.Request.Headers)
        {
            if (SkippedRequestHeaders.Contains(name)) continue;
            if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                request.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
            else
                request.Headers.TryAddWithoutValidation(name, values.ToArray());
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning("Cloud unreachable ({Message})", ex.Message);
            await WriteErrorAsync(ctx, 502, "api_error", $"ModelPicker: chmura nie odpowiada ({ex.Message})");
            return;
        }

        using (response)
        {
            ctx.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                if (!SkippedResponseHeaders.Contains(name)) ctx.Response.Headers[name] = values.ToArray();

            var reader = new UsageReader(response.Content.Headers.ContentType?.MediaType == "text/event-stream");
            await using var upstream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await upstream.ReadAsync(buffer, ct)) > 0)
            {
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                await ctx.Response.Body.FlushAsync(ct); // server-sent events must reach Claude Code at once
                reader.Feed(buffer, read);
            }

            if (response.IsSuccessStatusCode && reader.Finish() is { } used)
            {
                int tokens = used.BudgetTokens(opt.CacheReadWeight);
                usage.Record(employee, Target.Cloud, tokens);
                log.LogInformation(
                    "{User}: Claude Code -> cloud ({Model}), {In} in + {CacheWrite} cache write + {CacheRead} cache read + {Out} out = {Tokens} budget tokens",
                    employee.User, used.Model, used.InputTokens, used.CacheCreationTokens, used.CacheReadTokens, used.OutputTokens, tokens);
            }
            else if (!response.IsSuccessStatusCode)
            {
                log.LogInformation("{User}: Claude Code -> cloud {Path}: HTTP {Status}", employee.User, ctx.Request.Path, (int)response.StatusCode);
            }
        }
    }

    /// <summary>An error in Anthropic's format, which Claude Code shows as "API Error: ...".</summary>
    public static Task WriteErrorAsync(HttpContext ctx, int status, string type, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var json = new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message } };
        return ctx.Response.WriteAsync(json.ToJsonString());
    }

    /// <summary>
    /// Picks the usage out of the answer as it streams past: message_start carries the input tokens,
    /// message_delta the output tokens. A plain JSON answer has it all in "usage".
    /// </summary>
    private sealed class UsageReader(bool sse)
    {
        private const int MaxJsonBytes = 4 * 1024 * 1024;
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _pending = new();
        private string _model = "";
        private int _input, _cacheWrite, _cacheRead, _output;
        private bool _seen;

        public void Feed(byte[] bytes, int count)
        {
            if (!sse && _pending.Length > MaxJsonBytes) return;
            var chars = new char[_decoder.GetCharCount(bytes, 0, count)];
            _decoder.GetChars(bytes, 0, count, chars, 0);
            _pending.Append(chars);
            if (!sse) return;

            // Whole lines only; the rest waits for the next chunk.
            var text = _pending.ToString();
            int end = text.LastIndexOf('\n');
            if (end < 0) return;
            _pending.Clear().Append(text, end + 1, text.Length - end - 1);
            foreach (var line in text[..end].Split('\n'))
                if (line.StartsWith("data:")) Read(line[5..].Trim());
        }

        public CloudUsage? Finish()
        {
            if (!sse) Read(_pending.ToString());
            return _seen ? new CloudUsage(_model, _input, _cacheWrite, _cacheRead, _output) : null;
        }

        private void Read(string json)
        {
            if (json.Length == 0 || json[0] != '{') return;
            JsonNode? node;
            try { node = JsonNode.Parse(json); } catch (System.Text.Json.JsonException) { return; }
            var type = node?["type"]?.GetValue<string>();
            var message = type == "message_start" ? node!["message"] : type == "message" ? node : null;
            if (message is not null)
            {
                _model = message["model"]?.GetValue<string>() ?? _model;
                Apply(message["usage"]);
            }
            else if (type == "message_delta")
            {
                Apply(node!["usage"]);
            }
        }

        // Later events repeat or update the counts, so the latest value of each wins.
        private void Apply(JsonNode? u)
        {
            if (u is null) return;
            _seen = true;
            _input = Int(u, "input_tokens") ?? _input;
            _cacheWrite = Int(u, "cache_creation_input_tokens") ?? _cacheWrite;
            _cacheRead = Int(u, "cache_read_input_tokens") ?? _cacheRead;
            _output = Int(u, "output_tokens") ?? _output;
        }

        private static int? Int(JsonNode u, string name) => u[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
    }
}
