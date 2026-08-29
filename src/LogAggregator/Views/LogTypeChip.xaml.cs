using System.Windows.Controls;

namespace LogAggregator.Views;

/// <summary>
/// One chip on a Source card - a color swatch, icon glyph, or both (per the bound LogType's
/// DisplayMode), with its own drag-and-drop target, sync spinner, and error badge. Set this
/// control's DataContext to a LogTypeChipViewModel; all behavior lives there and in
/// Common/DragDropBehavior.cs (attached to this control's inner Border in the XAML).
/// </summary>
public partial class LogTypeChip : UserControl
{
    public LogTypeChip()
    {
        InitializeComponent();
    }
}
