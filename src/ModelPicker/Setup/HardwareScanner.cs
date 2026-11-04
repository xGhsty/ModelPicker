using System.Diagnostics;
using System.Globalization;

namespace ModelPicker.Setup;

public record GpuInfo(string Name, double VramGb);

public record HardwareInfo(IReadOnlyList<GpuInfo> Gpus, double RamGb, int CpuThreads)
{
    /// <summary>Ollama can split a model across several GPUs, so their memory adds up.</summary>
    public double VramGb => Gpus.Sum(g => g.VramGb);
}

/// <summary>Detects GPU memory (NVIDIA, via nvidia-smi) and system RAM.</summary>
public static class HardwareScanner
{
    public static HardwareInfo Scan() => new(ScanNvidiaGpus(), ScanRamGb(), Environment.ProcessorCount);

    public static double ScanRamGb() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024d * 1024 * 1024);

    public static List<GpuInfo> ScanNvidiaGpus()
    {
        var gpus = new List<GpuInfo>();
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return gpus;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mib))
                    gpus.Add(new GpuInfo(parts[0], mib / 1024));
            }
        }
        catch (Exception)
        {
            // No NVIDIA GPU or driver: the models will run on CPU.
        }
        return gpus;
    }
}
