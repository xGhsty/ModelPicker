using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ModelPicker;
using ModelPicker.Models;
using ModelPicker.Providers;
using ModelPicker.Routing;
using ModelPicker.Setup;
using ModelPicker.Usage;

// "setup" is a command for this app, not a configuration value, so it is kept away from the configuration.
bool setupMode = args.Contains("setup");
bool listOnly = args.Contains("--list");
var builder = WebApplication.CreateBuilder(args.Where(a => a is not ("setup" or "--list")).ToArray());

// Machine-specific choices made by the setup wizard (e.g. the local model): placed right after
// appsettings.{Environment}.json, so it overrides both, while environment variables and command line still win.
var localSettings = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
{
    Path = SetupWizard.LocalSettingsFile,
    Optional = true,
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(builder.Environment.ContentRootPath),
};
var lastAppSettings = builder.Configuration.Sources.ToList().FindLastIndex(s =>
    s is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource { Path: { } path } && path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));
builder.Configuration.Sources.Insert(lastAppSettings + 1, localSettings);

if (setupMode)
    return await SetupWizard.RunAsync(builder.Configuration, builder.Environment.ContentRootPath, listOnly);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.AddSingleton<HardwareProfile>();
builder.Services.AddSingleton<UsageTracker>();
builder.Services.AddSingleton<OutputLengthProfile>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<OllamaClient>(c => c.Timeout = TimeSpan.FromMinutes(10));
// No timeout: a Claude Code answer streams for as long as it needs; a closed connection cancels it.
builder.Services.AddHttpClient<AnthropicProxy>(c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddTransient<TaskAnalyzer>();
builder.Services.AddTransient<Router>();
builder.Services.AddTransient<ContextCompressor>();
builder.Services.AddHostedService<HardwareBenchmark>();

var app = builder.Build();

app.MapPost("/v1/chat/completions", async (
    HttpContext http,
    ChatRequest request,
    IOptions<GatewayOptions> options,
    TaskAnalyzer analyzer,
    Router router,
    ContextCompressor compressor,
    OllamaClient ollama,
    UsageTracker usage,
    OutputLengthProfile lengths,
    IMemoryCache handoffs,
    ILogger<Program> log,
    CancellationToken ct) =>
{
    var employee = Authenticate(http, options.Value);
    if (employee is null)
        return Results.Json(new { error = "Invalid or missing API key" }, statusCode: 401);
    if (request.Messages.Count == 0)
        return Results.BadRequest(new { error = "messages must not be empty" });

    // Keyed by user too, so nobody can reuse another user's handoff.
    var analysis = request.HandoffId is { } handoffId && handoffs.TryGetValue((employee.User, handoffId), out TaskAnalysis? earlier) && earlier is not null
        ? earlier
        : await analyzer.AnalyzeAsync(request, ct);
    var decision = router.Decide(request.Model, analysis, employee);
    log.LogInformation("{User}: {Target} ({Reason})", employee.User, decision.Target, decision.Reason);

    var id = $"chatcmpl-{Guid.NewGuid():N}";
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    bool stream = request.Stream == true;
    if (stream)
    {
        // OpenAI-style server-sent events. The routing decision goes out immediately, so the client can show
        // where the task went (and why) long before the answer exists. OpenAI clients ignore the extra field.
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-ModelPicker-Route"] = decision.Target.ToString().ToLowerInvariant();
        await Sse(new
        {
            id, @object = "chat.completion.chunk", created,
            model = decision.Target == Target.Local ? options.Value.Ollama.Model : "cloud",
            choices = new[] { new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null } },
            routing = new { phase = "decided", target = decision.Target.ToString().ToLowerInvariant(), reason = decision.Reason, analysis },
        });
    }

    try
    {
        if (decision.Target == Target.Cloud)
            return await HandOff(decision.Reason, fallbackUsed: false);

        CompletionResult result;
        try
        {
            result = await ollama.CompleteAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && options.Value.Routing.EnableFallback && CloudAllowed())
        {
            log.LogWarning("Local model failed ({Message}), handing the task to the cloud", ex.Message);
            return await HandOff($"local model failed ({ex.Message})", fallbackUsed: true);
        }

        usage.Record(employee, Target.Local, result.InputTokens + result.OutputTokens);

        // Learn how long the local model really answers. Answers cut off by max_tokens would understate it.
        bool truncated = request.MaxTokens is { } limit && result.OutputTokens >= limit;
        if (!truncated)
        {
            var prompt = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
            lengths.Record(analysis.Category, analysis.ExpectedLength, prompt, result.OutputTokens);
        }
        var routing = new RoutingInfo("local", decision.Reason, analysis, null, false);
        var tokens = new ChatUsage(result.InputTokens, result.OutputTokens, result.InputTokens + result.OutputTokens);

        if (!stream)
        {
            http.Response.Headers["X-ModelPicker-Route"] = "local";
            return Results.Ok(new ChatResponse(
                id, "chat.completion", created, result.Model,
                [new ChatChoice(0, new ChatMessage("assistant", result.Content), "stop")],
                tokens, routing));
        }

        await Sse(new
        {
            id, @object = "chat.completion.chunk", created, model = result.Model,
            choices = new[] { new { index = 0, delta = new { content = result.Content }, finish_reason = (string?)null } },
            routing,
        });
        await Sse(new
        {
            id, @object = "chat.completion.chunk", created, model = result.Model,
            choices = new[] { new { index = 0, delta = new { }, finish_reason = (string?)"stop" } },
            usage = tokens,
        });
        await http.Response.WriteAsync("data: [DONE]\n\n", ct);
        return Results.Empty;
    }
    catch (Exception ex) when (stream && ex is not OperationCanceledException)
    {
        // The 200 status is already sent, so the error travels as an event, like OpenAI does.
        log.LogError(ex, "Request failed after the stream started");
        await Sse(new { error = new { message = ex.Message, type = "server_error" } });
        await http.Response.WriteAsync("data: [DONE]\n\n", ct);
        return Results.Empty;
    }

    // The gateway does not talk to any cloud API: the client picks a provider and runs it with the user's login.
    async Task<IResult> HandOff(string reason, bool fallbackUsed)
    {
        // Spend local time to save cloud tokens; after a local failure this is skipped, the local model just failed.
        var messages = request.Messages;
        CompressionInfo? compression = null;
        if (!fallbackUsed)
        {
            ChatRequest compressed;
            (compressed, compression) = await compressor.CompressAsync(request, ct);
            messages = compressed.Messages;
            log.LogInformation("Compression: {Note} (saved ~{Saved} tokens)", compression.Note, compression.SavedTokens);
        }
        handoffs.Set((employee.User, id), analysis, TimeSpan.FromMinutes(30));
        var handoff = new CloudHandoff(id, "modelpicker.cloud_handoff", created, options.Value.Cloud.Providers, messages,
            new RoutingInfo("cloud", reason, analysis, compression, fallbackUsed));

        if (!stream)
        {
            http.Response.Headers["X-ModelPicker-Route"] = "cloud";
            return Results.Ok(handoff);
        }
        await Sse(handoff);
        await http.Response.WriteAsync("data: [DONE]\n\n", ct);
        return Results.Empty;
    }

    async Task Sse(object payload)
    {
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    // Handing a failed local task to the cloud must still respect access and budget.
    bool CloudAllowed() => options.Value.Cloud.Providers.Count > 0 && usage.RemainingCloudTokens(employee) > 0;
});

// The client reports what a cloud provider it ran (after a handoff) spent, so monthly budgets keep working.
app.MapPost("/v1/usage", (HttpContext http, UsageReport report, IOptions<GatewayOptions> options, UsageTracker usage, ILogger<Program> log) =>
{
    var employee = Authenticate(http, options.Value);
    if (employee is null)
        return Results.Json(new { error = "Invalid or missing API key" }, statusCode: 401);
    if (report.InputTokens < 0 || report.OutputTokens < 0 || report.CacheReadInputTokens < 0)
        return Results.BadRequest(new { error = "token counts must not be negative" });

    int tokens = new CloudUsage(report.Model ?? "", report.InputTokens, 0, report.CacheReadInputTokens, report.OutputTokens)
        .BudgetTokens(options.Value.Cloud.CacheReadWeight);
    usage.Record(employee, Target.Cloud, tokens);
    log.LogInformation("{User}: cloud via {Provider} ({Model}), {Tokens} budget tokens", employee.User, report.Provider, report.Model, tokens);
    return Results.Ok(new { remaining_cloud_tokens = usage.RemainingCloudTokens(employee) });
});

// ---- Claude Code with the gateway as its API server (ANTHROPIC_BASE_URL, set by "mp claude") ----

// Claude Code checks that the server is reachable before its first request.
app.MapMethods("/api/hello", ["GET", "HEAD"], () => Results.Ok());

// Everything else under /v1 that is not the gateway's own endpoint (/v1/messages, /v1/messages/count_tokens, ...)
// is Anthropic's API. For now every request goes to the cloud; routing to local models comes next.
app.Map("/v1/{**path}", async (HttpContext http, IOptions<GatewayOptions> options, AnthropicProxy proxy, UsageTracker usage, CancellationToken ct) =>
{
    // Claude Code's own Authorization header carries the user's Anthropic login, so the gateway key
    // travels in a header of its own (ANTHROPIC_CUSTOM_HEADERS).
    var key = http.Request.Headers[AnthropicProxy.KeyHeader].ToString();
    var employee = options.Value.ApiKeys.FirstOrDefault(k => k.Key == key && key.Length > 0);
    if (employee is null)
    {
        await AnthropicProxy.WriteErrorAsync(http, 401, "authentication_error",
            $"ModelPicker: brak lub nieznany klucz gatewaya (nagłówek {AnthropicProxy.KeyHeader}). Uruchom przez: mp claude");
        return;
    }

    using var buffer = new MemoryStream();
    await http.Request.Body.CopyToAsync(buffer, ct);
    bool isCompletion = http.Request.Path.Equals("/v1/messages", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(http.Request.Method);
    if (isCompletion && usage.RemainingCloudTokens(employee) <= 0)
    {
        await AnthropicProxy.WriteErrorAsync(http, 403, "permission_error",
            $"ModelPicker: miesięczny budżet chmury użytkownika {employee.User} jest wyczerpany ({employee.MonthlyCloudTokenBudget:N0} tokenów).");
        return;
    }
    await proxy.ForwardAsync(http, employee, buffer.ToArray(), ct);
});

app.MapGet("/stats",(HttpContext http, IOptions<GatewayOptions> options, HardwareProfile hardware, UsageTracker usage) =>
{
    // Without a key: readiness and hardware only (the launcher polls this). With one: also who you are and your budget.
    var employee = Authenticate(http, options.Value);
    return Results.Ok(new
    {
        ready = hardware.WarmedUp,
        models = new { local = options.Value.Ollama.Model, classifier = options.Value.Ollama.ClassifierModel },
        hardware = new
        {
            prompt_tokens_per_second = Math.Round(hardware.PromptTokensPerSecond),
            generation_tokens_per_second = Math.Round(hardware.GenerationTokensPerSecond, 1),
            samples = hardware.Samples,
        },
        me = employee is null ? null : new
        {
            user = employee.User,
            monthly_cloud_token_budget = employee.MonthlyCloudTokenBudget,
            remaining_cloud_tokens = usage.RemainingCloudTokens(employee),
            usage = usage.CurrentMonthUsage().FirstOrDefault(u => u.User == employee.User),
        },
        usage = usage.CurrentMonthUsage(),
    });
});

app.Run();
return 0;

static EmployeeKey? Authenticate(HttpContext http, GatewayOptions options)
{
    var header = http.Request.Headers.Authorization.ToString();
    var key = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..] : header;
    return options.ApiKeys.FirstOrDefault(k => k.Key == key && key.Length > 0);
}
