using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace GPU_T.Services.SystemMetrics;

/// <summary>
/// One GPU's high-granularity thermal readout parsed from the gputherm MMIO sidecar.
/// </summary>
public sealed class HiddenGpuThermal
{
    public string BusId = "";              // short PCI slot, e.g. "08:00.0"
    public string Name = "";
    public double Core;
    public double HotSpot;
    public double Vram;
    public List<double> HotSpotChannels = new();   // NaN entries = slot not wired/invalid
    public List<double> VramModules = new();
}

/// <summary>
/// Optional data source: invokes the external <c>gputherm</c>/<c>gputherm-rs</c> binary
/// (direct BAR0 MMIO reader) and parses per-channel hotspot and per-module VRAM
/// temperatures that NVML/NVAPI do not expose. If the binary is absent or root access
/// is unavailable, <see cref="Read"/> returns an empty list and the UI omits these rows.
/// </summary>
public static class HiddenThermalReader
{
    private static readonly Regex LineRe = new(
        @"^\[(?<slot>[0-9a-fA-F]{2}:[0-9a-fA-F]{2}\.[0-9a-fA-F])\]\s+(?<name>.*?)\s+core=(?<core>-?\d+)C\s+hotspot=(?<hs>-?\d+)C\s+vram=(?<vr>-?\d+)C",
        RegexOptions.Compiled);
    private static readonly Regex ChRe = new(@"hotspot_ch:\s*(?<vals>[0-9 \-]+?)\)", RegexOptions.Compiled);
    private static readonly Regex ModRe = new(@"(?:vram_mod|modules):\s*(?<vals>[0-9 \-]+?)\)", RegexOptions.Compiled);

    /// <summary>Finds the gputherm binary via GPUTHERM_BIN, common install dirs, or the app directory.</summary>
    public static string? LocateBinary()
    {
        var env = Environment.GetEnvironmentVariable("GPUTHERM_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".local", "bin", "gputherm-rs"),
            Path.Combine(home, ".local", "bin", "gputherm"),
            "/usr/local/bin/gputherm-rs",
            "/usr/local/bin/gputherm",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gputherm-rs"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gputherm"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>True when a gputherm binary is present (regardless of whether root is available).</summary>
    public static bool IsBinaryPresent() => LocateBinary() != null;

    public static List<HiddenGpuThermal> Read(int timeoutMs = 900)
    {
        var results = new List<HiddenGpuThermal>();
        string? bin = LocateBinary();
        if (bin == null) return results;

        // Privileged path first (needs the scoped NOPASSWD sudoers rule), then a
        // direct call in case the app itself is already running as root.
        string output = RunOnce("sudo", $"-n \"{bin}\" --once", timeoutMs);
        if (string.IsNullOrWhiteSpace(output))
            output = RunOnce(bin, "--once", timeoutMs);
        if (string.IsNullOrWhiteSpace(output)) return results;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var m = LineRe.Match(line);
            if (!m.Success) continue;

            var g = new HiddenGpuThermal
            {
                BusId = m.Groups["slot"].Value.ToLowerInvariant(),
                Name = m.Groups["name"].Value.Trim(),
                Core = ParseD(m.Groups["core"].Value),
                HotSpot = ParseD(m.Groups["hs"].Value),
                Vram = ParseD(m.Groups["vr"].Value),
            };
            var chm = ChRe.Match(line);
            if (chm.Success) g.HotSpotChannels = ParseList(chm.Groups["vals"].Value);
            var mmm = ModRe.Match(line);
            if (mmm.Success) g.VramModules = ParseList(mmm.Groups["vals"].Value);
            results.Add(g);
        }
        return results;
    }

    private static List<double> ParseList(string s)
    {
        var outp = new List<double>();
        foreach (var tok in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok == "--") { outp.Add(double.NaN); continue; }
            outp.Add(int.TryParse(tok, out int v) ? v : double.NaN);
        }
        return outp;
    }

    private static double ParseD(string s) => double.TryParse(s, out var v) ? v : 0;

    private static string RunOnce(string file, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
            return p.ExitCode == 0 ? outp : "";
        }
        catch { return ""; }
    }
}
