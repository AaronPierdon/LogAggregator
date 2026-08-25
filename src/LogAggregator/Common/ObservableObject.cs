using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LogAggregator.Common;

/// <summary>
/// Minimal INotifyPropertyChanged base class. Hand-rolled (rather than pulling in a
/// third-party MVVM package) to keep the dependency surface as small as possible for a
/// self-contained single-file publish.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
