using Avalonia.Controls;
using Avalonia.Input;
using GPU_T.ViewModels;

namespace GPU_T.Views;

/// <summary>CPU tab: same interactive sparkline rows as the Sensors view, bound to CpuSensors.</summary>
public partial class CpuView : UserControl
{
    public CpuView()
    {
        InitializeComponent();
    }

    private void Graph_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Control control && control.DataContext is SensorItemViewModel vm)
            vm.ShowHistoryAt(e.GetPosition(control).X, control.Bounds.Width);
    }

    private void Graph_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control control && control.DataContext is SensorItemViewModel vm)
            vm.StopHovering();
    }
}
