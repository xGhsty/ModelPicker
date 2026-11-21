namespace ModelPicker;

public class GatewayOptions
{
    public OllamaOptions Ollama { get; set; } = new();
    public CloudOptions Cloud { get; set; } = new();
    public RoutingOptions Routing { get; set; } = new();
    public CompressionOptions Compression { get; set; } = new();
    public List<EmployeeKey> ApiKeys { get; set; } = [];
}

public class OllamaOptions
{
    // 127.0.0.1 rather than localhost: on Windows localhost tries IPv6 first and Ollama listens on IPv4 only (~2 s delay).
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen3.8:27b";
    /// <summary>Model used to classify task difficulty (can be smaller/faster than Model).</summary>
    public string ClassifierModel { get; set; } = "qwen3:1.7b";
    /// <summary>
    /// Run the classifier on CPU only, so it does not evict the main model from GPU memory
    /// (reloading a large model on every request would take many seconds).
    /// </summary>
    public bool ClassifierOnCpu { get; set; } = true;
    public int ContextWindow { get; set; } = 8192;
    /// <summary>Let the main model reason before answering (better answers, many more output tokens).</summary>
    public bool Think { get; set; } = false;
    /// <summary>Extra output tokens assumed for reasoning when Think is enabled.</summary>
    public int ThinkingTokenEstimate { get; set; } = 1500;
    /// <summary>How long Ollama keeps models loaded after the last request.</summary>
    public string KeepAlive { get; set; } = "30m";
    /// <summary>Starting values used until the hardware benchmark measures real ones.</summary>
    public double DefaultPromptTokensPerSecond { get; set; } = 1000;
    public double DefaultGenerationTokensPerSecond { get; set; } = 40;
}

/// <summary>
/// The gateway does not call any cloud API itself. When a task needs the cloud, it hands the (compressed)
/// conversation back to the client, which lets the user pick one of these providers and runs its CLI
/// with the user's own login (e.g. "claude" for Claude Code, "codex" for Codex).
/// </summary>
public class CloudOptions
{
    /// <summary>Provider ids the client may offer; empty = no cloud, every task stays local.</summary>
    // Empty by default: the configuration binder appends to a pre-filled list instead of replacing it.
    public List<string> Providers { get; set; } = [];
}

/// <summary>
/// Before a task goes to the cloud, the local model summarizes the older part of the conversation,
/// so fewer cloud tokens are spent. System prompts and the most recent messages are sent verbatim.
/// </summary>
public class CompressionOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>How many of the latest messages are always sent unchanged (includes the current task).</summary>
    public int KeepRecentMessages { get; set; } = 4;
    /// <summary>Compress only when the older history is at least this long; below it the saving is not worth the time.</summary>
    public int MinHistoryTokens { get; set; } = 1500;
    public int MaxSummaryTokens { get; set; } = 300;
    /// <summary>Skip compression when summarizing locally is estimated to take longer than this.</summary>
    public double MaxCompressionSeconds { get; set; } = 60;
}

public class RoutingOptions
{
    /// <summary>Send to cloud when the estimated local response time exceeds this.</summary>
    public double MaxLocalLatencySeconds { get; set; } = 30;
    /// <summary>Send to cloud when the estimated total tokens (in + out) exceed this.</summary>
    public int MaxLocalTokens { get; set; } = 6000;
    /// <summary>Difficulty (1-5) from the classifier at or above which the task goes to cloud.</summary>
    public int CloudDifficultyThreshold { get; set; } = 4;
    /// <summary>Retry on the other provider when the chosen one fails.</summary>
    public bool EnableFallback { get; set; } = true;
}

public class EmployeeKey
{
    public string Key { get; set; } = "";
    public string User { get; set; } = "";
    /// <summary>Cloud tokens (in + out) the user may spend per month; 0 = no cloud access.</summary>
    public long MonthlyCloudTokenBudget { get; set; }
}
