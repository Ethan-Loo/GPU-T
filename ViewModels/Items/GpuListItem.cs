using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GPU_T.Services;
using Avalonia.Threading;
using Avalonia.Controls;

namespace GPU_T.ViewModels;



public partial class GpuListItem : ObservableObject
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Whether this GPU's sensors are shown on the Sensors tab (multi-select).</summary>
    [ObservableProperty] private bool _isDisplayed;

    public override string ToString() => DisplayName;
}