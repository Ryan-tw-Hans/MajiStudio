using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MajiOCR_Windows.Models;

/// <summary>
/// In-memory contact draft model for W1 offline slice.
/// Follows RS-ISSUE-0005-W1 and DBC-ISSUE-0005-v0.2.
/// </summary>
public class ContactDraft : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _company = string.Empty;
    private string _title = string.Empty;
    private string _website = string.Empty;
    private string _address = string.Empty;
    private string _note = string.Empty;
    private bool _isModified;

    public ContactDraft()
    {
        Phones = new ObservableCollection<ContactFieldItem>();
        Emails = new ObservableCollection<ContactFieldItem>();
        Tags = new ObservableCollection<string>();

        Phones.CollectionChanged += OnCollectionChanged;
        Emails.CollectionChanged += OnCollectionChanged;
        Tags.CollectionChanged += OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        IsModified = true;
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value)) IsModified = true;
        }
    }

    public string Company
    {
        get => _company;
        set
        {
            if (SetField(ref _company, value)) IsModified = true;
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (SetField(ref _title, value)) IsModified = true;
        }
    }

    public ObservableCollection<ContactFieldItem> Phones { get; }

    public ObservableCollection<ContactFieldItem> Emails { get; }

    public string Website
    {
        get => _website;
        set
        {
            if (SetField(ref _website, value)) IsModified = true;
        }
    }

    public string Address
    {
        get => _address;
        set
        {
            if (SetField(ref _address, value)) IsModified = true;
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            if (SetField(ref _note, value)) IsModified = true;
        }
    }

    public ObservableCollection<string> Tags { get; }

    public bool IsModified
    {
        get => _isModified;
        set => SetField(ref _isModified, value);
    }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name) &&
        string.IsNullOrWhiteSpace(Company) &&
        string.IsNullOrWhiteSpace(Title) &&
        Phones.Count == 0 &&
        Emails.Count == 0 &&
        string.IsNullOrWhiteSpace(Website) &&
        string.IsNullOrWhiteSpace(Address) &&
        string.IsNullOrWhiteSpace(Note) &&
        Tags.Count == 0;

    public void Clear()
    {
        _name = string.Empty;
        _company = string.Empty;
        _title = string.Empty;
        _website = string.Empty;
        _address = string.Empty;
        _note = string.Empty;

        Phones.Clear();
        Emails.Clear();
        Tags.Clear();

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Company));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Website));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(IsEmpty));

        IsModified = false;
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
        OnPropertyChanged(nameof(IsEmpty));
        return true;
    }
}
