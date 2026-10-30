using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Media.Imaging;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged
{
    private readonly ImageService _imageService = new();
    private readonly OcrService _ocrService;
    private readonly HashSet<DraftRowItem> _observedRows = new();

    private IStorageService? _storageService;
    private string _currentContactId = Guid.NewGuid().ToString("N");
    private int _currentRevision = 0;
    private string _currentSaveOperationId = Guid.NewGuid().ToString("N");
    private string? _lastSavedFingerprint;
    private bool _isSaving;
    private bool _isCurrentContactDeleted;

    public static bool IsHeadlessEnvironment { get; set; } = false;
    public static Func<byte[], Task<BitmapImage?>>? DisplayImageLoader { get; set; }
    public bool IsCurrentContactDeleted => _isCurrentContactDeleted;

    private CustomFieldCatalogService _catalogService;
    private readonly IAppSettingsService _settingsService;
    private ContactDraft _draft = new();
    private ImageSessionInfo? _currentSession;
    private BitmapImage? _displayImage;
    private bool _isBusy;
    private string _busyMessage = string.Empty;
    private string _statusMessage = string.Empty;
    private string _lastOcrSummary = string.Empty;

    // Viewport and zoom/pan state (conforming to RS-ISSUE-0008-v0.1 / DP-ISSUE-0008-v0.1)
    private double _viewportWidth = 800;
    private double _viewportHeight = 600;
    private double _zoomScale = 1.0;
    private double _panX = 0;
    private double _panY = 0;
    private bool _isFitMode = true;

    // Region selection state (original image pixel coordinates)
    private bool _isRegionMode;
    private int _regionX = 0;
    private int _regionY = 0;
    private int _regionWidth = 0;
    private int _regionHeight = 0;

    // Targeted field selection for per-field ROI OCR
    private DraftRowItem? _selectedTargetField;

    // Language selection state (conforming to RS-ISSUE-0009 / AR-ISSUE-0009 / DP-ISSUE-0009 / RS-ISSUE-0036)
    private OcrLanguageOption _selectedFullOcrLanguage;
    private OcrLanguageOption _selectedPartialOcrLanguage;

    public enum ActiveOcrKind { None, Full, Region }
    private ActiveOcrKind _activeOcrKind = ActiveOcrKind.None;

    public IAppSettingsService SettingsService => _settingsService;
    public IReadOnlyList<OcrLanguageOption> LanguageOptions => OcrLanguageCatalog.AllOptions;

    public OcrLanguageOption SelectedFullOcrLanguage
    {
        get => _selectedFullOcrLanguage;
        set
        {
            if (value != null && SetField(ref _selectedFullOcrLanguage, value))
            {
                try
                {
                    _settingsService?.UpdateFullOcrLanguage(value.Code);
                }
                catch { }

                if (_activeOcrKind == ActiveOcrKind.Full)
                {
                    CancelCurrentOcr();
                    StatusMessage = $"整圖辨識語系已切換為〔{value.DisplayName}〕，已取消進行中的整圖辨識。";
                }
            }
        }
    }

    public OcrLanguageOption SelectedPartialOcrLanguage
    {
        get => _selectedPartialOcrLanguage;
        set
        {
            if (value != null && SetField(ref _selectedPartialOcrLanguage, value))
            {
                try
                {
                    _settingsService?.UpdatePartialOcrLanguage(value.Code);
                }
                catch { }

                if (_activeOcrKind == ActiveOcrKind.Region)
                {
                    CancelCurrentOcr();
                    StatusMessage = $"局部辨識語系已切換為〔{value.DisplayName}〕，已取消進行中的局部辨識。";
                }
            }
        }
    }

    private int _currentRequestId = 0;
    private int _activeFullRequestId = 0;
    private int _activeRegionRequestId = 0;
    private CancellationTokenSource? _cts;

    // Fixed single-value field projection slots (always visible in UI, projected from DraftRows)
    private DraftRowItem _nameRow;
    private DraftRowItem _companyRow;
    private DraftRowItem _titleRow;
    private DraftRowItem _websiteRow;
    private DraftRowItem _addressRow;
    private DraftRowItem _noteRow;

    public MainViewModel(
        CustomFieldCatalogService? catalogService = null,
        OcrService? ocrService = null,
        IStorageService? storageService = null,
        IAppSettingsService? settingsService = null)
    {
        if (catalogService != null)
        {
            _catalogService = catalogService;
        }
        else if (IsHeadlessEnvironment)
        {
            string tempCatalog = Path.Combine(Path.GetTempPath(), "MajiOCR.Windows.Headless", "custom-fields.json");
            _catalogService = new CustomFieldCatalogService(tempCatalog);
        }
        else
        {
            _catalogService = new CustomFieldCatalogService();
        }

        if (settingsService != null)
        {
            _settingsService = settingsService;
        }
        else if (IsHeadlessEnvironment)
        {
            string tempSettings = Path.Combine(Path.GetTempPath(), "MajiOCR.Windows.Headless", $"settings-{Guid.NewGuid():N}.json");
            _settingsService = new AppSettingsService(tempSettings);
        }
        else
        {
            _settingsService = new AppSettingsService();
        }

        var initialSettings = _settingsService.LoadSettings();
        _selectedFullOcrLanguage = OcrLanguageCatalog.GetOptionByCode(initialSettings.FullOcrLanguageCode);
        _selectedPartialOcrLanguage = OcrLanguageCatalog.GetOptionByCode(initialSettings.PartialOcrLanguageCode);

        _ocrService = ocrService ?? new OcrService();
        _storageService = storageService;
        Candidates = new ObservableCollection<OcrCandidateItem>();
        DraftRows = new ObservableCollection<DraftRowItem>();
        FieldOptions = new ObservableCollection<DraftFieldOption>();

        _nameRow = CreateEmptyFixedRow(CanonicalValueKind.Name);
        _companyRow = CreateEmptyFixedRow(CanonicalValueKind.Company);
        _titleRow = CreateEmptyFixedRow(CanonicalValueKind.Title);
        _websiteRow = CreateEmptyFixedRow(CanonicalValueKind.Website);
        _addressRow = CreateEmptyFixedRow(CanonicalValueKind.Address);
        _noteRow = CreateEmptyFixedRow(CanonicalValueKind.Note);

        ObserveRow(_nameRow);
        ObserveRow(_companyRow);
        ObserveRow(_titleRow);
        ObserveRow(_websiteRow);
        ObserveRow(_addressRow);
        ObserveRow(_noteRow);

        DraftRows.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (DraftRowItem item in e.NewItems)
                {
                    ObserveRow(item);
                }
            }
            if (e.OldItems != null)
            {
                foreach (DraftRowItem item in e.OldItems)
                {
                    // A row may still be displayed in a fixed slot after leaving DraftRows.
                    // SyncProjections removes subscriptions once no projection uses it.
                }
            }
            SyncProjections();
            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(DraftRowsCountText));
        };

        RefreshFieldOptions();
        SyncProjections();
    }

    private void OnDraftRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is DraftRowItem row)
        {
            if (e.PropertyName == nameof(DraftRowItem.Value) && !string.IsNullOrEmpty(row.Value))
            {
                EnsureRowInDraft(row);
            }
        }
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void ObserveRow(DraftRowItem row)
    {
        if (_observedRows.Add(row)) row.PropertyChanged += OnDraftRowPropertyChanged;
    }

    private void ForgetDetachedRows()
    {
        foreach (var row in _observedRows.ToArray())
        {
            if (DraftRows.Contains(row) ||
                ReferenceEquals(row, _nameRow) || ReferenceEquals(row, _companyRow) ||
                ReferenceEquals(row, _titleRow) || ReferenceEquals(row, _websiteRow) ||
                ReferenceEquals(row, _addressRow) || ReferenceEquals(row, _noteRow) ||
                PhoneRows.Contains(row) || EmailRows.Contains(row))
            {
                continue;
            }

            row.PropertyChanged -= OnDraftRowPropertyChanged;
            _observedRows.Remove(row);
        }
    }

    public CustomFieldCatalogService CatalogService
    {
        get => _catalogService;
        set
        {
            if (SetField(ref _catalogService, value))
            {
                RefreshFieldOptions();
            }
        }
    }

    public ContactDraft Draft
    {
        get => _draft;
        set => SetField(ref _draft, value);
    }

    public ObservableCollection<OcrCandidateItem> Candidates { get; }

    public ObservableCollection<DraftRowItem> DraftRows { get; }

    public ObservableCollection<DraftFieldOption> FieldOptions { get; }

    // --- Fixed 8 Fields and Categorized Projections (ISSUE-0008) ---

    public DraftRowItem NameRow => _nameRow;
    public DraftRowItem CompanyRow => _companyRow;
    public DraftRowItem TitleRow => _titleRow;
    public DraftRowItem WebsiteRow => _websiteRow;
    public DraftRowItem AddressRow => _addressRow;
    public DraftRowItem NoteRow => _noteRow;

    public ObservableCollection<DraftRowItem> PhoneRows { get; } = new();
    public ObservableCollection<DraftRowItem> EmailRows { get; } = new();
    public ObservableCollection<DraftRowItem> CustomRows { get; } = new();
    public ObservableCollection<DraftRowItem> UnclassifiedRows { get; } = new();

    public string UnclassifiedCountText => $"待分類 ({UnclassifiedRows.Count} 筆)";

    // --- Target Field Selection for ROI OCR ---

    public DraftRowItem? SelectedTargetField
    {
        get => _selectedTargetField;
        set
        {
            if (SetField(ref _selectedTargetField, value))
            {
                OnPropertyChanged(nameof(IsTargetFieldSelected));
                OnPropertyChanged(nameof(TargetFieldHintText));
                OnPropertyChanged(nameof(TargetFieldSelectionDisplay));
            }
        }
    }

    public bool IsTargetFieldSelected => _selectedTargetField != null;

    public string TargetFieldHintText => _selectedTargetField != null
        ? $"已選取【{_selectedTargetField.DisplayField}】: 請在圖片上框選文字以自動填入 (按 Esc 或再按一次取消)"
        : "在圖片上拖曳滑鼠框選局部區域 (按住 Ctrl 拖曳平移)";

    public string TargetFieldSelectionDisplay => _selectedTargetField != null
        ? $"目標欄位: {_selectedTargetField.DisplayField}"
        : "未選欄位 (拖曳僅畫選區)";

    public bool IsRowStillActive(DraftRowItem? row)
    {
        if (row == null) return false;

        if (DraftRows.Contains(row)) return true;

        if (ReferenceEquals(row, _nameRow) ||
            ReferenceEquals(row, _companyRow) ||
            ReferenceEquals(row, _titleRow) ||
            ReferenceEquals(row, _websiteRow) ||
            ReferenceEquals(row, _addressRow) ||
            ReferenceEquals(row, _noteRow))
        {
            return true;
        }

        if (PhoneRows.Contains(row) || EmailRows.Contains(row))
        {
            return true;
        }

        return false;
    }

    public void ToggleTargetField(DraftRowItem row)
    {
        if (ReferenceEquals(SelectedTargetField, row))
        {
            ClearTargetFieldSelection();
        }
        else
        {
            CancelCurrentOcr();
            SelectedTargetField = row;
        }
    }

    public void ClearTargetFieldSelection()
    {
        if (SelectedTargetField != null)
        {
            CancelCurrentOcr();
            SelectedTargetField = null;
        }
    }

    // --- Viewport, Zoom, Pan ---

    public double ViewportWidth
    {
        get => _viewportWidth;
        set
        {
            if (SetField(ref _viewportWidth, value))
            {
                if (_isFitMode && HasImage) SetFitToWindow();
            }
        }
    }

    public double ViewportHeight
    {
        get => _viewportHeight;
        set
        {
            if (SetField(ref _viewportHeight, value))
            {
                if (_isFitMode && HasImage) SetFitToWindow();
            }
        }
    }

    public double ZoomScale
    {
        get => _zoomScale;
        set
        {
            if (SetField(ref _zoomScale, value))
            {
                OnPropertyChanged(nameof(ZoomPercentText));
            }
        }
    }

    public double PanX
    {
        get => _panX;
        set => SetField(ref _panX, value);
    }

    public double PanY
    {
        get => _panY;
        set => SetField(ref _panY, value);
    }

    public bool IsFitMode
    {
        get => _isFitMode;
        set
        {
            if (SetField(ref _isFitMode, value))
            {
                OnPropertyChanged(nameof(ZoomPercentText));
            }
        }
    }

    public string ZoomPercentText =>
        $"{Math.Round(ZoomScale * 100)}%" + (IsFitMode ? " (適合)" : "");

    public void SetFitToWindow()
    {
        if (CurrentSession == null || ViewportWidth <= 0 || ViewportHeight <= 0) return;
        double fitScale = ImageTransformHelper.CalculateFitScale(
            ViewportWidth, ViewportHeight, CurrentSession.Width, CurrentSession.Height);
        ZoomScale = fitScale > 0 ? fitScale : 1.0;
        PanX = 0;
        PanY = 0;
        IsFitMode = true;
    }

    public void ZoomIn()
    {
        if (CurrentSession == null) return;
        IsFitMode = false;
        double newScale = Math.Min(5.0, ZoomScale * 1.25);
        ZoomScale = newScale;
        var (clampedX, clampedY) = ImageTransformHelper.ClampPan(
            PanX, PanY, ViewportWidth, ViewportHeight, CurrentSession.Width, CurrentSession.Height, ZoomScale);
        PanX = clampedX;
        PanY = clampedY;
    }

    public void ZoomOut()
    {
        if (CurrentSession == null) return;
        IsFitMode = false;
        double newScale = Math.Max(0.1, ZoomScale * 0.8);
        ZoomScale = newScale;
        var (clampedX, clampedY) = ImageTransformHelper.ClampPan(
            PanX, PanY, ViewportWidth, ViewportHeight, CurrentSession.Width, CurrentSession.Height, ZoomScale);
        PanX = clampedX;
        PanY = clampedY;
    }

    public void ZoomAtPoint(int mouseWheelDelta, double pointerX, double pointerY)
    {
        if (CurrentSession == null || ViewportWidth <= 0 || ViewportHeight <= 0) return;

        IsFitMode = false;
        double steps = mouseWheelDelta / 120.0;
        double zoomMultiplier = Math.Pow(1.2, steps);

        var (newScale, newPanX, newPanY) = ImageTransformHelper.CalculateZoomAtPoint(
            ViewportWidth,
            ViewportHeight,
            CurrentSession.Width,
            CurrentSession.Height,
            ZoomScale,
            PanX,
            PanY,
            pointerX,
            pointerY,
            zoomMultiplier);

        ZoomScale = newScale;
        PanX = newPanX;
        PanY = newPanY;
    }

    public void SetPan(double newPanX, double newPanY)
    {
        if (CurrentSession == null) return;
        IsFitMode = false;
        var (clampedX, clampedY) = ImageTransformHelper.ClampPan(
            newPanX, newPanY, ViewportWidth, ViewportHeight, CurrentSession.Width, CurrentSession.Height, ZoomScale);
        PanX = clampedX;
        PanY = clampedY;
    }

    public void Pan(double deltaX, double deltaY)
    {
        SetPan(PanX + deltaX, PanY + deltaY);
    }

    // --- Image Session & Loading ---

    public ImageSessionInfo? CurrentSession
    {
        get => _currentSession;
        private set
        {
            if (SetField(ref _currentSession, value))
            {
                OnPropertyChanged(nameof(HasImage));
                OnPropertyChanged(nameof(ImageDimensionsText));
                OnPropertyChanged(nameof(ImageSourceText));
            }
        }
    }

    public BitmapImage? DisplayImage
    {
        get => _displayImage;
        private set => SetField(ref _displayImage, value);
    }

    public bool HasImage => CurrentSession != null;
    public string ImageDimensionsText => CurrentSession != null ? $"{CurrentSession.Width} × {CurrentSession.Height} px ({CurrentSession.MimeType})" : "無圖片";
    public string ImageSourceText => CurrentSession?.SourceName ?? "未載入圖片";

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanRunActions));
                OnPropertyChanged(nameof(CanUseStorage));
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        set
        {
            if (SetField(ref _isSaving, value))
            {
                OnPropertyChanged(nameof(CanRunActions));
                OnPropertyChanged(nameof(CanUseStorage));
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    private bool _isStorageInitializing;
    public bool IsStorageInitializing
    {
        get => _isStorageInitializing;
        set
        {
            if (SetField(ref _isStorageInitializing, value))
            {
                OnPropertyChanged(nameof(CanUseStorage));
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool CanRunActions => !_isBusy && !_isSaving;
    public bool CanUseStorage => CanRunActions && !_isStorageInitializing && _storageService?.IsAvailable == true;
    public bool CanSave => CanUseStorage && CurrentSession != null;

    public string CurrentContactId => _currentContactId;
    public int CurrentRevision => _currentRevision;
    public string CurrentSaveOperationId => _currentSaveOperationId;
    public string? LastSavedFingerprint => _lastSavedFingerprint;

    public IStorageService? StorageService
    {
        get => _storageService;
        set
        {
            if (SetField(ref _storageService, value))
            {
                OnPropertyChanged(nameof(CanUseStorage));
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public string BusyMessage
    {
        get => _busyMessage;
        set => SetField(ref _busyMessage, value);
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(_statusMessage);

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetField(ref _statusMessage, value))
                OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    public string LastOcrSummary
    {
        get => _lastOcrSummary;
        set => SetField(ref _lastOcrSummary, value);
    }

    public bool IsRegionMode
    {
        get => _isRegionMode;
        set => SetField(ref _isRegionMode, value);
    }

    public int RegionX
    {
        get => _regionX;
        set
        {
            if (SetField(ref _regionX, value)) OnPropertyChanged(nameof(RegionCoordsText));
        }
    }

    public int RegionY
    {
        get => _regionY;
        set
        {
            if (SetField(ref _regionY, value)) OnPropertyChanged(nameof(RegionCoordsText));
        }
    }

    public int RegionWidth
    {
        get => _regionWidth;
        set
        {
            if (SetField(ref _regionWidth, value)) OnPropertyChanged(nameof(RegionCoordsText));
        }
    }

    public int RegionHeight
    {
        get => _regionHeight;
        set
        {
            if (SetField(ref _regionHeight, value)) OnPropertyChanged(nameof(RegionCoordsText));
        }
    }

    public string RegionCoordsText => $"選區: X={RegionX}, Y={RegionY}, W={RegionWidth}, H={RegionHeight}";

    public string DraftRowsCountText => $"{DraftRows.Count} 筆草稿列";

    public bool HasUnsavedChanges
    {
        get
        {
            if (HasUnsavedTags) return true;
            string? currentFingerprint = DraftSnapshotFactory.ComputeCurrentDraftFingerprint(this, _currentContactId);
            if (currentFingerprint == null)
            {
                return false;
            }

            if (_lastSavedFingerprint == null)
            {
                return true;
            }

            return !string.Equals(currentFingerprint, _lastSavedFingerprint, StringComparison.Ordinal);
        }
    }

    public int CurrentRequestId => _currentRequestId;

    // --- Field Options Management ---

    public void RefreshFieldOptions()
    {
        FieldOptions.Clear();

        // Standard kinds
        foreach (var kind in CanonicalValueKind.AllStandardKinds)
        {
            FieldOptions.Add(DraftFieldOption.CreateStandard(kind));
        }

        // Active custom definitions from catalog
        List<CustomFieldDefinition> activeDefs = new();
        try
        {
            activeDefs = _catalogService.GetActiveDefinitions();
            foreach (var def in activeDefs)
            {
                FieldOptions.Add(DraftFieldOption.CreateCustom(def));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"讀取自訂名稱目錄提示: {ex.Message}";
        }

        // Action item for adding a new custom definition
        var actionOption = DraftFieldOption.CreateAction("+ 新增自訂名稱…");
        FieldOptions.Add(actionOption);

        // Synchronize options for each existing draft row so snapshot and deactivated items are preserved
        foreach (var row in DraftRows)
        {
            row.SyncOptions(activeDefs, actionOption);
        }
    }

    private static DraftRowItem CreateEmptyFixedRow(string kind)
    {
        return new DraftRowItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ValueKind = kind,
            Value = string.Empty,
            OriginalText = string.Empty,
            Source = "手動輸入",
            Status = DraftItemStatus.PendingConfirmation,
            IsUserModified = false
        };
    }

    private void ResetFixedRows()
    {
        _nameRow = CreateEmptyFixedRow(CanonicalValueKind.Name);
        _companyRow = CreateEmptyFixedRow(CanonicalValueKind.Company);
        _titleRow = CreateEmptyFixedRow(CanonicalValueKind.Title);
        _websiteRow = CreateEmptyFixedRow(CanonicalValueKind.Website);
        _addressRow = CreateEmptyFixedRow(CanonicalValueKind.Address);
        _noteRow = CreateEmptyFixedRow(CanonicalValueKind.Note);

        ObserveRow(_nameRow);
        ObserveRow(_companyRow);
        ObserveRow(_titleRow);
        ObserveRow(_websiteRow);
        ObserveRow(_addressRow);
        ObserveRow(_noteRow);
        ForgetDetachedRows();

        // x:Bind to NameRow.Value and the other fixed fields must rebind after a new image or draft.
        OnPropertyChanged(nameof(NameRow));
        OnPropertyChanged(nameof(CompanyRow));
        OnPropertyChanged(nameof(TitleRow));
        OnPropertyChanged(nameof(WebsiteRow));
        OnPropertyChanged(nameof(AddressRow));
        OnPropertyChanged(nameof(NoteRow));
    }

    public void EnsureRowInDraft(DraftRowItem row)
    {
        if (!DraftRows.Contains(row))
        {
            var activeDefs = _catalogService.GetActiveDefinitions();
            var actionOption = DraftFieldOption.CreateAction("+ 新增自訂名稱…");
            row.SyncOptions(activeDefs, actionOption);
            DraftRows.Add(row);
        }
    }

    public void SyncProjections()
    {
        UpdateFixedSlot(ref _nameRow, CanonicalValueKind.Name, nameof(NameRow));
        UpdateFixedSlot(ref _companyRow, CanonicalValueKind.Company, nameof(CompanyRow));
        UpdateFixedSlot(ref _titleRow, CanonicalValueKind.Title, nameof(TitleRow));
        UpdateFixedSlot(ref _websiteRow, CanonicalValueKind.Website, nameof(WebsiteRow));
        UpdateFixedSlot(ref _addressRow, CanonicalValueKind.Address, nameof(AddressRow));
        UpdateFixedSlot(ref _noteRow, CanonicalValueKind.Note, nameof(NoteRow));

        // Phone rows
        var activePhones = DraftRows.Where(r => r.ValueKind == CanonicalValueKind.Phone).ToList();
        PhoneRows.Clear();
        if (activePhones.Count == 0)
        {
            var emptyPhone = CreateEmptyFixedRow(CanonicalValueKind.Phone);
            ObserveRow(emptyPhone);
            PhoneRows.Add(emptyPhone);
        }
        else
        {
            foreach (var p in activePhones) PhoneRows.Add(p);
        }

        // Email rows
        var activeEmails = DraftRows.Where(r => r.ValueKind == CanonicalValueKind.Email).ToList();
        EmailRows.Clear();
        if (activeEmails.Count == 0)
        {
            var emptyEmail = CreateEmptyFixedRow(CanonicalValueKind.Email);
            ObserveRow(emptyEmail);
            EmailRows.Add(emptyEmail);
        }
        else
        {
            foreach (var em in activeEmails) EmailRows.Add(em);
        }

        // Custom rows
        var activeCustom = DraftRows.Where(r => r.ValueKind == CanonicalValueKind.Custom).ToList();
        CustomRows.Clear();
        foreach (var c in activeCustom) CustomRows.Add(c);

        // Unclassified rows (待分類)
        var activeUnclass = DraftRows.Where(r => r.ValueKind == CanonicalValueKind.Unclassified).ToList();

        // 兜底防護：若任何單值種類存在超過 1 筆非空項目，超出第一筆的衝突項目自動納入待分類列表中展示，確保 UI 100% 可視可調，絕不隱形！
        var singleKinds = new[]
        {
            CanonicalValueKind.Name,
            CanonicalValueKind.Company,
            CanonicalValueKind.Title,
            CanonicalValueKind.Website,
            CanonicalValueKind.Address,
            CanonicalValueKind.Note
        };
        foreach (var kind in singleKinds)
        {
            var extraRows = DraftRows.Where(r => r.ValueKind == kind && !string.IsNullOrWhiteSpace(r.Value)).Skip(1);
            foreach (var extra in extraRows)
            {
                if (!activeUnclass.Contains(extra))
                {
                    activeUnclass.Add(extra);
                }
            }
        }

        UnclassifiedRows.Clear();
        foreach (var u in activeUnclass) UnclassifiedRows.Add(u);

        OnPropertyChanged(nameof(UnclassifiedCountText));
        ForgetDetachedRows();
    }

    private void UpdateFixedSlot(ref DraftRowItem slot, string kind, string propertyName)
    {
        var existing = DraftRows.FirstOrDefault(r => r.ValueKind == kind);
        if (existing != null)
        {
            if (!ReferenceEquals(slot, existing))
            {
                slot = existing;
                ObserveRow(slot);
                OnPropertyChanged(propertyName);
            }
        }
        else
        {
            if (slot.ValueKind != kind || !string.IsNullOrEmpty(slot.Value))
            {
                slot = CreateEmptyFixedRow(kind);
                ObserveRow(slot);
                OnPropertyChanged(propertyName);
            }
        }
    }

    // --- Image Loading and Session Switching ---

    public async Task SetSessionImageAsync(ImageSessionInfo session)
    {
        if (IsSaving) return;

        CancelCurrentOcr();
        int decodeReqId = _currentRequestId;

        // Verify image decodability using ValidateImageAsync
        var imgValidation = await MajiOCR_Windows.Services.Storage.StorageService.ValidateImageAsync(session.ImageBytes);
        if (!imgValidation.valid)
        {
            StatusMessage = "圖片資料損壞或格式不支援，無法正常解碼。保留目前草稿。";
            return;
        }

        BitmapImage? bmp = null;
        if (!IsHeadlessEnvironment)
        {
            if (DisplayImageLoader != null)
            {
                bmp = await DisplayImageLoader(session.ImageBytes);
            }
            else
            {
                try
                {
                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                        {
                            writer.WriteBytes(session.ImageBytes);
                            await writer.StoreAsync();
                        }
                        stream.Seek(0);

                        bmp = new BitmapImage();
                        await bmp.SetSourceAsync(stream);
                    }
                }
                catch
                {
                    // Headless / CLI test environment without active XAML dispatcher
                    bmp = null;
                }
            }
        }

        if (decodeReqId != _currentRequestId) return;

        Candidates.Clear();
        DraftRows.Clear();
        Draft.Clear();
        SelectedTargetField = null;

        _currentContactId = Guid.NewGuid().ToString("N");
        _currentRevision = 0;
        ResetClassification();
        _currentSaveOperationId = Guid.NewGuid().ToString("N");
        _lastSavedFingerprint = null;
        _isCurrentContactDeleted = false;

        _nameRow.ClearUndo();
        _companyRow.ClearUndo();
        _titleRow.ClearUndo();
        _websiteRow.ClearUndo();
        _addressRow.ClearUndo();
        _noteRow.ClearUndo();

        ResetFixedRows();

        CurrentSession = session;
        DisplayImage = bmp;

        // Auto-fit to viewport on image load if DisplayImage is available (RS-ISSUE-0008 OCR8-06)
        if (bmp != null)
        {
            SetFitToWindow();
        }

        LastOcrSummary = $"已載入圖片: {session.Width}x{session.Height} ({session.ByteCount / 1024.0:F1} KB)";
        StatusMessage = "圖片載入完成，已等比例適合視窗。可點擊欄位旁「局部辨識」框選文字，或點擊「整圖辨識」。";

        _regionX = 0;
        _regionY = 0;
        _regionWidth = 0;
        _regionHeight = 0;
        OnPropertyChanged(nameof(RegionCoordsText));
        OnPropertyChanged(nameof(DraftRowsCountText));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        SyncProjections();
    }

    public async Task ReloadSavedContactAsync(ContactDetailModel detail)
    {
        if (IsSaving) return;

        CancelCurrentOcr();

        ImageSessionInfo? session = null;
        BitmapImage? bmp = null;
        if (detail.VerifiedImageBytes != null && detail.VerifiedImageBytes.Length > 0)
        {
            var imgInfo = await MajiOCR_Windows.Services.Storage.StorageService.ValidateImageAsync(detail.VerifiedImageBytes);
            string mimeType = detail.ImageAsset?.MimeType ?? (imgInfo.valid ? imgInfo.mimeType : "image/png");
            int width = detail.ImageAsset?.Width > 0 ? detail.ImageAsset.Width : (imgInfo.valid ? imgInfo.width : 0);
            int height = detail.ImageAsset?.Height > 0 ? detail.ImageAsset.Height : (imgInfo.valid ? imgInfo.height : 0);
            string captureSource = detail.ImageAsset?.CaptureSource ?? "SavedCardReload";

            session = ImageSessionInfo.Create(detail.VerifiedImageBytes, $"{detail.ContactId}.png", captureSource, mimeType, width, height);
            if (!IsHeadlessEnvironment)
            {
                if (DisplayImageLoader != null)
                {
                    bmp = await DisplayImageLoader(detail.VerifiedImageBytes);
                }
                else
                {
                    try
                    {
                        using (var stream = new InMemoryRandomAccessStream())
                        {
                            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                            {
                                writer.WriteBytes(detail.VerifiedImageBytes);
                                await writer.StoreAsync();
                            }
                            stream.Seek(0);
                            bmp = new BitmapImage();
                            await bmp.SetSourceAsync(stream);
                        }
                    }
                    catch
                    {
                        bmp = null;
                    }
                }
            }
        }

        Candidates.Clear();
        DraftRows.Clear();
        Draft.Clear();
        SelectedTargetField = null;

        _currentContactId = detail.ContactId;
        _currentRevision = detail.Revision;
        _currentSaveOperationId = Guid.NewGuid().ToString("N");
        _isCurrentContactDeleted = false;

        _nameRow.ClearUndo();
        _companyRow.ClearUndo();
        _titleRow.ClearUndo();
        _websiteRow.ClearUndo();
        _addressRow.ClearUndo();
        _noteRow.ClearUndo();

        ResetFixedRows();

        foreach (var val in detail.Values.OrderBy(v => v.Ordinal))
        {
            var row = new DraftRowItem
            {
                ValueKind = val.ValueKind,
                CustomFieldDefinitionId = val.CustomFieldDefinitionId,
                CustomFieldNameSnapshot = val.CustomFieldNameSnapshot,
                Value = val.RawValue,
                OriginalText = val.RawValue,
                Status = DraftItemStatus.Confirmed,
                IsUserModified = false
            };
            DraftRows.Add(row);
        }

        LoadClassificationFromDetail(detail.Tags, detail.TagRevision);

        CurrentSession = session;
        DisplayImage = bmp;

        if (bmp != null)
        {
            SetFitToWindow();
        }

        _regionX = 0;
        _regionY = 0;
        _regionWidth = 0;
        _regionHeight = 0;

        _lastSavedFingerprint = DraftSnapshotFactory.ComputeCurrentDraftFingerprint(this, _currentContactId);

        SyncProjections();
        OnPropertyChanged(nameof(RegionCoordsText));
        OnPropertyChanged(nameof(DraftRowsCountText));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CanSave));

        StatusMessage = $"已載入名片「{detail.Name}」 (版本 {detail.Revision})，可進行框選局部辨識或修改後儲存。";
    }

    public async Task<string?> LoadFromFilePathAsync(string filePath)
    {
        if (IsSaving || IsBusy) return "系統正忙，無法載入圖片。";
        IsBusy = true;
        BusyMessage = "正在驗證並載入圖片檔案...";
        try
        {
            var result = await _imageService.LoadFromFileAsync(filePath);
            if (!result.Success || result.SessionInfo == null)
            {
                return result.ErrorMessage ?? "讀取圖片失敗。";
            }

            await SetSessionImageAsync(result.SessionInfo);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<string?> PasteFromClipboardAsync()
    {
        if (IsSaving || IsBusy) return "系統正忙，無法自剪貼簿貼上圖片。";
        IsBusy = true;
        BusyMessage = "正在讀取剪貼簿圖片資料...";
        try
        {
            var result = await _imageService.LoadFromClipboardAsync();
            if (!result.Success || result.SessionInfo == null)
            {
                return result.ErrorMessage ?? "剪貼簿中未偵測到有效圖片。";
            }

            await SetSessionImageAsync(result.SessionInfo);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // --- OCR Operations ---

    public async Task<string?> RunFullOcrAsync()
    {
        if (CurrentSession == null)
        {
            return "尚未載入任何名片圖片。";
        }

        var langSnapshot = SelectedFullOcrLanguage ?? OcrLanguageCatalog.AllOptions[0];
        string langCode = langSnapshot.Code;
        string langDisplayName = langSnapshot.DisplayName;

        CancelCurrentOcr();
        _cts = new CancellationTokenSource();
        int reqId = ++_currentRequestId;
        _activeFullRequestId = reqId;
        _activeOcrKind = ActiveOcrKind.Full;
        var sessionAtStart = CurrentSession;

        IsBusy = true;
        BusyMessage = $"正在執行〔{langDisplayName}〕離線辨識...";

        try
        {
            var ocrResult = await _ocrService.RunFullOcrAsync(CurrentSession.ImageBytes, reqId, _cts.Token, langCode);

            if (reqId != _currentRequestId || (_cts != null && _cts.IsCancellationRequested)) return null;

            if (SelectedFullOcrLanguage?.Code != langCode)
            {
                StatusMessage = $"整圖辨識語系已變更為〔{SelectedFullOcrLanguage?.DisplayName}〕，已略過〔{langDisplayName}〕舊辨識結果。";
                return null;
            }

            if (!ocrResult.Success)
            {
                StatusMessage = ocrResult.ErrorMessage ?? $"〔{langDisplayName}〕整圖辨識失敗。";
                return ocrResult.ErrorMessage ?? "OCR 辨識失敗。";
            }

            Candidates.Clear();
            foreach (var item in ocrResult.Candidates)
            {
                Candidates.Add(item);
            }

            PopulateDraftRowsFromCandidates(ocrResult.Candidates, reqId, isRegion: false);

            LastOcrSummary = $"整圖辨識完成 ({langDisplayName}): 耗時 {ocrResult.LatencyMs} ms，平均信心度 {ocrResult.MeanConfidence:P0}，共 {ocrResult.Candidates.Count} 行。";
            StatusMessage = $"〔{langDisplayName}〕整圖辨識完成，共產出 {ocrResult.Candidates.Count} 筆候選。未確定或衝突項目已收納至「待分類」。";
            return null;
        }
        catch (OperationCanceledException)
        {
            if (reqId == _activeFullRequestId &&
                ReferenceEquals(CurrentSession, sessionAtStart) &&
                SelectedFullOcrLanguage?.Code == langCode)
            {
                StatusMessage = "OCR 辨識已取消。";
            }
            return null;
        }
        finally
        {
            if (reqId == _activeFullRequestId)
            {
                _activeFullRequestId = 0;
            }
            if (reqId == _currentRequestId)
            {
                IsBusy = false;
                _activeOcrKind = ActiveOcrKind.None;
            }
        }
    }

    public async Task<string?> RunRegionOcrAsync()
    {
        if (CurrentSession == null)
        {
            return "尚未載入任何名片圖片。";
        }

        if (RegionWidth < 10 || RegionHeight < 10)
        {
            return "選取區域過小 (寬度與高度需至少 10 像素)。請在圖片上拖曳選框。";
        }

        // If a target field is explicitly selected, delegate to targeted ROI OCR
        if (SelectedTargetField != null)
        {
            return await RunTargetedRoiOcrAsync(SelectedTargetField);
        }

        var langSnapshot = SelectedPartialOcrLanguage ?? OcrLanguageCatalog.AllOptions[0];
        string langCode = langSnapshot.Code;
        string langDisplayName = langSnapshot.DisplayName;

        CancelCurrentOcr();
        _cts = new CancellationTokenSource();
        int reqId = ++_currentRequestId;
        _activeRegionRequestId = reqId;
        _activeOcrKind = ActiveOcrKind.Region;
        var sessionAtStart = CurrentSession;

        IsBusy = true;
        BusyMessage = $"正在以〔{langDisplayName}〕辨識局部選區 ({RegionWidth}x{RegionHeight})...";

        try
        {
            var ocrResult = await _ocrService.RunRegionOcrAsync(
                CurrentSession.ImageBytes,
                RegionX, RegionY, RegionWidth, RegionHeight,
                reqId, _cts.Token,
                langCode
            );

            if (reqId != _currentRequestId || (_cts != null && _cts.IsCancellationRequested)) return null;

            if (SelectedPartialOcrLanguage?.Code != langCode)
            {
                StatusMessage = $"局部辨識語系已變更為〔{SelectedPartialOcrLanguage?.DisplayName}〕，已略過〔{langDisplayName}〕舊辨識結果。";
                return null;
            }

            if (!ocrResult.Success)
            {
                StatusMessage = ocrResult.ErrorMessage ?? $"〔{langDisplayName}〕局部辨識失敗。";
                return ocrResult.ErrorMessage ?? "局部 OCR 辨識失敗。";
            }

            if (ocrResult.Candidates.Count == 0)
            {
                StatusMessage = $"選取區域未辨識出有效文字行 ({langDisplayName})。";
                return "選取區域未辨識出文字行，可手動補正或放大選區。";
            }

            int inserted = 0;
            foreach (var item in ocrResult.Candidates)
            {
                Candidates.Insert(0, item);
                inserted++;
            }

            PopulateDraftRowsFromCandidates(ocrResult.Candidates, reqId, isRegion: true);

            LastOcrSummary = $"局部辨識完成 ({langDisplayName}): 耗時 {ocrResult.LatencyMs} ms，產出 {inserted} 行局部候選。";
            StatusMessage = $"〔{langDisplayName}〕局部選區辨識完成 ({inserted} 行)，已加入草稿列。";
            return null;
        }
        catch (OperationCanceledException)
        {
            if (reqId == _activeRegionRequestId &&
                ReferenceEquals(CurrentSession, sessionAtStart) &&
                SelectedPartialOcrLanguage?.Code == langCode)
            {
                StatusMessage = "局部 OCR 辨識已取消。";
            }
            return null;
        }
        finally
        {
            if (reqId == _activeRegionRequestId)
            {
                _activeRegionRequestId = 0;
            }
            if (reqId == _currentRequestId)
            {
                IsBusy = false;
                _activeOcrKind = ActiveOcrKind.None;
            }
        }
    }

    /// <summary>
    /// Explicitly targeted per-field ROI OCR conforming to RS-ISSUE-0008 OCR8-02, OCR8-03, OCR8-04, OCR8-05.
    /// Fills only the target field, replaces existing text with one-field undo support,
    /// sets status to PendingConfirmation, and preserves undo/prior text on empty/failure/cancel/stale.
    /// </summary>
    public async Task<string?> RunTargetedRoiOcrAsync(DraftRowItem targetField)
    {
        if (CurrentSession == null)
        {
            return "尚未載入任何名片圖片。";
        }

        if (!IsRowStillActive(targetField))
        {
            return "目標欄位已不存在。";
        }

        if (RegionWidth < 10 || RegionHeight < 10)
        {
            return "選取區域過小 (寬度與高度需至少 10 像素)。請在圖片上重新框選。";
        }

        var langSnapshot = SelectedPartialOcrLanguage ?? OcrLanguageCatalog.AllOptions[0];
        string langCode = langSnapshot.Code;
        string langDisplayName = langSnapshot.DisplayName;

        CancelCurrentOcr();
        _cts = new CancellationTokenSource();
        int reqId = ++_currentRequestId;
        _activeRegionRequestId = reqId;
        _activeOcrKind = ActiveOcrKind.Region;

        // Establish target field selection if not already set
        if (!ReferenceEquals(SelectedTargetField, targetField))
        {
            SelectedTargetField = targetField;
        }

        var sessionAtStart = CurrentSession;
        var targetFieldAtStart = targetField;
        int editVersionAtStart = targetField.EditVersion;
        string valueAtStart = targetField.Value;
        DraftItemStatus statusAtStart = targetField.Status;

        IsBusy = true;
        BusyMessage = $"正在以〔{langDisplayName}〕辨識局部選區以填入【{targetField.DisplayField}】...";

        try
        {
            var ocrResult = await _ocrService.RunRegionOcrAsync(
                sessionAtStart.ImageBytes,
                RegionX, RegionY, RegionWidth, RegionHeight,
                reqId, _cts.Token,
                langCode
            );

            // 1. Request generation or cancellation check
            if (reqId != _currentRequestId || (_cts != null && _cts.IsCancellationRequested))
            {
                return null;
            }

            // 2. Language selection change check
            if (SelectedPartialOcrLanguage?.Code != langCode)
            {
                StatusMessage = $"局部辨識語系已變更為〔{SelectedPartialOcrLanguage?.DisplayName}〕，已略過〔{langDisplayName}〕舊辨識結果。";
                return null;
            }

            // 2. Image session switch / close check
            if (CurrentSession == null || !ReferenceEquals(CurrentSession, sessionAtStart))
            {
                return null;
            }

            // 3. Target field selection change check
            if (!ReferenceEquals(SelectedTargetField, targetFieldAtStart))
            {
                StatusMessage = "目標欄位選取已變更，已略過局部辨識結果。";
                return null;
            }

            // 4. Target row removal / deletion check
            if (!IsRowStillActive(targetFieldAtStart))
            {
                StatusMessage = "目標欄位已移除，已略過局部辨識結果。";
                return null;
            }

            // 5. Manual edit or confirmation change check while OCR awaited
            if (targetFieldAtStart.EditVersion != editVersionAtStart ||
                targetFieldAtStart.Value != valueAtStart ||
                targetFieldAtStart.Status != statusAtStart)
            {
                StatusMessage = $"【{targetFieldAtStart.DisplayField}】在辨識期間已有人工修改，已保留最新編輯內容。";
                return null;
            }

            if (!ocrResult.Success)
            {
                // OCR failure leaves prior value and undo intact
                StatusMessage = ocrResult.ErrorMessage ?? $"〔{langDisplayName}〕局部辨識失敗。";
                return ocrResult.ErrorMessage ?? "局部 OCR 辨識失敗。";
            }

            if (ocrResult.Candidates.Count == 0)
            {
                // Empty OCR leaves prior value and undo intact
                StatusMessage = $"選取區域未辨識出有效文字行 ({langDisplayName})。";
                return "選取區域未辨識出文字行，可手動補正或重新框選。";
            }

            // Combine candidate lines according to field type:
            // Multi-line for Address and Note; space-separated for other fields
            string combinedValue;
            string combinedOriginal;
            if (targetField.ValueKind is CanonicalValueKind.Address or CanonicalValueKind.Note)
            {
                combinedValue = string.Join(Environment.NewLine, ocrResult.Candidates.Select(c => c.Text));
                combinedOriginal = string.Join(Environment.NewLine, ocrResult.Candidates.Select(c => c.OriginalText ?? c.Text));
            }
            else
            {
                combinedValue = string.Join(" ", ocrResult.Candidates.Select(c => c.Text));
                combinedOriginal = string.Join(" ", ocrResult.Candidates.Select(c => c.OriginalText ?? c.Text));
            }

            // Traditional Chinese suggestion combination
            string? combinedSuggestion = null;
            string? suggestionSource = null;
            var candsWithSug = ocrResult.Candidates.Where(c => c.HasSuggestion).ToList();
            if (candsWithSug.Count > 0)
            {
                if (targetField.ValueKind is CanonicalValueKind.Address or CanonicalValueKind.Note)
                {
                    combinedSuggestion = string.Join(Environment.NewLine, ocrResult.Candidates.Select(c => c.TraditionalSuggestion ?? c.Text));
                }
                else
                {
                    combinedSuggestion = string.Join(" ", ocrResult.Candidates.Select(c => c.TraditionalSuggestion ?? c.Text));
                }
                suggestionSource = ocrResult.Candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c.SuggestionSource))?.SuggestionSource ?? "繁體建議";
            }

            // Save undo snapshot if field has existing value before replacing
            if (!string.IsNullOrEmpty(targetField.Value))
            {
                targetField.SaveUndoSnapshot();
            }

            // Update target field
            targetField.Value = combinedValue;
            targetField.OriginalText = combinedOriginal;
            targetField.TraditionalSuggestion = combinedSuggestion;
            targetField.SuggestionSource = suggestionSource;
            targetField.X = RegionX;
            targetField.Y = RegionY;
            targetField.Width = RegionWidth;
            targetField.Height = RegionHeight;
            targetField.Source = "局部辨識";
            targetField.Status = DraftItemStatus.PendingConfirmation; // Targeted OCR of confirmed field returns to pending confirmation!
            targetField.IsUserModified = false;

            EnsureRowInDraft(targetField);

            // Prepend candidates to session candidates list
            foreach (var cand in ocrResult.Candidates)
            {
                Candidates.Insert(0, cand);
            }

            LastOcrSummary = $"局部辨識完成 ({langDisplayName}): 耗時 {ocrResult.LatencyMs} ms，已填入【{targetField.DisplayField}】。";
            StatusMessage = $"局部辨識完成 ({langDisplayName})，已填入【{targetField.DisplayField}】(待確認)。";

            SelectedTargetField = null;
            SyncProjections();
            return null;
        }
        catch (OperationCanceledException)
        {
            if (reqId == _activeRegionRequestId &&
                ReferenceEquals(CurrentSession, sessionAtStart) &&
                SelectedPartialOcrLanguage?.Code == langCode &&
                ReferenceEquals(SelectedTargetField, targetFieldAtStart))
            {
                StatusMessage = "局部 OCR 辨識已取消。";
            }
            return null;
        }
        finally
        {
            if (reqId == _activeRegionRequestId)
            {
                _activeRegionRequestId = 0;
            }
            if (reqId == _currentRequestId)
            {
                IsBusy = false;
                _activeOcrKind = ActiveOcrKind.None;
            }
        }
    }

    public bool UndoField(DraftRowItem targetField)
    {
        if (!targetField.CanUndo) return false;
        bool ok = targetField.RestoreUndo();
        if (ok)
        {
            StatusMessage = $"已復原【{targetField.DisplayField}】至前次內容。";
            OnPropertyChanged(nameof(HasUnsavedChanges));
            SyncProjections();
        }
        return ok;
    }

    public void PopulateDraftRowsFromCandidates(IEnumerable<OcrCandidateItem> candidates, int reqId, bool isRegion)
    {
        if (reqId != _currentRequestId) return;

        var activeDefs = _catalogService.GetActiveDefinitions();
        var actionOption = DraftFieldOption.CreateAction("+ 新增自訂名稱…");

        if (!isRegion)
        {
            // Full OCR:
            // 1. Preserve any user-modified or confirmed rows
            var preservedRows = DraftRows.Where(r => r.IsUserModified || r.Status == DraftItemStatus.Confirmed).ToList();
            DraftRows.Clear();
            foreach (var r in preservedRows)
            {
                r.SyncOptions(activeDefs, actionOption);
                DraftRows.Add(r);
            }

            var filledSingleKinds = new HashSet<string>(
                preservedRows.Where(r => CanonicalValueKind.IsSingleValueKind(r.ValueKind)).Select(r => r.ValueKind));
            var matchedPreserved = new HashSet<DraftRowItem>();

            foreach (var cand in candidates)
            {
                string candOrig = cand.OriginalText ?? cand.Text;

                // De-duplicate against already preserved rows with identical raw text, pixel coords, and source
                var matchingPreserved = preservedRows.FirstOrDefault(r =>
                    !matchedPreserved.Contains(r) &&
                    string.Equals(r.OriginalText, candOrig, StringComparison.Ordinal) &&
                    r.X == cand.X &&
                    r.Y == cand.Y &&
                    r.Width == cand.Width &&
                    r.Height == cand.Height &&
                    string.Equals(r.Source, cand.Source, StringComparison.Ordinal));

                if (matchingPreserved != null)
                {
                    matchedPreserved.Add(matchingPreserved);
                    continue;
                }

                string suggestedKind = FieldSuggester.Suggest(cand.Text);
                string targetKind = suggestedKind;

                // Single-value conflict protection:
                if (CanonicalValueKind.IsSingleValueKind(suggestedKind))
                {
                    if (filledSingleKinds.Contains(suggestedKind))
                    {
                        // Already occupied by preserved row or earlier candidate in this run:
                        // Route conflicting candidate into Unclassified (待分類)
                        targetKind = CanonicalValueKind.Unclassified;
                    }
                    else
                    {
                        // Target single-value field is empty: claim it
                        filledSingleKinds.Add(suggestedKind);
                        targetKind = suggestedKind;
                    }
                }

                var row = new DraftRowItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    OriginalText = candOrig,
                    Value = cand.Text,
                    X = cand.X,
                    Y = cand.Y,
                    Width = cand.Width,
                    Height = cand.Height,
                    Source = cand.Source,
                    RequestGeneration = reqId,
                    ValueKind = targetKind,
                    Status = DraftItemStatus.PendingConfirmation,
                    IsUserModified = false,
                    TraditionalSuggestion = cand.TraditionalSuggestion,
                    SuggestionSource = cand.SuggestionSource
                };
                row.SyncOptions(activeDefs, actionOption);
                DraftRows.Add(row);
            }
        }
        else
        {
            // Region OCR (when not specifically targeted)
            int insertIndex = 0;
            var existingSingleKinds = new HashSet<string>(
                DraftRows.Where(r => CanonicalValueKind.IsSingleValueKind(r.ValueKind) && !string.IsNullOrWhiteSpace(r.Value))
                         .Select(r => r.ValueKind));

            foreach (var cand in candidates)
            {
                string suggestedKind = FieldSuggester.Suggest(cand.Text);
                if (CanonicalValueKind.IsSingleValueKind(suggestedKind) && existingSingleKinds.Contains(suggestedKind))
                {
                    suggestedKind = CanonicalValueKind.Unclassified;
                }
                else if (CanonicalValueKind.IsSingleValueKind(suggestedKind))
                {
                    existingSingleKinds.Add(suggestedKind);
                }

                var row = new DraftRowItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    OriginalText = cand.OriginalText ?? cand.Text,
                    Value = cand.Text,
                    X = cand.X,
                    Y = cand.Y,
                    Width = cand.Width,
                    Height = cand.Height,
                    Source = cand.Source,
                    RequestGeneration = reqId,
                    ValueKind = suggestedKind,
                    Status = DraftItemStatus.PendingConfirmation,
                    IsUserModified = false,
                    TraditionalSuggestion = cand.TraditionalSuggestion,
                    SuggestionSource = cand.SuggestionSource
                };
                row.SyncOptions(activeDefs, actionOption);
                DraftRows.Insert(insertIndex++, row);
            }
        }

        SyncProjections();
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(DraftRowsCountText));
    }

    public void CancelCurrentOcr()
    {
        Interlocked.Increment(ref _currentRequestId);
        _activeFullRequestId = 0;
        _activeRegionRequestId = 0;
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            _cts = null;
        }
        _activeOcrKind = ActiveOcrKind.None;
        if (!IsSaving)
        {
            IsBusy = false;
        }
    }

    public void SetRegionCoordinates(int x, int y, int width, int height)
    {
        CancelCurrentOcr();

        RegionX = Math.Max(0, x);
        RegionY = Math.Max(0, y);
        RegionWidth = Math.Max(0, width);
        RegionHeight = Math.Max(0, height);
    }

    // --- Draft Row Operations ---

    public DraftRowItem AddBlankDraftRow()
    {
        var activeDefs = _catalogService.GetActiveDefinitions();
        var actionOption = DraftFieldOption.CreateAction("+ 新增自訂名稱…");

        var row = new DraftRowItem
        {
            Id = Guid.NewGuid().ToString("N"),
            OriginalText = string.Empty,
            Value = string.Empty,
            Source = "手動輸入",
            RequestGeneration = _currentRequestId,
            ValueKind = CanonicalValueKind.Unclassified,
            Status = DraftItemStatus.PendingConfirmation,
            IsUserModified = true
        };
        row.SyncOptions(activeDefs, actionOption);
        DraftRows.Add(row);
        StatusMessage = "已新增空白草稿列。";
        return row;
    }

    public DraftRowItem AddPhoneRow(string value = "")
    {
        var row = new DraftRowItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ValueKind = CanonicalValueKind.Phone,
            Value = value,
            Source = "手動輸入",
            RequestGeneration = _currentRequestId,
            Status = DraftItemStatus.PendingConfirmation,
            IsUserModified = true
        };
        EnsureRowInDraft(row);
        StatusMessage = "已新增電話列。";
        return row;
    }

    public DraftRowItem AddEmailRow(string value = "")
    {
        var row = new DraftRowItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ValueKind = CanonicalValueKind.Email,
            Value = value,
            Source = "手動輸入",
            RequestGeneration = _currentRequestId,
            Status = DraftItemStatus.PendingConfirmation,
            IsUserModified = true
        };
        EnsureRowInDraft(row);
        StatusMessage = "已新增 Email 列。";
        return row;
    }

    public DraftRowItem AddCustomRow(string? definitionId = null, string? name = null)
    {
        var row = new DraftRowItem
        {
            Id = Guid.NewGuid().ToString("N"),
            ValueKind = CanonicalValueKind.Custom,
            CustomFieldDefinitionId = definitionId,
            CustomFieldNameSnapshot = name,
            Value = string.Empty,
            Source = "手動輸入",
            RequestGeneration = _currentRequestId,
            Status = DraftItemStatus.PendingConfirmation,
            IsUserModified = true
        };
        EnsureRowInDraft(row);
        StatusMessage = "已新增自訂欄位列。";
        return row;
    }

    public void RemoveDraftRow(DraftRowItem row)
    {
        if (ReferenceEquals(SelectedTargetField, row))
        {
            ClearTargetFieldSelection();
        }
        DraftRows.Remove(row);
        SyncProjections();
        StatusMessage = $"已移除草稿列【{row.DisplayField}】。";
    }

    public void ConfirmDraftRow(DraftRowItem row)
    {
        EnsureRowInDraft(row);
        row.Confirm();
        StatusMessage = $"已確認【{row.DisplayField}】。";
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    public void CreateBlankDraft()
    {
        if (IsSaving) return;

        CurrentSession = null;
        DisplayImage = null;

        _currentContactId = Guid.NewGuid().ToString("N");
        _currentRevision = 0;
        ResetClassification();
        _currentSaveOperationId = Guid.NewGuid().ToString("N");
        _lastSavedFingerprint = null;
        _isCurrentContactDeleted = false;

        DraftRows.Clear();
        Draft.Clear();
        Candidates.Clear();
        SelectedTargetField = null;

        _nameRow.ClearUndo();
        _companyRow.ClearUndo();
        _titleRow.ClearUndo();
        _websiteRow.ClearUndo();
        _addressRow.ClearUndo();
        _noteRow.ClearUndo();

        ResetFixedRows();

        SyncProjections();
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        StatusMessage = "已建立空白草稿 (草稿僅保留在本次工作階段)。";
    }

    public void OnActiveContactDeletedExternally(string contactId)
    {
        if (string.Equals(_currentContactId, contactId, StringComparison.OrdinalIgnoreCase))
        {
            _isCurrentContactDeleted = true;
            _lastSavedFingerprint = null;
            OnPropertyChanged(nameof(HasUnsavedChanges));
            StatusMessage = "目前名片已在儲存清單中刪除；既有欄位仍保留在草稿中，如需再次儲存請建立新名片工作階段。";
        }
    }

    public async Task<SaveResult> SaveCurrentDraftAsync()
    {
        if (_storageService == null || !_storageService.IsAvailable)
        {
            string msg = _storageService?.ErrorMessage ?? "本機儲存資料庫尚未初始化或不可用。";
            StatusMessage = $"儲存失敗: {msg}";
            return SaveResult.Failed("DB_UNAVAILABLE", msg);
        }

        if (IsBusy || IsSaving)
        {
            return SaveResult.Failed("BUSY", "系統正忙，請稍候再試。");
        }

        if (_isCurrentContactDeleted)
        {
            string msg = "此名片已在儲存清單中刪除，無法直接更新；請建立空白草稿或重新載入圖片以開啟新工作階段。";
            StatusMessage = msg;
            return SaveResult.Failed("CONTACT_DELETED", msg);
        }

        var validation = DraftSnapshotFactory.ValidateDraft(this);
        if (!validation.IsValid)
        {
            StatusMessage = validation.Message;
            return SaveResult.Failed(validation.Code.ToString(), validation.Message);
        }

        IsBusy = true;
        IsSaving = true;
        BusyMessage = "正在保存名片資料及受控圖片...";

        try
        {
            int expectedRev = _currentRevision;
            int nextRevision = _currentRevision == 0 ? 1 : _currentRevision + 1;
            string saveOpId = _currentSaveOperationId;
            var snapshot = DraftSnapshotFactory.CreateSnapshot(this, _currentContactId, expectedRev, nextRevision, saveOpId);

            // Freeze UI fingerprint before await so concurrent/in-flight edits are not falsely marked clean
            string? frozenFingerprint = DraftSnapshotFactory.ComputeCurrentDraftFingerprint(this, _currentContactId);

            var result = await _storageService.SaveDraftAsync(snapshot);
            if (result.Succeeded)
            {
                if (!string.Equals(result.PersistedFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
                {
                    return SaveResult.Failed("SAVE_CONTENT_MISMATCH", "儲存結果與提交內容不一致，草稿仍保留。");
                }
                _currentRevision = result.PersistedRevision;
                AcceptSavedTags(result.PersistedTags, snapshot.Tags);
                _lastSavedFingerprint = frozenFingerprint;
                OnPropertyChanged(nameof(HasUnsavedChanges));
                StatusMessage = "已儲存至這台電腦";
            }
            else
            {
                if (result.RequiresSaveAgain)
                {
                    _currentRevision = result.PersistedRevision;
                    if (result.PersistedTags is not null) {
                        _currentTagRevision = result.PersistedTags.Revision;
                        _savedTagFingerprint = TagChoice.Fingerprint(result.PersistedTags.Tags.Select(t => new TagChoice(t.TagId,t.DisplayName)));
                    }
                    // The completed previous payload differs from this submitted draft.
                    // Keep the draft dirty until the new revision is actually saved.
                    _lastSavedFingerprint = null;
                    OnPropertyChanged(nameof(HasUnsavedChanges));
                }
                StatusMessage = $"儲存尚未完成，草稿仍保留: {result.ErrorMessage ?? result.ErrorCode}";
            }

            return result;
        }
        catch (Exception ex)
        {
            string safeMsg = MajiOCR_Windows.Services.Storage.StorageService.FormatStorageError("SAVE_EXCEPTION", ex);
            StatusMessage = safeMsg;
            return SaveResult.Failed("EXCEPTION", safeMsg);
        }
        finally
        {
            IsSaving = false;
            IsBusy = false;
        }
    }

    public DraftRowItem? CheckSingleValueConflict(DraftRowItem row, string targetKind)
    {
        if (!CanonicalValueKind.IsSingleValueKind(targetKind)) return null;

        return DraftRows.FirstOrDefault(r => r != row &&
                                             !string.IsNullOrWhiteSpace(r.Value) &&
                                             r.ValueKind == targetKind);
    }

    // --- Legacy field operations for backwards compatibility ---

    public void ApplyCandidateToField(OcrCandidateItem candidate, string fieldName)
    {
        switch (fieldName)
        {
            case "Name":
                Draft.Name = candidate.Text;
                break;
            case "Company":
                Draft.Company = candidate.Text;
                break;
            case "Title":
                Draft.Title = candidate.Text;
                break;
            case "Phone":
                Draft.Phones.Add(new ContactFieldItem
                {
                    Kind = "Phone",
                    Label = "公司",
                    Value = candidate.Text,
                    Ordinal = Draft.Phones.Count
                });
                break;
            case "Email":
                Draft.Emails.Add(new ContactFieldItem
                {
                    Kind = "Email",
                    Label = "公司",
                    Value = candidate.Text,
                    Ordinal = Draft.Emails.Count
                });
                break;
            case "Website":
                Draft.Website = candidate.Text;
                break;
            case "Address":
                Draft.Address = candidate.Text;
                break;
            case "Note":
                if (string.IsNullOrWhiteSpace(Draft.Note))
                    Draft.Note = candidate.Text;
                else
                    Draft.Note += Environment.NewLine + candidate.Text;
                break;
        }

        StatusMessage = $"已將候選「{candidate.Text}」填入草稿【{fieldName}】欄位。";
    }

    public void AddPhone(string label = "公司", string value = "")
    {
        Draft.Phones.Add(new ContactFieldItem
        {
            Kind = "Phone",
            Label = label,
            Value = value,
            Ordinal = Draft.Phones.Count
        });
    }

    public void RemovePhone(ContactFieldItem item)
    {
        Draft.Phones.Remove(item);
    }

    public void AddEmail(string label = "公司", string value = "")
    {
        Draft.Emails.Add(new ContactFieldItem
        {
            Kind = "Email",
            Label = label,
            Value = value,
            Ordinal = Draft.Emails.Count
        });
    }

    public void RemoveEmail(ContactFieldItem item)
    {
        Draft.Emails.Remove(item);
    }

    public void AddTag(string tag)
    {
        tag = tag.Trim();
        if (!string.IsNullOrWhiteSpace(tag) && !Draft.Tags.Contains(tag))
        {
            Draft.Tags.Add(tag);
        }
    }

    public void RemoveTag(string tag)
    {
        Draft.Tags.Remove(tag);
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

