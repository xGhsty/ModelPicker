using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ModelPicker.Setup;

public class CandidateModel
{
    public string Name { get; set; } = "";
    /// <summary>Approximate download size; replaced by the exact size for models already installed.</summary>
    public double SizeGb { get; set; }
}

public enum ModelFit { Gpu, PartialOffload, TooLarge }

public record ModelOption(string Name, double SizeGb, double NeededGb, ModelFit Fit, bool Installed);

/// <summary>
/// First-run setup: scans the hardware, lists the local models that physically fit
/// (recommending the largest one that fits entirely in GPU memory), pulls the chosen one
/// and saves it to appsettings.Local.json.
/// Run with:  dotnet run -- setup                  (interactive)
///            dotnet run -- setup --list           (only print the list)
///            dotnet run -- setup --pull qwen3:8b  (download one model with the progress bar)
/// </summary>
public static class SetupWizard
{
    public const string LocalSettingsFile = "appsettings.Local.json";

    // Memory the OS and the desktop keep on the GPU, and the share of RAM we allow a model to use.
    private const double ReservedVramGb = 0.8;
    private const double UsableRamShare = 0.5;

    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    public static async Task<int> RunAsync(IConfiguration config, string contentRoot, bool listOnly)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var gateway = config.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
        var candidates = config.GetSection("Setup:Candidates").Get<List<CandidateModel>>() ?? [];
        using var http = new HttpClient { BaseAddress = new Uri(gateway.Ollama.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };

        // "setup --pull <model>": just download one model with the progress bar.
        if (config["pull"] is { Length: > 0 } pullModel)
            return await PullAsync(http, pullModel) ? 0 : 1;

        // 1. Hardware
        Console.WriteLine();
        Console.WriteLine("Skanowanie sprzętu:");
        var gpus = await Step("Karta graficzna", () => Task.Run(HardwareScanner.ScanNvidiaGpus), g =>
            g.Count == 0
                ? (false, "nie wykryto karty NVIDIA - modele będą działać na procesorze (wolno)")
                : (true, string.Join(", ", g.Select(x => $"{x.Name} ({Gb(x.VramGb)} VRAM)"))));
        var ram = await Step("Pamięć RAM", () => Task.Run(HardwareScanner.ScanRamGb), r =>
            (true, $"{Gb(r)}, wątki CPU: {Environment.ProcessorCount}"));
        var hw = new HardwareInfo(gpus, ram, Environment.ProcessorCount);

        // 2. Models: catalog + whatever is already installed in Ollama (exact sizes).
        var installed = await Step("Modele zainstalowane w Ollamie", () => GetInstalledAsync(http), i =>
            i is null
                ? (false, $"Ollama nie odpowiada pod {gateway.Ollama.BaseUrl} - pokazuję tylko katalog, pobieranie nie zadziała")
                : (true, $"{i.Count} {ModelsWord(i.Count)}"));

        var sizes = candidates.ToDictionary(c => c.Name, c => c.SizeGb);
        foreach (var (name, size) in installed ?? []) sizes[name] = size;
        sizes.Remove(gateway.Ollama.ClassifierModel);

        var options = sizes
            .Select(kv => Evaluate(kv.Key, kv.Value, gateway.Ollama.ContextWindow, hw, installed?.ContainsKey(kv.Key) == true))
            .OrderBy(o => o.SizeGb)
            .ToList();
        var usable = options.Where(o => o.Fit != ModelFit.TooLarge).ToList();
        var tooLarge = options.Where(o => o.Fit == ModelFit.TooLarge).ToList();
        var recommended = Recommend(usable);
        await Step("Dopasowanie modeli do sprzętu", () => Task.FromResult(usable.Count), n =>
            (n > 0, n > 0 ? $"pasujące: {n}, zalecany: {recommended?.Name}" : "żaden model nie zmieści się na tym sprzęcie"));

        // 3. List
        Console.WriteLine();
        Console.WriteLine($"Modele lokalne (kontekst {gateway.Ollama.ContextWindow} tokenów):");
        Console.WriteLine($"  {"#",2}  {"Model",-16} {"Rozmiar",8}  {"Potrzeba",8}  {"Gdzie działa",-24} Status");
        for (int i = 0; i < usable.Count; i++)
        {
            var o = usable[i];
            var where = o.Fit == ModelFit.Gpu ? "cały na GPU - szybko" : "część na CPU - wolno";
            var status = new List<string>();
            if (o == recommended) status.Add("★ ZALECANY");
            if (o.Name == gateway.Ollama.Model) status.Add("obecnie używany");
            status.Add(o.Installed ? "zainstalowany" : "do pobrania");

            Console.ForegroundColor = o == recommended ? ConsoleColor.Green : o.Fit == ModelFit.Gpu ? ConsoleColor.Gray : ConsoleColor.DarkYellow;
            Console.WriteLine($"  {i + 1,2}  {o.Name,-16} {Gb(o.SizeGb),8}  {Gb(o.NeededGb),8}  {where,-24} {string.Join(", ", status)}");
            Console.ResetColor();
            if (Animated) await Task.Delay(70); // rows appear one by one
        }
        if (tooLarge.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Pominięte - przekraczają możliwości sprzętu: {string.Join(", ", tooLarge.Select(o => $"{o.Name} ({Gb(o.SizeGb)})"))}");
            Console.ResetColor();
        }
        Console.WriteLine("  \"Potrzeba\" = model + pamięć na kontekst (szacunek).");

        if (listOnly || usable.Count == 0) return 0;

        // 4. Choice
        int defaultIndex = recommended is null ? 1 : usable.IndexOf(recommended) + 1;
        Console.Write($"\nWybierz numer modelu [Enter = {defaultIndex}, q = anuluj]: ");
        var input = ReadAnswer();
        if (input is null || input.Equals("q", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Anulowano - konfiguracja bez zmian.");
            return 1;
        }
        int choice = input.Length == 0 ? defaultIndex : int.TryParse(input, out var n) ? n : -1;
        if (choice < 1 || choice > usable.Count)
        {
            Console.WriteLine("Nieprawidłowy numer - konfiguracja bez zmian.");
            return 1;
        }
        var chosen = usable[choice - 1];

        // 5. Pull if needed
        if (!chosen.Installed)
        {
            Console.Write($"Pobrać {chosen.Name} (~{Gb(chosen.SizeGb)})? [T/n]: ");
            var confirm = ReadAnswer();
            if (confirm is null || confirm.Equals("n", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Anulowano - konfiguracja bez zmian.");
                return 1;
            }
            if (!await PullAsync(http, chosen.Name)) return 1;
        }

        // 6. Save
        SaveLocalModel(contentRoot, chosen.Name);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✓ Model lokalny: {chosen.Name} (zapisano w {LocalSettingsFile})");
        Console.ResetColor();

        // 7. The small classifier rates every request, so it is required, not optional.
        var classifier = gateway.Ollama.ClassifierModel;
        if (installed is not null && !string.IsNullOrEmpty(classifier) && !installed.ContainsKey(classifier))
        {
            Console.WriteLine($"Pobieram model klasyfikatora {classifier} - ocenia trudność każdego zadania (wymagany, ok. 1,4 GB).");
            if (!await PullAsync(http, classifier)) return 1;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Klasyfikator: {classifier}");
            Console.ResetColor();
        }
        return 0;
    }

    public static ModelOption Evaluate(string name, double sizeGb, int contextWindow, HardwareInfo hw, bool installed)
    {
        // Weights + runtime buffers + KV cache, which grows with model size and context length (rough estimate).
        double needed = sizeGb + 0.5 + sizeGb * 0.06 * (contextWindow / 8192.0);
        double usableVram = Math.Max(0, hw.VramGb - ReservedVramGb);
        var fit = hw.Gpus.Count > 0 && needed <= usableVram ? ModelFit.Gpu
            : needed <= usableVram + hw.RamGb * UsableRamShare ? ModelFit.PartialOffload
            : ModelFit.TooLarge;
        return new ModelOption(name, sizeGb, needed, fit, installed);
    }

    /// <summary>The largest model that fits entirely on the GPU; without a GPU, the largest small model (CPU is slow).</summary>
    public static ModelOption? Recommend(IReadOnlyList<ModelOption> usable) =>
        usable.Where(o => o.Fit == ModelFit.Gpu).MaxBy(o => o.SizeGb)
        ?? usable.Where(o => o.SizeGb <= 3).MaxBy(o => o.SizeGb)
        ?? usable.MinBy(o => o.SizeGb);

    private static async Task<Dictionary<string, double>?> GetInstalledAsync(HttpClient http)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var json = JsonNode.Parse(await http.GetStringAsync("/api/tags", cts.Token))!;
            return json["models"]!.AsArray().ToDictionary(
                m => m!["name"]!.GetValue<string>(),
                m => m!["size"]!.GetValue<long>() / (1024d * 1024 * 1024));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Download state shared between the reader of Ollama's stream and the renderer.</summary>
    private sealed class PullProgress
    {
        // A model is several layers (weights, template, license...), each with its own progress.
        // Initial = what was already on disk when the layer first appeared (Ollama resumes and skips existing data).
        public Dictionary<string, (long Done, long Total, long Initial)> Layers { get; } = [];
        public string Phase { get; set; } = "Łączenie";
        public long Done => Layers.Values.Sum(l => l.Done);
        public long Total => Layers.Values.Sum(l => l.Total);
        public long Downloaded => Layers.Values.Sum(l => l.Done - l.Initial);

        public void Update(string digest, long done, long total) =>
            Layers[digest] = (done, total, Layers.TryGetValue(digest, out var old) ? old.Initial : done);
    }

    private const int BarWidth = 24;

    /// <summary>
    /// Pulls a model through Ollama with a Linux-style progress line, redrawn 10x per second:
    ///   ◑ Pobieranie qwen3:14b  ██████████░░░░░░░░░░░░░░  42,7%  4,0/9,3 GB  48,1 MB/s  ETA 1:52
    /// </summary>
    private static async Task<bool> PullAsync(HttpClient http, string model)
    {
        var progress = new PullProgress();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var renderStop = new CancellationTokenSource();
        var render = Animated ? RenderPullAsync(model, progress, sw, renderStop.Token) : Task.CompletedTask;

        string? error = null;
        try
        {
            var body = new JsonObject { ["model"] = model, ["stream"] = true }.ToJsonString();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pull") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                var msg = JsonNode.Parse(line)!;
                if (msg["error"] is { } e) { error = e.ToString(); break; }

                var status = msg["status"]?.GetValue<string>() ?? "";
                lock (progress)
                {
                    progress.Phase = status switch
                    {
                        "pulling manifest" => "Łączenie",
                        _ when status.StartsWith("pulling") => "Pobieranie",
                        _ when status.StartsWith("verifying") => "Weryfikacja",
                        _ when status.StartsWith("writing") => "Zapisywanie",
                        _ => progress.Phase,
                    };
                    if (msg["digest"]?.GetValue<string>() is { } digest && msg["total"] is { } total)
                        progress.Update(digest, msg["completed"]?.GetValue<long>() ?? 0, total.GetValue<long>());
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        renderStop.Cancel();
        await render;

        long size, downloaded;
        lock (progress) { size = progress.Total; downloaded = progress.Downloaded; }
        Console.Write("\r" + new string(' ', LineWidth()) + "\r  ");
        if (error is null)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("✓");
            Console.ResetColor();
            if (downloaded < size / 100)
            {
                Console.WriteLine($" {model} jest już pobrany ({Size(size)})");
                return true;
            }
            Console.Write($" Pobrano {model}  ");
            WriteBar(1, 1);
            var avg = sw.Elapsed.TotalSeconds > 0 ? downloaded / sw.Elapsed.TotalSeconds : 0;
            Console.WriteLine($" 100%  {Size(size)} w {Duration(sw.Elapsed.TotalSeconds)} (śr. {Speed(avg)})");
            return true;
        }
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("✗");
        Console.ResetColor();
        Console.WriteLine($" Błąd pobierania {model}: {error}");
        return false;
    }

    private static async Task RenderPullAsync(string model, PullProgress progress, System.Diagnostics.Stopwatch sw, CancellationToken stop)
    {
        int tick = 0;
        long lastDone = 0;
        double lastTime = 0, speed = 0;
        while (!stop.IsCancellationRequested)
        {
            long done, total;
            string phase;
            lock (progress) { done = progress.Done; total = progress.Total; phase = progress.Phase; }

            // Download speed, smoothed over one-second windows so the number does not jump around.
            double now = sw.Elapsed.TotalSeconds;
            if (now - lastTime >= 1)
            {
                double current = (done - lastDone) / (now - lastTime);
                speed = speed == 0 ? current : speed * 0.7 + current * 0.3;
                lastDone = done;
                lastTime = now;
            }

            Console.Write("\r  ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(SpinnerFrames[tick++ % SpinnerFrames.Length]);
            Console.ResetColor();
            var head = $" {phase} {model}  ";
            Console.Write(head);
            string tail;
            if (total > 0)
            {
                WriteBar(done, total);
                var eta = speed > 0 && done < total ? $"  ETA {Duration((total - done) / speed)}" : "";
                tail = $" {100.0 * done / total,5:F1}%  {Size(done)}/{Size(total)}  {Speed(speed)}{eta}";
            }
            else
            {
                tail = "";
            }
            // Pad to clear leftovers of a longer previous frame, but never wrap to the next line.
            int available = Math.Max(0, LineWidth() - (3 + head.Length + (total > 0 ? BarWidth : 0)));
            Console.Write(tail.Length > available ? tail[..available] : tail.PadRight(available));

            try { await Task.Delay(100, stop); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Draws ████████░░░░ with the filled part green and the rest dark gray.</summary>
    private static void WriteBar(long done, long total)
    {
        int filled = total > 0 ? (int)Math.Round(BarWidth * Math.Clamp((double)done / total, 0, 1)) : 0;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write(new string('█', filled));
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(new string('░', BarWidth - filled));
        Console.ResetColor();
    }

    private static int LineWidth()
    {
        try { return Math.Max(40, Console.WindowWidth - 1); }
        catch (IOException) { return 100; }
    }

    private static string Size(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? Gb(bytes / (1024d * 1024 * 1024))
            : (bytes / (1024d * 1024)).ToString("0", Pl) + " MB";

    private static string Speed(double bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024
            ? (bytesPerSecond / (1024 * 1024)).ToString("0.0", Pl) + " MB/s"
            : (bytesPerSecond / 1024).ToString("0", Pl) + " KB/s";

    private static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static void SaveLocalModel(string contentRoot, string model)
    {
        var path = Path.Combine(contentRoot, LocalSettingsFile);
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        var gateway = root["Gateway"] as JsonObject ?? (JsonObject)(root["Gateway"] = new JsonObject());
        var ollama = gateway["Ollama"] as JsonObject ?? (JsonObject)(gateway["Ollama"] = new JsonObject());
        ollama["Model"] = model;
        File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    // Animation only in a real console; piped/redirected output gets plain lines (MODELPICKER_ANIMATE=1 forces it, for testing).
    private static bool Animated =>
        !Console.IsOutputRedirected || Environment.GetEnvironmentVariable("MODELPICKER_ANIMATE") == "1";

    // Each step stays on screen at least this long, so fast steps do not just flash by.
    private const int MinStepMs = 500;

    // A rotating circle in Windows Terminal (and other modern terminals, which fall back to a font that has
    // these glyphs); the classic | / - \ spinner in the old Windows console, whose fonts may lack them.
    private static readonly string[] SpinnerFrames =
        Environment.GetEnvironmentVariable("WT_SESSION") is not null || !OperatingSystem.IsWindows()
            ? ["◐", "◓", "◑", "◒"]
            : ["|", "/", "-", "\\"];

    /// <summary>
    /// Shows "  ◐ text" with a rotating spinner while the work runs, then replaces the spinner
    /// with a green ✓ (or a yellow ✗) followed by the summary returned by <paramref name="describe"/>.
    /// </summary>
    private static async Task<T> Step<T>(string text, Func<Task<T>> work, Func<T, (bool Ok, string Summary)> describe)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = work();
        if (Animated)
        {
            int tick = 0;
            while (!task.IsCompleted || sw.ElapsedMilliseconds < MinStepMs)
            {
                Console.Write("\r  ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(SpinnerFrames[tick++ % SpinnerFrames.Length]);
                Console.ResetColor();
                Console.Write($" {text}");
                await Task.Delay(100);
            }
        }
        var result = await task;
        var (ok, summary) = describe(result);

        Console.Write("\r  ");
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.Write(ok ? "✓" : "✗");
        Console.ResetColor();
        Console.WriteLine($" {text}: {summary}      ");
        return result;
    }

    // Polish plural: 1 model, 2-4 modele (but 12-14 modeli), 5+ modeli.
    private static string ModelsWord(int n) =>
        n == 1 ? "model" : n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14) ? "modele" : "modeli";

    // Input piped from PowerShell can start with a byte order mark.
    private static string? ReadAnswer() => Console.ReadLine()?.Trim().TrimStart('﻿');

    private static string Gb(double gb) => gb.ToString("0.0", Pl) + " GB";
}
