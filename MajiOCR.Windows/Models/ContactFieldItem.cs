using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MajiOCR_Windows.Models;

/// <summary>
/// Represents a structured field value (e.g. Phone, Email) following DBC-ISSUE-0005-v0.2.
/// </summary>
public class ContactFieldItem : INotifyPropertyChanged
{
    private string _kind = string.Empty;
    private string _label = string.Empty;
    private string _value = string.Empty;
    private int _ordinal;

    public string Kind
    {
        get => _kind;
        set => SetField(ref _kind, value);
    }

    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    public string Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }

    public int Ordinal
    {
        get => _ordinal;
        set => SetField(ref _ordinal, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
