using System.Windows.Controls;

namespace LogAggregator.Views;

/// <summary>
/// Interactive timestamp region picker (item 4's fallback UI) - shows a real sample line broken
/// into selectable chunks (see TimestampTokenizer/TimestampPickerViewModel) so the user can pick
/// exactly which chunk(s) are the date/time when auto-detection can't figure it out confidently.
/// Set this control's DataContext to a TimestampPickerViewModel; all behavior lives there.
/// </summary>
public partial class TimestampPickerControl : UserControl
{
    public TimestampPickerControl()
    {
        InitializeComponent();
    }
}
