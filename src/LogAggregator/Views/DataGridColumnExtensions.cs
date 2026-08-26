using System;
using System.Windows;
using System.Windows.Controls;

namespace LogAggregator.Views;

/// <summary>
/// DataGridColumn derives from DependencyObject, not FrameworkElement - it has no Tag property
/// (that's defined on FrameworkElement), which is why setting Tag="..." on a DataGridTextColumn
/// fails at compile time. Attached properties work on any DependencyObject, so this is used
/// instead to tag each column with which SortColumn enum value it corresponds to, for the
/// SQL-driven sort click handler in MainWindow.xaml.cs.
/// </summary>
public static class DataGridColumnExtensions
{
    public static readonly DependencyProperty SortColumnKeyProperty =
        DependencyProperty.RegisterAttached(
            "SortColumnKey",
            typeof(string),
            typeof(DataGridColumnExtensions),
            new PropertyMetadata(null));

    public static string? GetSortColumnKey(DependencyObject obj) => (string?)obj.GetValue(SortColumnKeyProperty);
    public static void SetSortColumnKey(DependencyObject obj, string? value) => obj.SetValue(SortColumnKeyProperty, value);
}
