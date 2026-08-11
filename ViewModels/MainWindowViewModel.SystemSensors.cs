using System;
using System.Collections.ObjectModel;
using GPU_T.Services.SystemMetrics;

namespace GPU_T.ViewModels;

/// <summary>
/// Additive sensor sources merged into the Sensors tab beyond GPU-T's built-in GPU probe:
/// per-channel hotspot / per-module VRAM (via the optional gputherm MMIO sidecar) and
/// system-wide sensors (CPU cores, storage, motherboard, fans, package power).
/// </summary>
public partial class MainWindowViewModel
{
    private string _hiddenBus = "";
    private int _hotspotChannelCount;
    private int _vramModuleCount;

    private readonly SystemSensorReader _systemReader = new();
    private readonly System.Collections.Generic.List<string> _systemSensorNames = new();

    /// <summary>Adds the extra sensor rows to the freshly built Sensors list (called from InitSensors).</summary>
    private void InitExtraSensors(ObservableCollection<SensorItemViewModel> list, string gpuId)
    {
        InitHiddenThermalSensors(list, gpuId);
        InitSystemSensors(list);
        ApplyThermalThresholds(list);
    }

    /// <summary>Refreshes the extra sensor rows each timer tick (called from SensorTimer_Tick).</summary>
    private void UpdateExtraSensors()
    {
        UpdateHiddenThermalSensors();
        UpdateSystemSensors();
    }

    private void InitHiddenThermalSensors(ObservableCollection<SensorItemViewModel> list, string gpuId)
    {
        _hotspotChannelCount = 0;
        _vramModuleCount = 0;
        _hiddenBus = "";

        try
        {
            string full = GPU_T.Services.Utilities.GpuFeatureDetection.GetBusId($"/sys/class/drm/{gpuId}/device");
            string shortBus = full.Contains(':') ? full.Substring(full.IndexOf(':') + 1) : full;

            var match = FindHidden(HiddenThermalReader.Read(), shortBus);
            if (match == null) return;

            _hiddenBus = match.BusId;

            for (int i = 0; i < match.HotSpotChannels.Count; i++)
            {
                if (double.IsNaN(match.HotSpotChannels[i])) continue;
                _hotspotChannelCount = i + 1;
                list.Add(new SensorItemViewModel($"Hotspot Channel {i}", "°C", 20, 90, false, "#e0503c"));
            }
            for (int i = 0; i < match.VramModules.Count; i++)
            {
                if (double.IsNaN(match.VramModules[i])) continue;
                _vramModuleCount = i + 1;
                list.Add(new SensorItemViewModel($"VRAM Module {i}", "°C", 20, 95, false, "#3c8ce0"));
            }
        }
        catch { }
    }

    private void UpdateHiddenThermalSensors()
    {
        if (_hotspotChannelCount == 0 && _vramModuleCount == 0) return;

        var match = FindHidden(HiddenThermalReader.Read(), _hiddenBus);
        if (match == null) return;

        for (int i = 0; i < match.HotSpotChannels.Count; i++)
            if (!double.IsNaN(match.HotSpotChannels[i]))
                UpdateSensor($"Hotspot Channel {i}", match.HotSpotChannels[i]);
        for (int i = 0; i < match.VramModules.Count; i++)
            if (!double.IsNaN(match.VramModules[i]))
                UpdateSensor($"VRAM Module {i}", match.VramModules[i]);
    }

    private static HiddenGpuThermal? FindHidden(System.Collections.Generic.List<HiddenGpuThermal> all, string shortBus)
    {
        foreach (var h in all)
            if (string.Equals(h.BusId, shortBus, StringComparison.OrdinalIgnoreCase))
                return h;
        return all.Count > 0 ? all[0] : null;
    }

    private void InitSystemSensors(ObservableCollection<SensorItemViewModel> list)
    {
        _systemSensorNames.Clear();
        try
        {
            foreach (var r in _systemReader.Read())
            {
                double lo = r.IsPercent ? 0 : 20;
                double hi = 100;
                list.Add(new SensorItemViewModel(r.Name, r.Unit, lo, hi, r.IsPercent, r.Color));
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
}
