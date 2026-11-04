using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ModelPicker.Routing;

/// <summary>
/// Learns how long the local model's answers really are, per expected length
/// (short/medium/long), instead of assuming a fixed 150/600/2000 tokens. The estimate is the
/// 80th percentile of the last answers, so it stays on the safe (pessimistic) side.
///
/// Guards against skewed data:
///  - the same question asked again is counted only once (only a hash is kept, never the text),
///  - each bucket has a floor, so a run of tiny answers cannot push the estimate to almost zero,
///  - only the last answers are kept, so the profile follows a change of model or usage.
///
/// Stored per machine in modelpicker-stats.json (numbers and hashes only, no prompts or answers).
/// Starts from scratch when the local model changes, because another model answers differently.
/// </summary>
public class OutputLengthProfile
{
    public const string FileName = "modelpicker-stats.json";

    private const int MaxSamples = 50;
    private const int MinSamples = 3;
    private const int MaxRememberedPrompts = 500;
    private const double Percentile = 0.8;

    private static readonly Dictionary<string, int> Defaults = new() { ["short"] = 150, ["medium"] = 600, ["long"] = 2000 };
    private static readonly Dictionary<string, int> Floors = new() { ["short"] = 40, ["medium"] = 150, ["long"] = 500 };

    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly ILogger<OutputLengthProfile> _log;
    private readonly Data _data;

    private sealed class Data
    {
        public string Model { get; set; } = "";
        public Dictionary<string, List<int>> Samples { get; set; } = [];
        public List<string> RecentPrompts { get; set; } = [];
    }

    public OutputLengthProfile(IOptions<GatewayOptions> options, IHostEnvironment env, ILogger<OutputLengthProfile> log)
    {
        _log = log;
        _path = Path.Combine(env.ContentRootPath, FileName);
        var model = options.Value.Ollama.Model;
        _data = Load(model) ?? new Data { Model = model };
    }

    /// <summary>Expected answer length in tokens; Learned is false while there are too few samples.</summary>
    public (int Tokens, bool Learned) Estimate(string category, string expectedLength)
    {
        int fallback = Defaults.GetValueOrDefault(expectedLength, 600);
        lock (_lock)
        {
            if (!_data.Samples.TryGetValue(Key(category, expectedLength), out var samples) || samples.Count < MinSamples)
                return (fallback, false);
            var sorted = samples.Order().ToList();
            int p80 = sorted[(int)Math.Ceiling(Percentile * sorted.Count) - 1];
            return (Math.Max(Floors.GetValueOrDefault(expectedLength, 40), p80), true);
        }
    }

    public void Record(string category, string expectedLength, string prompt, int outputTokens)
    {
        if (outputTokens <= 0) return;
        var hash = Hash(prompt);
        lock (_lock)
        {
            if (_data.RecentPrompts.Contains(hash)) return;
            _data.RecentPrompts.Add(hash);
            if (_data.RecentPrompts.Count > MaxRememberedPrompts) _data.RecentPrompts.RemoveAt(0);

            var key = Key(category, expectedLength);
            if (!_data.Samples.TryGetValue(key, out var samples)) _data.Samples[key] = samples = [];
            samples.Add(outputTokens);
            if (samples.Count > MaxSamples) samples.RemoveAt(0);
            Save();
        }
    }

    // Grouped by expected length only: the small classifier's category is too noisy (it calls
    // "What is the capital of Poland?" code), and splitting by it would leave each bucket with few samples.
    private static string Key(string category, string expectedLength) => expectedLength;

    // Only letters and digits count, so "Ile to 2+2?", "ile to 2+2 " and "ILE TO 2 + 2?" are the same question.
    private static string Hash(string prompt)
    {
        var normalized = new string(prompt.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    private Data? Load(string model)
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path));
            if (data?.Model == model) return data;
            _log.LogInformation("Local model changed to {Model}; answer length statistics start from scratch", model);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not read {File} ({Message}); starting from scratch", FileName, ex.Message);
        }
        return null;
    }

    private void Save()
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not save {File} ({Message})", FileName, ex.Message);
        }
    }
}
