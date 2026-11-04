using Microsoft.Extensions.Options;
using ModelPicker.Providers;
using ModelPicker.Usage;

namespace ModelPicker.Routing;

public enum Target { Local, Cloud }

public record RoutingDecision(Target Target, string Reason);

/// <summary>Turns a task analysis into a local/cloud decision using the configured policy.</summary>
public class Router(IOptions<GatewayOptions> options, UsageTracker usage, AnthropicClient cloud)
{
    private readonly GatewayOptions _opt = options.Value;

    public RoutingDecision Decide(string requestedModel, TaskAnalysis a, EmployeeKey employee)
    {
        var r = _opt.Routing;
        int totalTokens = a.EstimatedInputTokens + a.EstimatedOutputTokens;

        var wanted = requestedModel.ToLowerInvariant() switch
        {
            "local" => new RoutingDecision(Target.Local, "forced by client"),
            "cloud" => new RoutingDecision(Target.Cloud, "forced by client"),
            _ when totalTokens > _opt.Ollama.ContextWindow =>
                new(Target.Cloud, $"~{totalTokens} tokens exceed local context window ({_opt.Ollama.ContextWindow})"),
            _ when totalTokens > r.MaxLocalTokens =>
                new(Target.Cloud, $"~{totalTokens} tokens exceed local token limit ({r.MaxLocalTokens})"),
            _ when a.Difficulty >= r.CloudDifficultyThreshold =>
                new(Target.Cloud, $"difficulty {a.Difficulty} >= threshold {r.CloudDifficultyThreshold}"),
            _ when a.EstimatedLocalSeconds > r.MaxLocalLatencySeconds =>
                new(Target.Cloud, $"estimated local time {a.EstimatedLocalSeconds}s > {r.MaxLocalLatencySeconds}s"),
            _ => new RoutingDecision(Target.Local, "simple enough for local model"),
        };

        if (wanted.Target == Target.Cloud)
        {
            if (!cloud.IsConfigured)
                return new(Target.Local, $"{wanted.Reason}, but cloud is not configured");
            if (usage.RemainingCloudTokens(employee) < totalTokens)
                return new(Target.Local, $"{wanted.Reason}, but monthly cloud budget is exhausted");
        }
        return wanted;
    }
}
