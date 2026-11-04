using System.Collections.Concurrent;
using ModelPicker.Routing;

namespace ModelPicker.Usage;

public record UserUsage(string User, string Month, long LocalTokens, long CloudTokens, int LocalRequests, int CloudRequests);

/// <summary>
/// Per-user token usage for the current month.
/// In-memory for now; swap for a database (e.g. SQLite) to survive restarts.
/// </summary>
public class UsageTracker
{
    private readonly ConcurrentDictionary<(string User, string Month), UserUsage> _usage = new();

    private static string CurrentMonth => DateTime.UtcNow.ToString("yyyy-MM");

    public void Record(EmployeeKey employee, Target target, int tokens)
    {
        _usage.AddOrUpdate(
            (employee.User, CurrentMonth),
            _ => Apply(new UserUsage(employee.User, CurrentMonth, 0, 0, 0, 0), target, tokens),
            (_, existing) => Apply(existing, target, tokens));
    }

    public long RemainingCloudTokens(EmployeeKey employee)
    {
        var used = _usage.TryGetValue((employee.User, CurrentMonth), out var u) ? u.CloudTokens : 0;
        return Math.Max(0, employee.MonthlyCloudTokenBudget - used);
    }

    public IEnumerable<UserUsage> CurrentMonthUsage() =>
        _usage.Where(kv => kv.Key.Month == CurrentMonth).Select(kv => kv.Value);

    private static UserUsage Apply(UserUsage u, Target target, int tokens) => target == Target.Cloud
        ? u with { CloudTokens = u.CloudTokens + tokens, CloudRequests = u.CloudRequests + 1 }
        : u with { LocalTokens = u.LocalTokens + tokens, LocalRequests = u.LocalRequests + 1 };
}
