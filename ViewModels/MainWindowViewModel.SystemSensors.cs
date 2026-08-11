using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using GPU_T.Services.SystemMetrics;

namespace GPU_T.ViewModels;

/// <summary>
/// Additive sensor sources beyond GPU-T's built-in GPU probe: per-channel hotspot /
/// per-module VRAM (optional gputherm MMIO sidecar), system-wide sensors (CPU cores,
/// storage, motherboard, fans, package power), multi-GPU display, and the CPU tab split.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>CPU clocks/temps live on their own tab to keep the Sensors list uncluttered.</summary>
    [ObservableProperty] private ObservableCollection<SensorItemViewModel> _cpuSensors = new();

    private bool _multiGpu;
    // One entry per displayed GPU that exposes hidden sensors: (row-name prefix, PCI bus).
    private readonly List<(string prefix, string bus)> _hiddenEntries = new();

    private readonly List<string> _systemSensorNames = new();
    private readonly SystemSensorReader _systemReader = new();

    // -- displayed-GPU selection -----------------------------------------
    /// <summary>Card ids whose sensors should be shown; falls back to the primary GPU when none are ticked.</summary>
    private List<string> DisplayedGpuIds()
    {
        var ids = new List<string>();
        if (AvailableGpus != null)
            foreach (var g in AvailableGpus)
                if (g.IsDisplayed) ids.Add(g.Id);
        if (ids.Count == 0 && SelectedGpu != null) ids.Add(SelectedGpu.Id);
        if (ids.Count == 0) ids.Add("card0");
        return ids;
    }

    private static string GpuTag(string cardId)
    {
        string digits = new string(cardId.Where(char.IsDigit).ToArray());
        return "GPU" + (digits.Length > 0 ? digits : "0");
    }

    // -- per-GPU row build/update (prefixed when >1 GPU is displayed) -----
    private void AddGpuRows(ObservableCollection<SensorItemViewModel> list, GPU_T.Models.SensorAvailability support, string p)
    {
        list.Add(new SensorItemViewModel(p + "GPU Clock", "MHz", 0, 100, false));
        list.Add(new SensorItemViewModel(p + "Memory Clock", "MHz", 0, 1000, false));
        list.Add(new SensorItemViewModel(p + "GPU Temperature", "°C", 20, 60, false));
        if (support.HasHotSpot) list.Add(new SensorItemViewModel(p + "GPU Temperature (Hot Spot)", "°C", 20, 80, false));
        if (support.HasMemTemp) list.Add(new SensorItemViewModel(p + "Memory Temperature", "°C", 20, 60, false));
        if (support.HasFan) list.Add(new SensorItemViewModel(p + "Fan Speed (%)", "%", 0, 100, true));
        if (support.HasFanRpm) list.Add(new SensorItemViewModel(p + "Fan Speed (RPM)", "RPM", 0, 1000, false));
        if (support.HasGpuLoad) list.Add(new SensorItemViewModel(p + "GPU Load", "%", 0, 100, true));
        if (support.HasEncoderLoad) list.Add(new SensorItemViewModel(p + "Video Encoder Load", "%", 0, 100, true));
        if (support.HasDecoderLoad) list.Add(new SensorItemViewModel(p + "Video Decoder Load", "%", 0, 100, true));
        if (support.HasPcieTx) list.Add(new SensorItemViewModel(p + "PCIe Tx Throughput", "GB/s", 0, 4, false));
        if (support.HasPcieRx) list.Add(new SensorItemViewModel(p + "PCIe Rx Throughput", "GB/s", 0, 4, false));
        if (support.HasMemControllerLoad) list.Add(new SensorItemViewModel(p + "Memory Controller Load", "%", 0, 100, true));
        if (support.HasMemUsed)
        {
            list.Add(new SensorItemViewModel(p + "Memory Used (Dedicated)", "MB", 0, 512, false));
            list.Add(new SensorItemViewModel(p + "Memory Used (Dynamic)", "MB", 0, 128, false));
        }
        if (support.HasPower) list.Add(new SensorItemViewModel(p + "Board Power Draw", "W", 0, 100, false));
        if (support.HasPerfCapReason) list.Add(new SensorItemViewModel(p + "PerfCap Reason", "", 0, 1, true, "#00aa00"));
        if (support.HasVoltage) list.Add(new SensorItemViewModel(p + "GPU Voltage", "V", 0, 1.0, false));
    }

    private void UpdateGpuRows(GPU_T.Models.GpuSensorData data, string p)
    {
        UpdateSensor(p + "GPU Clock", data.GpuClock);
        UpdateSensor(p + "Memory Clock", data.MemoryClock);
        UpdateSensor(p + "GPU Temperature", data.GpuTemp);
        UpdateSensor(p + "GPU Temperature (Hot Spot)", data.GpuHotSpot);
        UpdateSensor(p + "Memory Temperature", data.MemoryTemp);
        UpdateSensor(p + "Fan Speed (%)", (double)data.FanPercent);
        UpdateSensor(p + "Fan Speed (RPM)", (double)data.FanRpm);
        UpdateSensor(p + "GPU Load", (double)data.GpuLoad);
        UpdateSensor(p + "Memory Controller Load", (double)data.MemControllerLoad);
        UpdateSensor(p + "Video Encoder Load", (double)data.EncoderLoad);
        UpdateSensor(p + "Video Decoder Load", (double)data.DecoderLoad);
        UpdateSensor(p + "PCIe Tx Throughput", data.PcieTx);
        UpdateSensor(p + "PCIe Rx Throughput", data.PcieRx);
        UpdateSensor(p + "Memory Used (Dedicated)", data.MemoryUsed);
        UpdateSensor(p + "Memory Used (Dynamic)", data.MemoryUsedDynamic);
        UpdateSensor(p + "Board Power Draw", data.BoardPower);
        string perfCapStr = GPU_T.Services.Probes.LinuxNvidia.LinuxNvidiaPerfCapDecoder.Decode(data.PerfCapReason);
        double perfCapVal = GPU_T.Services.Probes.LinuxNvidia.LinuxNvidiaPerfCapDecoder.GetGraphValue(perfCapStr);
        UpdateSensor(p + "PerfCap Reason", perfCapVal, perfCapStr);
        UpdateSensor(p + "GPU Voltage", data.GpuVoltage);
    }

    // Main-tab dynamic specs track only the primary (selected) GPU.
    private void RecalcDynamicSpecs(GPU_T.Models.GpuSensorData data)
    {
        if (data.NVIDIA_CoreOcOffset != _lastNvidiaCoreOffset || data.NVIDIA_MemOcOffset != _lastNvidiaMemOffset)
        {
            _lastNvidiaCoreOffset = data.NVIDIA_CoreOcOffset;
            _lastNvidiaMemOffset = data.NVIDIA_MemOcOffset;
            if (_currentVendorName == "NVIDIA")
            {
                var s = GPU_T.Services.Probes.LinuxNvidia.LinuxNvidiaGpuProbe.CalculateDynamicSpecs(
                    _rawDefGpuClock, _rawDefBoostClock, _rawDefMemClock,
                    _rawRops, _rawTmus, _rawBusWidth, _rawMemoryType,
                    data.NVIDIA_CoreOcOffset, data.NVIDIA_MemOcOffset);
                GpuClock = s.GpuClock; BoostClock = s.BoostClock; MemoryClock = s.MemClock;
                PixelFillrate = s.PixelFill; TextureFillrate = s.TexFill; Bandwidth = s.Bandwidth;
            }
        }

        if (data.AMD_BoostReadValue != _lastAmdBoostRead || data.AMD_CoreReadValue != _lastAmdCoreRead || data.AMD_MemReadValue != _lastAmdMemRead)
        {
            _lastAmdBoostRead = data.AMD_BoostReadValue;
            _lastAmdCoreRead = data.AMD_CoreReadValue;
            _lastAmdMemRead = data.AMD_MemReadValue;
            if (_currentVendorName == "AMD")
            {
                var s = GPU_T.Services.Probes.LinuxAmd.LinuxAmdGpuProbe.CalculateDynamicSpecs(
                    data.AMD_CoreReadValue, data.AMD_MemReadValue, data.AMD_BoostReadValue,
                    _rawDefGpuClock, _rawDefBoostClock, _rawDefMemClock,
                    _rawRops, _rawTmus, _rawBusWidth, _rawMemoryType);
                GpuClock = s.GpuClock; BoostClock = s.BoostClock; MemoryClock = s.MemClock;
                PixelFillrate = s.PixelFill; TextureFillrate = s.TexFill; Bandwidth = s.Bandwidth;
            }
        }

        if (data.BusInterface != _lastBusInterface)
        {
            _lastBusInterface = data.BusInterface;
            BusInterface = _lastBusInterface;
        }
    }

    private void OnGpuItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GpuListItem.IsDisplayed))
            ChangeGpuReinitSensors();
    }

    // -- per-tick refresh of the additive sources ------------------------
    private void UpdateExtraSensors()
    {
        UpdateHiddenThermalSensors();
        UpdateSystemSensors();
    }

    // -- hidden (MMIO) thermal, one block per displayed GPU --------------
    private void InitHiddenThermalSensors(ObservableCollection<SensorItemViewModel> list, string gpuId, string prefix, bool allowFallback)
    {
        try
        {
            string full = GPU_T.Services.Utilities.GpuFeatureDetection.GetBusId($"/sys/class/drm/{gpuId}/device");
            string shortBus = full.Contains(':') ? full.Substring(full.IndexOf(':') + 1) : full;

            var match = FindHidden(HiddenThermalReader.Read(), shortBus, allowFallback);
            if (match == null) return;

            _hiddenEntries.Add((prefix, match.BusId));
            for (int i = 0; i < match.HotSpotChannels.Count; i++)
            {
                if (double.IsNaN(match.HotSpotChannels[i])) continue;
                list.Add(new SensorItemViewModel($"{prefix}Hotspot Channel {i}", "°C", 20, 90, false, "#e0503c"));
            }
            for (int i = 0; i < match.VramModules.Count; i++)
            {
                if (double.IsNaN(match.VramModules[i])) continue;
                list.Add(new SensorItemViewModel($"{prefix}VRAM Module {i}", "°C", 20, 95, false, "#3c8ce0"));
            }
        }
        catch { }
    }

    private void UpdateHiddenThermalSensors()
    {
        if (_hiddenEntries.Count == 0) return;

        var all = HiddenThermalReader.Read();
        foreach (var (prefix, bus) in _hiddenEntries)
        {
            HiddenGpuThermal? match = null;
            foreach (var h in all)
                if (string.Equals(h.BusId, bus, StringComparison.OrdinalIgnoreCase)) { match = h; break; }
            if (match == null) continue;

            for (int i = 0; i < match.HotSpotChannels.Count; i++)
                if (!double.IsNaN(match.HotSpotChannels[i]))
                    UpdateSensor($"{prefix}Hotspot Channel {i}", match.HotSpotChannels[i]);
            for (int i = 0; i < match.VramModules.Count; i++)
                if (!double.IsNaN(match.VramModules[i]))
                    UpdateSensor($"{prefix}VRAM Module {i}", match.VramModules[i]);
        }
    }

    private static HiddenGpuThermal? FindHidden(List<HiddenGpuThermal> all, string shortBus, bool allowFallback)
    {
        foreach (var h in all)
            if (string.Equals(h.BusId, shortBus, StringComparison.OrdinalIgnoreCase))
                return h;
        return (allowFallback && all.Count > 0) ? all[0] : null;
    }

    // -- system-wide sensors (host, added once) --------------------------
    private void InitSystemSensors(ObservableCollection<SensorItemViewModel> list)
    {
        _systemSensorNames.Clear();
        try
        {
            foreach (var r in _systemReader.Read())
            {
                double lo = r.IsPercent ? 0 : 20;
                list.Add(new SensorItemViewModel(r.Name, r.Unit, lo, 100, r.IsPercent, r.Color));
                _systemSensorNames.Add(r.Name);
            }
        }
        catch { }
    }

    private void UpdateSystemSensors()
    {
        if (_systemSensorNames.Count == 0) return;
        try
        {
            foreach (var r in _systemReader.Read())
                UpdateSensor(r.Name, r.Value);
        }
        catch { }
    }

    // -- thresholds + CPU/Sensors partition ------------------------------
    private static void ApplyThermalThresholds(ObservableCollection<SensorItemViewModel> list)
    {
        foreach (var s in list)
        {
            if (s.Unit != "°C") continue;
            string n = s.Name.ToLowerInvariant();
            if (n.Contains("hot spot") || n.Contains("hotspot")) s.SetThresholds(95, 105);
            else if (n.Contains("vram") || n.Contains("memory")) s.SetThresholds(92, 100);
            else if (n.Contains("cpu")) s.SetThresholds(85, 95);
            else s.SetThresholds(80, 90);
        }
    }

    // Splits the freshly built row set: CPU clocks/temps go to the CPU tab, the rest stay on Sensors.
    private void PartitionSensors(ObservableCollection<SensorItemViewModel> all)
    {
        var main = new ObservableCollection<SensorItemViewModel>();
        var cpu = new ObservableCollection<SensorItemViewModel>();
        foreach (var s in all)
            (IsCpuRow(s.Name) ? cpu : main).Add(s);
        Sensors = main;
        CpuSensors = cpu;
    }

    private static bool IsCpuRow(string name)
    {
        if (name.StartsWith("CPU Core ")) return true;
        if (name == "CPU Temperature") return true;
        string l = name.ToLowerInvariant();
        return l.StartsWith("k10temp:") || l.StartsWith("coretemp:") || l.StartsWith("zenpower:");
    }
}
