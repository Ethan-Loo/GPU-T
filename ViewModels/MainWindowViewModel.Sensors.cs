using System;
using System.IO;
using System.Linq;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using GPU_T.Services;

namespace GPU_T.ViewModels;


/// <summary>
/// Partial view model responsible for GPU/CPU sensor management and logging orchestration.
/// </summary>
public partial class MainWindowViewModel
{
    private DispatcherTimer _sensorTimer;
    private string _logFilePath = "";

    /// <summary>
    /// Backing field for the generated Sensors property.
    /// Contains the collection of sensor view models displayed in the UI.
    /// </summary>
    [ObservableProperty] private ObservableCollection<SensorItemViewModel> _sensors;

    /// <summary>
    /// Backing field for the generated IsLogEnabled property.
    /// Indicates whether periodic sensor data logging is active.
    /// </summary>
    [ObservableProperty] private bool _isLogEnabled;
    
    /// <summary>
    /// Backing field for the generated RefreshRates property.
    /// Provides selectable refresh intervals for sensor polling.
    /// </summary>
    [ObservableProperty] private ObservableCollection<RefreshRateItem> _refreshRates = new()
    {
        new RefreshRateItem { Label = "0.1 s", Seconds = 0.1 },
        new RefreshRateItem { Label = "0.2 s", Seconds = 0.2 },
        new RefreshRateItem { Label = "0.5 s", Seconds = 0.5 },
        new RefreshRateItem { Label = "1.0 s", Seconds = 1.0 },
        new RefreshRateItem { Label = "2.0 s", Seconds = 2.0 },
        new RefreshRateItem { Label = "5.0 s", Seconds = 5.0 },
        new RefreshRateItem { Label = "10.0 s", Seconds = 10.0 },
    };
    
    /// <summary>
    /// Backing field for the generated SelectedRefreshRate property.
    /// When changed, updates the internal polling timer interval.
    /// </summary>
    [ObservableProperty] private RefreshRateItem _selectedRefreshRate;

    /// <summary>
    /// Called by the source generator when SelectedRefreshRate changes.
    /// Updates the dispatcher's timer interval to reflect the selected rate.
    /// </summary>
    /// <param name="value">The newly selected refresh rate item.</param>
    partial void OnSelectedRefreshRateChanged(RefreshRateItem value)
    {
        if (_sensorTimer != null && value != null)
        {
            _sensorTimer.Interval = TimeSpan.FromSeconds(value.Seconds);
        }
    }

    /// <summary>
    /// Resets all sensor items to their initial state.
    /// Intended to be invoked from the UI command infrastructure.
    /// </summary>
    [RelayCommand]
    private void ResetSensors()
    {
        if (Sensors != null)
            foreach (var sensor in Sensors)
                sensor.Reset();
        if (CpuSensors != null)
            foreach (var sensor in CpuSensors)
                sensor.Reset();
    }

    /// <summary>
    /// Starts CSV logging to the specified file path and writes the CSV header.
    /// </summary>
    /// <param name="filePath">Filesystem path to append log rows.</param>
    public void StartLogging(string filePath)
    {
        _logFilePath = filePath;
        IsLogEnabled = true;
        WriteLogHeader();
    }

    /// <summary>
    /// Stops logging and clears the internal log file path.
    /// </summary>
    public void StopLogging()
    {
        IsLogEnabled = false;
        _logFilePath = "";
    }

    private void InitSensors()
    {
        var list = new ObservableCollection<SensorItemViewModel>();
        _hiddenEntries.Clear();

        var displayed = DisplayedGpuIds();
        _multiGpu = displayed.Count > 1;

        foreach (var id in displayed)
        {
            var probe = GpuProbeFactory.Create(id);
            var support = probe.GetSensorAvailability();
            string prefix = _multiGpu ? GpuTag(id) + " " : "";
            AddGpuRows(list, support, prefix);
            InitHiddenThermalSensors(list, id, prefix, allowFallback: !_multiGpu);
        }

        list.Add(new SensorItemViewModel("CPU Temperature", "°C", 20, 70, false));
        list.Add(new SensorItemViewModel("System Memory Used", "MB", 0, 4096, false));

        InitSystemSensors(list);
        ApplyThermalThresholds(list);
        PartitionSensors(list);

        // Safely read the UI selection, falling back to 1.0s on initial startup
        double intervalSeconds = SelectedRefreshRate?.Seconds ?? 1.0;

        _sensorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(intervalSeconds)
        };
        _sensorTimer.Tick += SensorTimer_Tick;
        _sensorTimer.Start();
    }

    private void ChangeGpuReinitSensors()
    {
        if (_sensorTimer != null)
        {
            _sensorTimer.Stop();
            _sensorTimer.Tick -= SensorTimer_Tick;
            _sensorTimer = null;
        }
        Sensors = null;

        InitSensors();

        if (IsLogEnabled)
        {
            WriteLogHeader();
        }

    }

    private void SensorTimer_Tick(object? sender, EventArgs e)
    {
        var displayed = DisplayedGpuIds();
        string primaryId = _selectedGpu?.Id ?? (displayed.Count > 0 ? displayed[0] : "");
        GPU_T.Models.GpuSensorData? primary = null;

        foreach (var id in displayed)
        {
            var probe = GpuProbeFactory.Create(id, MemoryType);
            var data = probe.LoadSensorData();
            string prefix = _multiGpu ? GpuTag(id) + " " : "";
            UpdateGpuRows(data, prefix);
            if (id == primaryId) primary = data;
        }

        UpdateExtraSensors();

        if (primary != null)
        {
            UpdateSensor("CPU Temperature", primary.CpuTemperature);
            UpdateSensor("System Memory Used", primary.SystemRamUsed);
            RecalcDynamicSpecs(primary);
        }

        if (IsLogEnabled && !string.IsNullOrEmpty(_logFilePath) && Sensors != null)
        {
            try
            {
                string row = SensorLogService.BuildDataRow(Sensors.Concat(CpuSensors));
                File.AppendAllText(_logFilePath, row + Environment.NewLine);
            }
            catch
            {
                // Ignore IO locking scenarios during append; logging should not disrupt runtime sensor updates.
            }
        }
    }

    private void UpdateSensor(string name, double value, string? textValue = null)
    {
        var sensor = Sensors?.FirstOrDefault(s => s.Name == name)
                     ?? CpuSensors?.FirstOrDefault(s => s.Name == name);
        if (sensor != null) sensor.UpdateValue(value, textValue);
    }

    private void WriteLogHeader()
    {
        if (!IsLogEnabled || string.IsNullOrEmpty(_logFilePath) || Sensors == null) return;
        try
        {
            string header = SensorLogService.BuildHeader(Sensors.Concat(CpuSensors));
            File.AppendAllText(_logFilePath, header + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Log file write failures disable logging to avoid repeated errors; surface the error to the console for diagnostics.
            Console.WriteLine($"Log write error: {ex.Message}");
            StopLogging();
        }
    }
}