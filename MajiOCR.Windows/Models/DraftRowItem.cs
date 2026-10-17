using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MajiOCR_Windows.Models;

public enum DraftItemStatus
{
    PendingConfirmation, // 待確認
    Confirmed            // 已確認
}

/// <summary>
/// Represents a dynamic draft row generated automatically from an OCR candidate line.
/// Follows RS-ISSUE-0006-v0.2 and AR-ISSUE-0006-v0.1.
/// Retains OCR raw text, original image pixel coordinates, source, generation, and explicit confirmation state.
/// </summary>
public class DraftRowItem : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _originalText = string.Empty;
    private int _x;
    private int _y;
    private int _width;
    private int _height;
    private string _source = "整圖辨識";
    private int _requestGeneration;

    private string _valueKind = CanonicalValueKind.Unclassified;
    private string? _customFieldDefinitionId;
    private string? _customFieldNameSnapshot;
    private string _value = string.Empty;

    private DraftItemStatus _status = DraftItemStatus.PendingConfirmation;
    private DateTimeOffset? _confirmedAt;
    private bool _isUserModified;
    private string? _traditionalSuggestion;
    private string? _suggestionSource;
    private DraftRowSnapshot? _undoSnapshot;
    private int _editVersion;

    public bool CanUndo => _undoSnapshot != null;
    public int EditVersion => _editVersion;
    public void IncrementEditVersion() => _editVersion++;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

        public string OriginalText
    {
        get => _originalText;
        set
        {
            if (SetField(ref _originalText, value))
            {
                OnPropertyChanged(nameof(HasDiff));
                OnPropertyChanged(nameof(HasSuggestion));
                OnPropertyChanged(nameof(CanApplySuggestion));
                OnPropertyChanged(nameof(DisplayOriginalText));
            }
        }
    }

    public int X
    {
        get => _x;
        set
        {
            if (SetField(ref _x, value)) OnPropertyChanged(nameof(DisplayLocation));
        }
    }

    public int Y
    {
        get => _y;
        set
        {
            if (SetField(ref _y, value)) OnPropertyChanged(nameof(DisplayLocation));
        }
    }

    public int Width
    {
        get => _width;
        set
        {
            if (SetField(ref _width, value)) OnPropertyChanged(nameof(DisplayLocation));
        }
    }

    public int Height
    {
        get => _height;
        set
        {
            if (SetField(ref _height, value)) OnPropertyChanged(nameof(DisplayLocation));
        }
    }

    public string Source
    {
        get => _source;
        set
        {
            if (SetField(ref _source, value))
            {
                OnPropertyChanged(nameof(IsRegionSource));
                OnPropertyChanged(nameof(DisplaySourceTag));
            }
        }
    }

    public int RequestGeneration
    {
        get => _requestGeneration;
        set => SetField(ref _requestGeneration, value);
    }

    public DateTimeOffset? ConfirmedAt
    {
        get => _confirmedAt;
        set => SetField(ref _confirmedAt, value);
    }

    private void InvalidateConfirmation()
    {
        if (_status == DraftItemStatus.Confirmed)
        {
            _status = DraftItemStatus.PendingConfirmation;
            _confirmedAt = null;
            _isUserModified = true;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(IsConfirmed));
            OnPropertyChanged(nameof(IsPending));
            OnPropertyChanged(nameof(ConfirmedAt));
            OnPropertyChanged(nameof(IsUserModified));
            OnPropertyChanged(nameof(DisplayStatus));
            OnPropertyChanged(nameof(CanApplySuggestion));
        }
    }

    public string ValueKind
    {
        get => _valueKind;
        set
        {
            if (SetField(ref _valueKind, value))
            {
                InvalidateConfirmation();
                OnPropertyChanged(nameof(DisplayField));
                OnPropertyChanged(nameof(DisplayStatus));
            }
        }
    }

    public string? CustomFieldDefinitionId
    {
        get => _customFieldDefinitionId;
        set
        {
            if (SetField(ref _customFieldDefinitionId, value))
            {
                InvalidateConfirmation();
                OnPropertyChanged(nameof(DisplayField));
                OnPropertyChanged(nameof(DisplayStatus));
            }
        }
    }

    public string? CustomFieldNameSnapshot
    {
        get => _customFieldNameSnapshot;
        set
        {
            if (SetField(ref _customFieldNameSnapshot, value))
            {
                InvalidateConfirmation();
                OnPropertyChanged(nameof(DisplayField));
                OnPropertyChanged(nameof(DisplayStatus));
            }
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value))
            {
                _editVersion++;
                InvalidateConfirmation();
                if (!string.IsNullOrEmpty(_originalText) && !string.Equals(_value, _originalText, StringComparison.Ordinal))
                {
                    _isUserModified = true;
                    OnPropertyChanged(nameof(IsUserModified));
                }
                OnPropertyChanged(nameof(DisplayStatus));
                OnPropertyChanged(nameof(HasDiff));
                OnPropertyChanged(nameof(CanApplySuggestion));
            }
        }
    }

    public DraftItemStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                _editVersion++;
                if (_status == DraftItemStatus.PendingConfirmation)
                {
                    _confirmedAt = null;
                    OnPropertyChanged(nameof(ConfirmedAt));
                }
                else if (_status == DraftItemStatus.Confirmed && _confirmedAt == null)
                {
                    _confirmedAt = DateTimeOffset.UtcNow;
                    OnPropertyChanged(nameof(ConfirmedAt));
                }
                OnPropertyChanged(nameof(IsConfirmed));
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(DisplayStatus));
                OnPropertyChanged(nameof(CanApplySuggestion));
            }
        }
    }

    public bool IsUserModified
    {
        get => _isUserModified;
        set
        {
            if (SetField(ref _isUserModified, value))
            {
                _editVersion++;
                OnPropertyChanged(nameof(DisplayStatus));
                OnPropertyChanged(nameof(CanApplySuggestion));
            }
        }
    }

    public string? TraditionalSuggestion
    {
        get => _traditionalSuggestion;
        set
        {
            if (SetField(ref _traditionalSuggestion, value))
            {
                OnPropertyChanged(nameof(HasSuggestion));
                OnPropertyChanged(nameof(CanApplySuggestion));
                OnPropertyChanged(nameof(SuggestionSourceTag));
            }
        }
    }

    public string? SuggestionSource
    {
        get => _suggestionSource;
        set
        {
            if (SetField(ref _suggestionSource, value))
            {
                OnPropertyChanged(nameof(SuggestionSourceTag));
            }
        }
    }

    // --- Computed UI Helper Properties ---

    public bool IsConfirmed => _status == DraftItemStatus.Confirmed;
    public bool IsPending => _status == DraftItemStatus.PendingConfirmation;
    public bool IsRegionSource => !string.IsNullOrEmpty(_source) && _source.StartsWith("局部辨識", StringComparison.Ordinal);
    public string DisplaySourceTag => IsRegionSource ? "局部辨識" : "整圖辨識";
    public string DisplayLocation => $"X:{X}, Y:{Y} ({Width}x{Height})";
    public string DisplayOriginalText => string.IsNullOrEmpty(OriginalText) ? string.Empty : $"原文: {OriginalText}";
    public bool HasDiff => !string.Equals(Value, OriginalText, StringComparison.Ordinal);

    public bool HasSuggestion => !string.IsNullOrWhiteSpace(_traditionalSuggestion) && !string.Equals(_traditionalSuggestion, OriginalText, StringComparison.Ordinal);
    public bool CanApplySuggestion => HasSuggestion && !_isUserModified && _status != DraftItemStatus.Confirmed;
    public string SuggestionSourceTag => !string.IsNullOrEmpty(_suggestionSource) ? _suggestionSource : "繁體建議";

    public string DisplayField => CanonicalValueKind.GetDisplayName(ValueKind, CustomFieldNameSnapshot);

    public string DisplayStatus
    {
        get
        {
            if (_status == DraftItemStatus.Confirmed) return "已確認";
            if (_isUserModified) return "已人工修改";
            return "待確認";
        }
    }

    public void Confirm()
    {
        _confirmedAt = DateTimeOffset.UtcNow;
        Status = DraftItemStatus.Confirmed;
        OnPropertyChanged(nameof(ConfirmedAt));
    }

    public void Unconfirm()
    {
        _confirmedAt = null;
        Status = DraftItemStatus.PendingConfirmation;
        OnPropertyChanged(nameof(ConfirmedAt));
    }

    public void MarkModified()
    {
        IsUserModified = true;
    }

    public void ApplySuggestion()
    {
        if (!CanApplySuggestion || string.IsNullOrWhiteSpace(_traditionalSuggestion)) return;
        Value = _traditionalSuggestion;
        MarkModified();
        OnPropertyChanged(nameof(CanApplySuggestion));
    }

    public void SaveUndoSnapshot()
    {
        _undoSnapshot = new DraftRowSnapshot
        {
            Value = Value,
            OriginalText = OriginalText,
            Status = Status,
            ConfirmedAt = ConfirmedAt,
            IsUserModified = IsUserModified,
            Source = Source,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            TraditionalSuggestion = TraditionalSuggestion,
            SuggestionSource = SuggestionSource
        };
        OnPropertyChanged(nameof(CanUndo));
    }

    public bool RestoreUndo()
    {
        if (_undoSnapshot == null) return false;
        _value = _undoSnapshot.Value;
        _originalText = _undoSnapshot.OriginalText;
        _status = _undoSnapshot.Status;
        _confirmedAt = _undoSnapshot.ConfirmedAt;
        _isUserModified = _undoSnapshot.IsUserModified;
        _source = _undoSnapshot.Source;
        _x = _undoSnapshot.X;
        _y = _undoSnapshot.Y;
        _width = _undoSnapshot.Width;
        _height = _undoSnapshot.Height;
        _traditionalSuggestion = _undoSnapshot.TraditionalSuggestion;
        _suggestionSource = _undoSnapshot.SuggestionSource;
        _undoSnapshot = null;
        _editVersion++;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(OriginalText));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ConfirmedAt));
        OnPropertyChanged(nameof(IsConfirmed));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsUserModified));
        OnPropertyChanged(nameof(DisplayStatus));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(CanApplySuggestion));
        return true;
    }

    public void ClearUndo()
    {
        _undoSnapshot = null;
        OnPropertyChanged(nameof(CanUndo));
    }

    public ObservableCollection<DraftFieldOption> AvailableOptions { get; } = new();

    private DraftFieldOption? _selectedOption;
    public DraftFieldOption? SelectedOption
    {
        get => _selectedOption;
        set => SetField(ref _selectedOption, value);
    }

    /// <summary>
    /// Synchronizes this row's available options based on active definitions and preserves
    /// the custom definition snapshot and ID if it was deactivated or renamed in the catalog.
    /// </summary>
    public void SyncOptions(
        IEnumerable<CustomFieldDefinition> activeDefinitions,
        DraftFieldOption actionOption)
    {
        AvailableOptions.Clear();

        // 1. All standard canonical kinds
        foreach (var kind in CanonicalValueKind.AllStandardKinds)
        {
            AvailableOptions.Add(DraftFieldOption.CreateStandard(kind));
        }

        // 2. Active custom definitions from catalog
        var activeList = activeDefinitions.ToList();
        bool foundCurrentInActive = false;

        foreach (var def in activeList)
        {
            if (ValueKind == CanonicalValueKind.Custom &&
                string.Equals(CustomFieldDefinitionId, def.Id, StringComparison.OrdinalIgnoreCase))
            {
                foundCurrentInActive = true;
                // Preserve row's custom snapshot name if set; else use def.Name
                string displayName = !string.IsNullOrEmpty(CustomFieldNameSnapshot) ? CustomFieldNameSnapshot : def.Name;
                AvailableOptions.Add(DraftFieldOption.CreateCustom(def.Id, displayName));
            }
            else
            {
                AvailableOptions.Add(DraftFieldOption.CreateCustom(def));
            }
        }

        // 3. Deactivated or missing custom field definition:
        // If this row is set to Custom and its definition is NOT in the active catalog list,
        // retain a snapshot item specifically in this row so the ComboBox displays correctly and preserves its ID!
        if (ValueKind == CanonicalValueKind.Custom &&
            !string.IsNullOrEmpty(CustomFieldDefinitionId) &&
            !foundCurrentInActive)
        {
            string snapshotName = !string.IsNullOrEmpty(CustomFieldNameSnapshot) ? CustomFieldNameSnapshot : "自訂項目";
            AvailableOptions.Add(DraftFieldOption.CreateCustom(CustomFieldDefinitionId, snapshotName));
        }

        // 4. Action item "+ 新增自訂名稱…"
        AvailableOptions.Add(actionOption);

        // 5. Select matching option
        var match = AvailableOptions.FirstOrDefault(opt =>
            !opt.IsActionItem &&
            opt.ValueKind == ValueKind &&
            (ValueKind != CanonicalValueKind.Custom || string.Equals(opt.CustomFieldDefinitionId, CustomFieldDefinitionId, StringComparison.OrdinalIgnoreCase))
        );

        SelectedOption = match;
    }

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
