using System.Text.Json;
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
builder.Services.AddHttpClient<OllamaClient>(c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient<AnthropicClient>(c => c.Timeout = TimeSpan.FromMinutes(10));
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
    AnthropicClient cloud,
    UsageTracker usage,
    OutputLengthProfile lengths,
    ILogger<Program> log,
    CancellationToken ct) =>
{
    var employee = Authenticate(http, options.Value);
    if (employee is null)
        return Results.Json(new { error = "Invalid or missing API key" }, statusCode: 401);
    if (request.Messages.Count == 0)
        return Results.BadRequest(new { error = "messages must not be empty" });

    var analysis = await analyzer.AnalyzeAsync(request, ct);
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
            model = decision.Target == Target.Local ? options.Value.Ollama.Model : options.Value.Cloud.Model,
            choices = new[] { new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null } },
            routing = new { phase = "decided", target = decision.Target.ToString().ToLowerInvariant(), reason = decision.Reason, analysis },
        });
    }

    try
    {
        // Spend local time to save cloud tokens; a fallback to cloud skips this, since the local model just failed.
        var cloudRequest = request;
        CompressionInfo? compression = null;
        if (decision.Target == Target.Cloud)
        {
            (cloudRequest, compression) = await compressor.CompressAsync(request, ct);
            log.LogInformation("Compression: {Note} (saved ~{Saved} tokens)", compression.Note, compression.SavedTokens);
        }

        var target = decision.Target;
        bool fallbackUsed = false;
        CompletionResult result;
        try
        {
            result = await Run(target, cloudRequest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && options.Value.Routing.EnableFallback && CanFallBack(target))
        {
            log.LogWarning("{Target} failed ({Message}), falling back", target, ex.Message);
            target = target == Target.Local ? Target.Cloud : Target.Local;
            fallbackUsed = true;
            result = await Run(target, cloudRequest);
        }

        usage.Record(employee, target, result.InputTokens + result.OutputTokens);

        // Learn how long the local model really answers. Answers cut off by max_tokens would understate it.
        bool truncated = request.MaxTokens is { } limit && result.OutputTokens >= limit;
        if (target == Target.Local && !truncated)
        {
            var prompt = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
            lengths.Record(analysis.Category, analysis.ExpectedLength, prompt, result.OutputTokens);
        }
        var routing = new RoutingInfo(target.ToString().ToLowerInvariant(), decision.Reason, analysis, compression, fallbackUsed);
        var tokens = new ChatUsage(result.InputTokens, result.OutputTokens, result.InputTokens + result.OutputTokens);

        if (!stream)
        {
            http.Response.Headers["X-ModelPicker-Route"] = target.ToString().ToLowerInvariant();
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

    Task<CompletionResult> Run(Target t, ChatRequest cloudRequest) =>
        t == Target.Local ? ollama.CompleteAsync(request, ct) : cloud.CompleteAsync(cloudRequest, ct);

    async Task Sse(object payload)
    {
        await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    // Falling back to cloud must still respect access and budget.
    bool CanFallBack(Target failed) =>
        failed == Target.Cloud || (cloud.IsConfigured && usage.RemainingCloudTokens(employee) > 0);
});

app.MapGet("/stats", (HardwareProfile hardware, UsageTracker usage) => Results.Ok(new
{
    ready = hardware.WarmedUp,
    hardware = new
    {
        prompt_tokens_per_second = Math.Round(hardware.PromptTokensPerSecond),
        generation_tokens_per_second = Math.Round(hardware.GenerationTokensPerSecond, 1),
        samples = hardware.Samples,
    },
    usage = usage.CurrentMonthUsage(),
}));

app.Run();
return 0;

static EmployeeKey? Authenticate(HttpContext http, GatewayOptions options)
{
    var header = http.Request.Headers.Authorization.ToString();
    var key = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..] : header;
    return options.ApiKeys.FirstOrDefault(k => k.Key == key && key.Length > 0);
}
