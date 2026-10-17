using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace MajiOCR_Windows.Models;

/// <summary>
/// Root model for user custom field catalog stored in custom-fields.json.
/// Version 1 specification according to RS-ISSUE-0006-v0.2 and DBA-ISSUE-0006-v0.1.
/// </summary>
public class CustomFieldCatalog
{
    [JsonPropertyName("catalogVersion")]
    public int CatalogVersion { get; set; } = 1;

    [JsonPropertyName("definitions")]
    public List<CustomFieldDefinition> Definitions { get; set; } = new();
}

/// <summary>
/// Individual custom field definition.
/// Stores only metadata (id, name, active, createdAt), never card content or OCR full text.
/// Implements INotifyPropertyChanged to update UI bindings on rename or activation change.
/// </summary>
public class CustomFieldDefinition : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private bool _active = true;
    private string _createdAt = string.Empty;

    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    [JsonPropertyName("active")]
    public bool Active
    {
        get => _active;
        set => SetField(ref _active, value);
    }

    [JsonPropertyName("createdAt")]
    public string CreatedAt
    {
        get => _createdAt;
        set => SetField(ref _createdAt, value);
    }

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
