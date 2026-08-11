using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace GPU_T.Services.SystemMetrics;

/// <summary>A single system-wide sensor reading (CPU/storage/motherboard/fan/power).</summary>
public sealed class SystemReading
{
    public string Name = "";
    public string Unit = "";
    public double Value;
    public bool IsPercent;      // fixed 0..100 scale for the graph
    public string Color = "#4c9a4c";
}

/// <summary>
/// Reads system-wide sensors straight from sysfs/procfs — no root and no external
/// tools (lm-sensors) required: CPU per-core load and clock, hwmon temperatures
/// (CPU package/die, NVMe, motherboard Super-I/O), fan RPM, and RAPL package power.
/// Keeps per-poll state for the CPU-load and energy deltas, so a single instance
/// must be reused across ticks.
/// </summary>
public sealed class SystemSensorReader
{
    private readonly Dictionary<string, (long idle, long total)> _cpuPrev = new();
    private (long energyUj, long stampUs)? _raplPrev;

    /// <summary>Reads and returns the current set of system sensors (order is stable within a boot).</summary>
    public List<SystemReading> Read()
    {
        var list = new List<SystemReading>();
        ReadCpuCores(list);
        ReadHwmon(list);
        ReadRaplPackagePower(list);
        DeduplicateNames(list);
        return list;
    }

    // UpdateSensor() matches rows by Name, so names must be unique; two identically
    // named hwmon sensors (e.g. "nvme: Composite" from two drives) get a stable suffix.
    private static void DeduplicateNames(List<SystemReading> list)
    {
        var seen = new Dictionary<string, int>();
        foreach (var r in list)
        {
            if (seen.TryGetValue(r.Name, out int c))
            {
                seen[r.Name] = c + 1;
                r.Name = $"{r.Name} #{c + 1}";
            }
            else
            {
                seen[r.Name] = 1;
            }
        }
    }

    private void ReadCpuCores(List<SystemReading> list)
    {
        string[] lines;
        try { lines = File.ReadAllLines("/proc/stat"); }
        catch { return; }

        foreach (var line in lines)
        {
            if (!line.StartsWith("cpu") || line.Length < 4 || !char.IsDigit(line[3])) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string cpu = parts[0];                       // e.g. "cpu0"
            long idle = 0, total = 0;
            for (int i = 1; i < parts.Length; i++)
            {
                if (!long.TryParse(parts[i], out long v)) continue;
                total += v;
                if (i == 4 || i == 5) idle += v;         // idle + iowait
            }

            double load = 0;
            if (_cpuPrev.TryGetValue(cpu, out var prev))
            {
                long dt = total - prev.total;
                long di = idle - prev.idle;
                if (dt > 0) load = Math.Clamp(100.0 * (1.0 - (double)di / dt), 0, 100);
            }
            _cpuPrev[cpu] = (idle, total);

            string n = cpu.Substring(3);
            list.Add(new SystemReading { Name = $"CPU Core {n} Load", Unit = "%", Value = Math.Round(load, 1), IsPercent = true });

            double mhz = ReadDouble($"/sys/devices/system/cpu/{cpu}/cpufreq/scaling_cur_freq");
            if (mhz > 0)
                list.Add(new SystemReading { Name = $"CPU Core {n} Clock", Unit = "MHz", Value = Math.Round(mhz / 1000.0), Color = "#7d3cb5" });
        }
    }

    private void ReadHwmon(List<SystemReading> list)
    {
        const string root = "/sys/class/hwmon";
        if (!Directory.Exists(root)) return;

        foreach (var dir in Directory.GetDirectories(root))
        {
            string chip = (ReadText(Path.Combine(dir, "name")) ?? "hwmon").Trim();

            foreach (var input in SafeGlob(dir, "temp*_input"))
            {
                string bn = Path.GetFileName(input);                 // tempN_input
                string idx = bn.Substring(4, bn.Length - 4 - 6);     // N
                string label = ReadText(Path.Combine(dir, $"temp{idx}_label"))?.Trim() ?? $"temp{idx}";
                double milli = ReadDouble(input);
                if (milli == 0 && !File.Exists(input)) continue;
                list.Add(new SystemReading
                {
                    Name = $"{chip}: {label}",
                    Unit = "°C",
                    Value = Math.Round(milli / 1000.0, 1),
                    Color = "#c0392b",
                });
            }

            foreach (var input in SafeGlob(dir, "fan*_input"))
            {
                string bn = Path.GetFileName(input);                 // fanN_input
                string idx = bn.Substring(3, bn.Length - 3 - 6);     // N
                double rpm = ReadDouble(input);
                list.Add(new SystemReading
                {
                    Name = $"{chip}: fan{idx}",
                    Unit = "RPM",
                    Value = Math.Round(rpm),
                    Color = "#2c6fbb",
                });
            }
        }
    }

    private void ReadRaplPackagePower(List<SystemReading> list)
    {
        foreach (var dir in SafeDirs("/sys/class/powercap", "intel-rapl:*"))
        {
            string name = ReadText(Path.Combine(dir, "name"))?.Trim() ?? "";
            if (!name.StartsWith("package")) continue;

            double energyUj = ReadDouble(Path.Combine(dir, "energy_uj"));
            long nowUs = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1_000_000);
            if (_raplPrev is { } prev)
            {
                double dE = energyUj - prev.energyUj;
                double dtS = (nowUs - prev.stampUs) / 1_000_000.0;
                if (dE >= 0 && dtS > 0)
                    list.Add(new SystemReading { Name = "CPU Package Power", Unit = "W", Value = Math.Round(dE / dtS / 1_000_000.0, 1), Color = "#e08b00" });
            }
            _raplPrev = ((long)energyUj, nowUs);
            break;   // first package socket only
        }
    }

    private static IEnumerable<string> SafeGlob(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeDirs(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static double ReadDouble(string path)
    {
        try
        {
            var s = File.ReadAllText(path).Trim();
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch { return 0; }
    }
}
