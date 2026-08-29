using System;
using System.Windows;
using System.Windows.Input;

namespace LogAggregator.Common;

/// <summary>
/// Attached-property drag-and-drop support for elements inside a DataTemplate, which can't have
/// named code-behind event handlers of their own (SourceCardTemplate.xaml is a
/// ResourceDictionary, not a Window/UserControl with an x:Class). Set DropCommand on any
/// FrameworkElement to make it a drop target - it receives the dropped file paths (string[]) as
/// the command parameter. IsDragOver is toggled automatically while a drag hovers the element,
/// for animation triggers (see Themes/Animations.xaml's Storyboard.DragPulse).
///
/// Drop is a routed event that bubbles: this only marks it Handled once a command actually
/// executes, so a drop that lands on a LogType chip is consumed there and never reaches the
/// card's own DropCommand behind it - this is what makes "drop on the icon" vs. "drop anywhere
/// else on the card" work with no manual hit-testing. DragEnter/DragLeave are deliberately left
/// unhandled so they keep bubbling - the whole card visually pulses while a drag hovers any part
/// of it (including a chip), not just the exact pixel under the cursor.
/// </summary>
public static class DragDropBehavior
{
    public static readonly DependencyProperty DropCommandProperty = DependencyProperty.RegisterAttached(
        "DropCommand", typeof(ICommand), typeof(DragDropBehavior), new PropertyMetadata(null, OnDropCommandChanged));

    public static void SetDropCommand(DependencyObject element, ICommand? value) => element.SetValue(DropCommandProperty, value);
    public static ICommand? GetDropCommand(DependencyObject element) => (ICommand?)element.GetValue(DropCommandProperty);

    public static readonly DependencyProperty IsDragOverProperty = DependencyProperty.RegisterAttached(
        "IsDragOver", typeof(bool), typeof(DragDropBehavior), new PropertyMetadata(false));

    public static void SetIsDragOver(DependencyObject element, bool value) => element.SetValue(IsDragOverProperty, value);
    public static bool GetIsDragOver(DependencyObject element) => (bool)element.GetValue(IsDragOverProperty);

    private static void OnDropCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        element.DragEnter -= Element_DragEnter;
        element.DragLeave -= Element_DragLeave;
        element.Drop -= Element_Drop;

        if (e.NewValue is ICommand)
        {
            element.AllowDrop = true;
            element.DragEnter += Element_DragEnter;
            element.DragLeave += Element_DragLeave;
            element.Drop += Element_Drop;
        }
    }

    private static void Element_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        if (sender is DependencyObject d) SetIsDragOver(d, true);
    }

    private static void Element_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is DependencyObject d) SetIsDragOver(d, false);
    }

    private static void Element_Drop(object sender, DragEventArgs e)
    {
        if (sender is DependencyObject d) SetIsDragOver(d, false);

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Handled) return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var command = sender is DependencyObject dep ? GetDropCommand(dep) : null;

        if (command is not null && command.CanExecute(paths))
        {
            command.Execute(paths);
            e.Handled = true;
        }
    }
}
