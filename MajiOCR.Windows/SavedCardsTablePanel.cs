using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows;

public sealed class SavedCardsTablePanel : UserControl
{
    private readonly IStorageService _storageService;
    private readonly IContactExportService _exportService;
    private readonly IGoogleContactsSyncService _syncService;
    private readonly IGoogleAuthService _authService;
    private readonly IGoogleDriveBackupService _driveBackupService;
    private readonly IAppSettingsService _settingsService;
    private readonly IDatabaseMergeService _databaseMergeService;
    private readonly IContactExcelCsvService _excelCsvService;
    public event EventHandler<string>? ActiveContactDeleted;
    public event EventHandler? RequestNavigateToScan;
    public event EventHandler<ContactDetailModel>? RequestReloadContact;

    private readonly double[] _colWidths = new double[] { 40, 110, 140, 100, 120, 150, 160, 120, 90, 110 };
    private readonly double[] _defaultColWidths = new double[] { 40, 110, 140, 100, 120, 150, 160, 120, 90, 110 };
    private readonly double[] _minColWidths = new double[] { 40, 80, 100, 80, 100, 120, 100, 110, 75, 90 };
    private readonly double[] _maxColWidths = new double[] { 40, 280, 360, 260, 240, 360, 360, 200, 110, 140 };
    private readonly ColumnDefinition[] _headerColDefs = new ColumnDefinition[10];

    // CheckBox Selection state
    private readonly HashSet<string> _selectedContactIds = new();
    private CheckBox _headerSelectAllChk = null!;
    private TextBlock _selectionStatusText = null!;
    private Button _clearSelectionBtn = null!;
    private readonly ICardSharingCryptoService _cardSharingCryptoService;

    private long _pageQueryGeneration = 0;
    private long _detailQueryGeneration = 0;
    private bool _isDeleting = false;
    private ContactDetailModel? _deleteTargetContact = null;
    private int _currentPage = 0;
    private int _totalPages = 0;
    private int _pageSize = 30;
    private string? _currentSearch = null;
    private ContactDetailModel? _currentDetail = null;
    private bool _disposed = false;

    // Sorting state
    private string _currentSortField = "CreatedAt";
    private bool _currentSortDescending = true;

    // Tag filter state
    private IReadOnlyList<string> _filterTags = Array.Empty<string>();
    private bool _matchAll = false;
    private bool _untagged = false;

    // Root visual elements
    private Grid _rootGrid = null!;
    private Grid _tableContainerGrid = null!;
    private Border _detailDrawerBorder = null!;
    private ColumnDefinition _detailColumnDef = null!;
    private bool _isDetailDrawerOpen = true;

    // Top Bar controls
    private TextBox _searchBox = null!;
    private Button _searchBtn = null!;
    private Button _clearSearchBtn = null!;
    private Button _tagFilterBtn = null!;
    private Button _manageTagsBtn = null!;
    private Button _autoFitColsBtn = null!;
    private DropDownButton _exportSyncBtn = null!;
    private StackPanel _tagChipsPanel = null!;
    private TextBlock _summaryCountText = null!;
    private Button _backToScanBtn = null!;
    private Button _newScanBtn = null!;

    // Table Header Sort indicators
    private TextBlock _hdrNameSort = null!;
    private TextBlock _hdrCompanySort = null!;
    private TextBlock _hdrTitleSort = null!;
    private TextBlock _hdrTimeSort = null!;

    // Table Body controls
    private ListView _tableListView = null!;
    private TextBlock _emptyNoticeText = null!;
    private ProgressRing _loadingProgressRing = null!;

    // Bottom Paging controls
    private Button _firstPageBtn = null!;
    private Button _prevPageBtn = null!;
    private TextBlock _pageInfoText = null!;
    private Button _nextPageBtn = null!;
    private Button _lastPageBtn = null!;
    private ComboBox _pageSizeComboBox = null!;
    private Button _toggleDrawerBtn = null!;

    // Right Drawer Detail controls
    private Grid _noSelectionGrid = null!;
    private Grid _detailContentGrid = null!;
    private TextBlock _detailTitleText = null!;
    private TextBlock _detailSubtitleText = null!;
    private TextBlock _detailMetaText = null!;
    private TextBlock _detailTagsText = null!;
    private Button _editTagsBtn = null!;
    private Button _reloadToScanBtn = null!;
    private Image _detailImage = null!;
    private TextBlock _detailImageNotice = null!;
    private Button _openBigImageBtn = null!;
    private Button _openSystemImageBtn = null!;
    private StackPanel _detailValuesStack = null!;
    private Button _startDeleteBtn = null!;

    // In-Drawer Delete Confirmation controls
    private Grid _deleteConfirmGrid = null!;
    private TextBlock _deleteSummaryText = null!;
    private TextBlock _deleteErrorText = null!;
    private Button _cancelDeleteBtn = null!;
    private Button _confirmDeleteBtn = null!;

    public SavedCardsTablePanel(
        IStorageService storageService,
        IContactExportService? exportService = null,
        IGoogleContactsSyncService? syncService = null,
        IGoogleAuthService? authService = null,
        IGoogleDriveBackupService? driveBackupService = null,
        IAppSettingsService? settingsService = null)
    {
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _exportService = exportService ?? new ContactExportService();
        _syncService = syncService ?? new GoogleContactsSyncService();
        _authService = authService ?? new GoogleAuthService();
        _driveBackupService = driveBackupService ?? new GoogleDriveBackupService();
        _settingsService = settingsService ?? new AppSettingsService();
        _databaseMergeService = new DatabaseMergeService();
        _excelCsvService = new ContactExcelCsvService();
        _cardSharingCryptoService = new CardSharingCryptoService();

        BuildVisualTree();

        Loaded += async (s, e) =>
        {
            _disposed = false;
            await LoadTagChipsAsync();
            await LoadPageAsync(0);
        };

        Unloaded += (s, e) =>
        {
            _disposed = true;
            Interlocked.Increment(ref _pageQueryGeneration);
            Interlocked.Increment(ref _detailQueryGeneration);
        };
    }

    private void BuildVisualTree()
    {
        _rootGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto }, // Row 0: Top Action & Search Bar
                new RowDefinition { Height = GridLength.Auto }, // Row 1: Tag Chips Quick-Filter Bar
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, // Row 2: Table + Detail Drawer
                new RowDefinition { Height = GridLength.Auto }  // Row 3: Pagination & Status Footer
            }
        };

        BuildTopBar();
        BuildTagChipsBar();
        BuildTableAndDetailArea();
        BuildFooter();

        Content = _rootGrid;
    }

    private void BuildTopBar()
    {
        var topBarGrid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 10),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto }, // Title
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, // Search box & filters
                new ColumnDefinition { Width = GridLength.Auto }  // Actions: New / Back
            }
        };

        // Title
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        titleStack.Children.Add(new FontIcon { Glyph = "\uE8EC", FontSize = 20, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
        titleStack.Children.Add(new TextBlock { Text = "已存名片總覽", FontSize = 18, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(titleStack, 0);
        topBarGrid.Children.Add(titleStack);

        // Search & Filters Center Group (Compacted & prevents toolbar overlapping)
        var searchGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

        _searchBox = new TextBox
        {
            PlaceholderText = "搜尋 (支援多條件：+ 為 AND，空格為 OR，如：王曉明+台達)",
            Width = 260,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(_searchBox, "搜尋語法說明：\n• 加號「+」代表 AND（例如「王曉明+台達」需兩者皆符合）\n• 空格代表 OR（例如「王曉明 台達」任一符合即可）\n• 支援混合搜尋（例如「王曉明+台達 研發」）\n• 亦可搜尋分類標籤名稱");
        _searchBox.KeyDown += async (s, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                _currentSearch = string.IsNullOrWhiteSpace(_searchBox.Text) ? null : _searchBox.Text.Trim();
                await LoadPageAsync(0);
            }
        };
        searchGroup.Children.Add(_searchBox);

        _searchBtn = new Button { Content = "🔍 搜尋", VerticalAlignment = VerticalAlignment.Center };
        _searchBtn.Click += async (s, e) =>
        {
            _currentSearch = string.IsNullOrWhiteSpace(_searchBox.Text) ? null : _searchBox.Text.Trim();
            await LoadPageAsync(0);
        };
        searchGroup.Children.Add(_searchBtn);

        _clearSearchBtn = new Button { Content = "重設", VerticalAlignment = VerticalAlignment.Center };
        _clearSearchBtn.Click += async (s, e) =>
        {
            _searchBox.Text = string.Empty;
            _currentSearch = null;
            _filterTags = Array.Empty<string>();
            _untagged = false;
            _matchAll = false;
            await LoadTagChipsAsync();
            await LoadPageAsync(0);
        };
        searchGroup.Children.Add(_clearSearchBtn);

        _tagFilterBtn = new Button { Content = "🏷️ 標籤篩選", VerticalAlignment = VerticalAlignment.Center };
        _tagFilterBtn.Click += OnTagFilterClicked;
        searchGroup.Children.Add(_tagFilterBtn);

        _manageTagsBtn = new Button { Content = "⚙️ 管理標籤", VerticalAlignment = VerticalAlignment.Center };
        _manageTagsBtn.Click += OnManageTagsClicked;
        searchGroup.Children.Add(_manageTagsBtn);

        _autoFitColsBtn = new Button { Content = "↔️ 自動欄寬", VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(_autoFitColsBtn, "自動依名片內容與標題長度調整所有欄位寬度（亦可雙擊欄位標頭或分隔線調整單欄）");
        _autoFitColsBtn.Click += (s, e) => AutoFitAllColumns();
        searchGroup.Children.Add(_autoFitColsBtn);

        // Consolidated Data & Sync DropDownButton
        var dataSyncFlyout = new MenuFlyout();

        // 1. 資料庫同步中心
        var dbSyncItem = new MenuFlyoutItem { Text = "🗄️ 多裝置資料庫同步中心..." };
        dbSyncItem.Click += async (s, e) => await OnDatabaseSyncClickedAsync();
        dataSyncFlyout.Items.Add(dbSyncItem);

        var syncGoogleItem = new MenuFlyoutItem { Text = "👤 同步至 Google 聯絡人 (通訊錄)" };
        syncGoogleItem.Click += async (s, e) => await OnSyncGoogleClickedAsync();
        dataSyncFlyout.Items.Add(syncGoogleItem);

        dataSyncFlyout.Items.Add(new MenuFlyoutSeparator());

        // 2. 匯入名片
        var importItem = new MenuFlyoutItem { Text = "📥 匯入名片資料 (Excel / CSV)..." };
        importItem.Click += async (s, e) => await OnImportExcelCsvClickedAsync();
        dataSyncFlyout.Items.Add(importItem);

        var importEncryptedItem = new MenuFlyoutItem { Text = "🔐 讀取加密名片分享包 (*.majicard)..." };
        importEncryptedItem.Click += async (s, e) => await OnImportEncryptedPackageClickedAsync();
        dataSyncFlyout.Items.Add(importEncryptedItem);

        dataSyncFlyout.Items.Add(new MenuFlyoutSeparator());

        // 3. 匯出設定與自訂 (支援選擇範圍、排除標籤/備註、加密等)
        var exportConfigItem = new MenuFlyoutItem { Text = "📤 匯出名片設定 (範圍 / 排除標籤備註 / 加密)..." };
        exportConfigItem.Click += async (s, e) => await OnExportWithDialogClickedAsync();
        dataSyncFlyout.Items.Add(exportConfigItem);

        // 4. 匯出全部名片 (子選單)
        var exportAllSub = new MenuFlyoutSubItem { Text = "📤 匯出全部名片 (快捷匯出)" };

        var excelItem = new MenuFlyoutItem { Text = "📊 匯出 Excel 工作表 (*.xlsx)" };
        excelItem.Click += async (s, e) => await OnExportExcelClickedAsync();
        exportAllSub.Items.Add(excelItem);

        var fullCsvItem = new MenuFlyoutItem { Text = "📁 匯出完整名片 CSV (*.csv)" };
        fullCsvItem.Click += async (s, e) => await OnExportFullCsvClickedAsync();
        exportAllSub.Items.Add(fullCsvItem);

        var outlookItem = new MenuFlyoutItem { Text = "📁 匯出 Outlook 聯絡人表單 (*.csv)" };
        outlookItem.Click += async (s, e) => await OnExportOutlookClickedAsync();
        exportAllSub.Items.Add(outlookItem);

        var googleItem = new MenuFlyoutItem { Text = "📁 匯出 Google 聯絡人 (*.csv)" };
        googleItem.Click += async (s, e) => await OnExportGoogleClickedAsync();
        exportAllSub.Items.Add(googleItem);

        var vcardItem = new MenuFlyoutItem { Text = "📇 匯出通用名片檔 (*.vcf)" };
        vcardItem.Click += async (s, e) => await OnExportVCardClickedAsync();
        exportAllSub.Items.Add(vcardItem);

        dataSyncFlyout.Items.Add(exportAllSub);

        // 5. 匯出所選名片 (子選單)
        var exportSelectedSub = new MenuFlyoutSubItem { Text = "📤 匯出所選名片 (快捷匯出)" };

        var excelSelectedItem = new MenuFlyoutItem { Text = "📊 匯出所選名片至 Excel (*.xlsx)" };
        excelSelectedItem.Click += async (s, e) => await OnExportSelectedExcelClickedAsync();
        exportSelectedSub.Items.Add(excelSelectedItem);

        var csvSelectedItem = new MenuFlyoutItem { Text = "📁 匯出所選名片至 CSV (*.csv)" };
        csvSelectedItem.Click += async (s, e) => await OnExportSelectedCsvClickedAsync();
        exportSelectedSub.Items.Add(csvSelectedItem);

        var encryptedSelectedItem = new MenuFlyoutItem { Text = "🔐 匯出所選名片為加密分享包 (*.majicard)..." };
        encryptedSelectedItem.Click += async (s, e) => await OnExportWithDialogClickedAsync(ExportCardsScope.SelectedOnly, ExportCardsFormat.EncryptedPackage);
        exportSelectedSub.Items.Add(encryptedSelectedItem);

        dataSyncFlyout.Items.Add(exportSelectedSub);

        dataSyncFlyout.Items.Add(new MenuFlyoutSeparator());

        // 5. 雲端與本機備份還原
        var backupDriveItem = new MenuFlyoutItem { Text = "☁️ 備份至 Google Drive (SQLite + 名片圖片)" };
        backupDriveItem.Click += async (s, e) => await OnBackupToGoogleDriveClickedAsync();
        dataSyncFlyout.Items.Add(backupDriveItem);

        var restoreDriveItem = new MenuFlyoutItem { Text = "📥 從 Google Drive 還原 (版本辨識與選擇)" };
        restoreDriveItem.Click += async (s, e) => await OnRestoreFromGoogleDriveClickedAsync();
        dataSyncFlyout.Items.Add(restoreDriveItem);

        var backupLocalItem = new MenuFlyoutItem { Text = "💾 備份至本機 ZIP 檔案" };
        backupLocalItem.Click += async (s, e) => await OnBackupLocalZipClickedAsync();
        dataSyncFlyout.Items.Add(backupLocalItem);

        var restoreLocalItem = new MenuFlyoutItem { Text = "📂 從本機 ZIP 檔案還原" };
        restoreLocalItem.Click += async (s, e) => await OnRestoreLocalZipClickedAsync();
        dataSyncFlyout.Items.Add(restoreLocalItem);

        dataSyncFlyout.Items.Add(new MenuFlyoutSeparator());

        // 6. 行動版部署
        var deployVercelItem = new MenuFlyoutItem { Text = "📱 iOS / Web 行動版 (GitHub Pages / Vercel)..." };
        deployVercelItem.Click += async (s, e) =>
        {
            if (this.XamlRoot != null)
            {
                await VercelDeployDialog.ShowAsync(this.XamlRoot, _settingsService);
            }
        };
        dataSyncFlyout.Items.Add(deployVercelItem);

        _exportSyncBtn = new DropDownButton
        {
            Content = "🔄 資料與同步",
            Flyout = dataSyncFlyout,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(_exportSyncBtn, "整合多裝置資料庫同步、Google 雲端備份/還原，以及 Excel / CSV 名片匯入與匯出");
        searchGroup.Children.Add(_exportSyncBtn);

        Grid.SetColumn(searchGroup, 1);
        topBarGrid.Children.Add(searchGroup);

        // Right Navigation Actions
        var navStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        _newScanBtn = new Button
        {
            Content = "➕ 辨識新名片",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        _newScanBtn.Click += (s, e) => RequestNavigateToScan?.Invoke(this, EventArgs.Empty);
        navStack.Children.Add(_newScanBtn);

        _backToScanBtn = new Button { Content = "🔙 返回掃描頁", VerticalAlignment = VerticalAlignment.Center };
        _backToScanBtn.Click += (s, e) => RequestNavigateToScan?.Invoke(this, EventArgs.Empty);
        navStack.Children.Add(_backToScanBtn);

        Grid.SetColumn(navStack, 2);
        topBarGrid.Children.Add(navStack);

        Grid.SetRow(topBarGrid, 0);
        _rootGrid.Children.Add(topBarGrid);
    }

    private void BuildTagChipsBar()
    {
        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 10)
        };

        _tagChipsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        scrollViewer.Content = _tagChipsPanel;
        Grid.SetRow(scrollViewer, 1);
        _rootGrid.Children.Add(scrollViewer);
    }

    private void BuildTableAndDetailArea()
    {
        var mainSplitGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ColumnSpacing = 12
        };
        mainSplitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Table area
        _detailColumnDef = new ColumnDefinition { Width = new GridLength(340) }; // Detail drawer
        mainSplitGrid.ColumnDefinitions.Add(_detailColumnDef);

        // --- Left Table Container ---
        _tableContainerGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto }, // Table Column Headers
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) } // Table Rows ListView
            }
        };

        var tableHeaderBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Padding = new Thickness(12, 10, 12, 10)
        };

        var hdrGrid = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < 10; i++)
        {
            _headerColDefs[i] = new ColumnDefinition { Width = new GridLength(_colWidths[i]) };
            hdrGrid.ColumnDefinitions.Add(_headerColDefs[i]);
        }

        // Header 0: CheckBox (Select All)
        _headerSelectAllChk = new CheckBox
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 24
        };
        ToolTipService.SetToolTip(_headerSelectAllChk, "全選 / 取消全選本頁名片");
        _headerSelectAllChk.Checked += OnHeaderSelectAllChecked;
        _headerSelectAllChk.Unchecked += OnHeaderSelectAllUnchecked;
        Grid.SetColumn(_headerSelectAllChk, 0);
        hdrGrid.Children.Add(_headerSelectAllChk);

        // Header 1: 姓名 (Sortable)
        var nameBtn = CreateSortHeaderButton("姓名", "Name", out _hdrNameSort);
        nameBtn.DoubleTapped += (s, e) => { AutoFitColumn(1); e.Handled = true; };
        ToolTipService.SetToolTip(nameBtn, "點擊切換排序，雙擊自動調整欄寬");
        Grid.SetColumn(nameBtn, 1);
        hdrGrid.Children.Add(nameBtn);

        // Header 2: 公司 (Sortable)
        var compBtn = CreateSortHeaderButton("公司", "Company", out _hdrCompanySort);
        compBtn.DoubleTapped += (s, e) => { AutoFitColumn(2); e.Handled = true; };
        ToolTipService.SetToolTip(compBtn, "點擊切換排序，雙擊自動調整欄寬");
        Grid.SetColumn(compBtn, 2);
        hdrGrid.Children.Add(compBtn);

        // Header 3: 職稱 (Sortable)
        var titleBtn = CreateSortHeaderButton("職稱", "Title", out _hdrTitleSort);
        titleBtn.DoubleTapped += (s, e) => { AutoFitColumn(3); e.Handled = true; };
        ToolTipService.SetToolTip(titleBtn, "點擊切換排序，雙擊自動調整欄寬");
        Grid.SetColumn(titleBtn, 3);
        hdrGrid.Children.Add(titleBtn);

        // Header 4: 電話
        var phoneHdr = new TextBlock { Text = "電話", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        phoneHdr.DoubleTapped += (s, e) => { AutoFitColumn(4); e.Handled = true; };
        ToolTipService.SetToolTip(phoneHdr, "雙擊自動調整欄寬");
        Grid.SetColumn(phoneHdr, 4);
        hdrGrid.Children.Add(phoneHdr);

        // Header 5: Email
        var emailHdr = new TextBlock { Text = "Email", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        emailHdr.DoubleTapped += (s, e) => { AutoFitColumn(5); e.Handled = true; };
        ToolTipService.SetToolTip(emailHdr, "雙擊自動調整欄寬");
        Grid.SetColumn(emailHdr, 5);
        hdrGrid.Children.Add(emailHdr);

        // Header 6: 分類標籤
        var tagHdr = new TextBlock { Text = "分類標籤", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        tagHdr.DoubleTapped += (s, e) => { AutoFitColumn(6); e.Handled = true; };
        ToolTipService.SetToolTip(tagHdr, "雙擊自動調整欄寬");
        Grid.SetColumn(tagHdr, 6);
        hdrGrid.Children.Add(tagHdr);

        // Header 7: 儲存時間 (Sortable)
        var timeBtn = CreateSortHeaderButton("儲存時間", "CreatedAt", out _hdrTimeSort);
        timeBtn.DoubleTapped += (s, e) => { AutoFitColumn(7); e.Handled = true; };
        ToolTipService.SetToolTip(timeBtn, "點擊切換排序，雙擊自動調整欄寬");
        Grid.SetColumn(timeBtn, 7);
        hdrGrid.Children.Add(timeBtn);

        // Header 8: 名片圖檔
        var imgHdr = new TextBlock { Text = "名片圖檔", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        imgHdr.DoubleTapped += (s, e) => { AutoFitColumn(8); e.Handled = true; };
        ToolTipService.SetToolTip(imgHdr, "雙擊自動調整欄寬");
        Grid.SetColumn(imgHdr, 8);
        hdrGrid.Children.Add(imgHdr);

        // Header 9: 操作
        var actionHdr = new TextBlock { Text = "操作", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        actionHdr.DoubleTapped += (s, e) => { AutoFitColumn(9); e.Handled = true; };
        ToolTipService.SetToolTip(actionHdr, "雙擊自動調整欄寬");
        Grid.SetColumn(actionHdr, 9);
        hdrGrid.Children.Add(actionHdr);

        // Column Resizers between columns 1..8
        for (int i = 1; i < 9; i++)
        {
            var resizer = CreateColumnResizer(i);
            Grid.SetColumn(resizer, i);
            hdrGrid.Children.Add(resizer);
        }

        tableHeaderBorder.Child = hdrGrid;
        Grid.SetRow(tableHeaderBorder, 0);
        _tableContainerGrid.Children.Add(tableHeaderBorder);

        // Table Rows ListView
        var tableBodyBorder = new Border
        {
            BorderThickness = new Thickness(1, 0, 1, 1),
            CornerRadius = new CornerRadius(0, 0, 6, 6),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"]
        };

        var listHostGrid = new Grid();

        _tableListView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _tableListView.ItemTemplate = BuildDataTemplate();
        _tableListView.SelectionChanged += OnTableSelectionChanged;
        _tableListView.DoubleTapped += (s, e) =>
        {
            if (_currentDetail != null && !_isDeleting)
            {
                RequestReloadContact?.Invoke(this, _currentDetail);
            }
        };
        listHostGrid.Children.Add(_tableListView);

        _emptyNoticeText = new TextBlock
        {
            Text = "尚無已儲存名片",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            Opacity = 0.6,
            Visibility = Visibility.Collapsed
        };
        listHostGrid.Children.Add(_emptyNoticeText);

        _loadingProgressRing = new ProgressRing
        {
            Width = 36,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsActive = false,
            Visibility = Visibility.Collapsed
        };
        listHostGrid.Children.Add(_loadingProgressRing);

        tableBodyBorder.Child = listHostGrid;
        Grid.SetRow(tableBodyBorder, 1);
        _tableContainerGrid.Children.Add(tableBodyBorder);

        Grid.SetColumn(_tableContainerGrid, 0);
        mainSplitGrid.Children.Add(_tableContainerGrid);

        // --- Right Detail Drawer ---
        _detailDrawerBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Padding = new Thickness(12)
        };

        var drawerContainerGrid = new Grid();

        // 1. No selection state
        _noSelectionGrid = new Grid();
        _noSelectionGrid.Children.Add(new TextBlock
        {
            Text = "請點選左側表格中的任一名片\n以檢視詳細資訊、完整備註與圖片。",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.6,
            FontSize = 13
        });
        drawerContainerGrid.Children.Add(_noSelectionGrid);

        // 2. Detail content state
        _detailContentGrid = new Grid
        {
            Visibility = Visibility.Collapsed,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto }, // Header
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) } // Scrollable content
            }
        };

        // Detail Header
        var detailHdrGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        detailHdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailHdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dHeadStack = new StackPanel();
        _detailTitleText = new TextBlock { FontSize = 16, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        _detailSubtitleText = new TextBlock { FontSize = 12, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis };
        _detailMetaText = new TextBlock { FontSize = 10, Opacity = 0.5, Margin = new Thickness(0, 2, 0, 0) };
        dHeadStack.Children.Add(_detailTitleText);
        dHeadStack.Children.Add(_detailSubtitleText);
        dHeadStack.Children.Add(_detailMetaText);
        Grid.SetColumn(dHeadStack, 0);
        detailHdrGrid.Children.Add(dHeadStack);

        _startDeleteBtn = new Button { Content = "🗑️ 刪除", FontSize = 11, Padding = new Thickness(6, 3, 6, 3), VerticalAlignment = VerticalAlignment.Top };
        _startDeleteBtn.Click += OnStartDeleteClicked;
        Grid.SetColumn(_startDeleteBtn, 1);
        detailHdrGrid.Children.Add(_startDeleteBtn);

        Grid.SetRow(detailHdrGrid, 0);
        _detailContentGrid.Children.Add(detailHdrGrid);

        // Detail Scrollable Body
        var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var bodyStack = new StackPanel { Spacing = 10 };

        _reloadToScanBtn = new Button
        {
            Content = "✏️ 載回主畫面編輯 / 重新辨識",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 6, 10, 6)
        };
        _reloadToScanBtn.Click += (s, e) =>
        {
            if (_currentDetail != null && !_isDeleting)
            {
                RequestReloadContact?.Invoke(this, _currentDetail);
            }
        };
        bodyStack.Children.Add(_reloadToScanBtn);

        // Tags bar inside detail
        var tagsBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Padding = new Thickness(8, 6, 8, 6)
        };
        var tagsGrid = new Grid { ColumnSpacing = 6 };
        tagsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tagsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _detailTagsText = new TextBlock { Text = "分類標籤：未分類", FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_detailTagsText, 0);
        tagsGrid.Children.Add(_detailTagsText);

        _editTagsBtn = new Button { Content = "編輯", FontSize = 11, Padding = new Thickness(6, 2, 6, 2) };
        _editTagsBtn.Click += async (s, e) => await EditDetailTagsAsync();
        Grid.SetColumn(_editTagsBtn, 1);
        tagsGrid.Children.Add(_editTagsBtn);

        tagsBorder.Child = tagsGrid;
        bodyStack.Children.Add(tagsBorder);

        // Thumbnail Image
        var imgBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            MinHeight = 110,
            MaxHeight = 160,
            Padding = new Thickness(4)
        };
        var imgGrid = new Grid();
        _detailImage = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _detailImageNotice = new TextBlock
        {
            Text = "此名片未包含圖檔",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Opacity = 0.6,
            Visibility = Visibility.Collapsed
        };
        imgGrid.Children.Add(_detailImage);
        imgGrid.Children.Add(_detailImageNotice);
        imgBorder.Child = imgGrid;
        bodyStack.Children.Add(imgBorder);

        // Image Buttons
        var imgBtnStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        _openBigImageBtn = new Button { Content = "🖼️ 開啟大圖檢視", FontSize = 11, Padding = new Thickness(8, 3, 8, 3) };
        _openBigImageBtn.Click += OnDetailOpenBigImageClicked;
        imgBtnStack.Children.Add(_openBigImageBtn);

        _openSystemImageBtn = new Button { Content = "💻 系統看圖開啟", FontSize = 11, Padding = new Thickness(8, 3, 8, 3) };
        _openSystemImageBtn.Click += OnDetailOpenSystemImageClicked;
        imgBtnStack.Children.Add(_openSystemImageBtn);
        bodyStack.Children.Add(imgBtnStack);

        // Field Values
        bodyStack.Children.Add(new TextBlock { Text = "確認欄位明細", FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        _detailValuesStack = new StackPanel { Spacing = 4 };
        bodyStack.Children.Add(_detailValuesStack);

        scrollViewer.Content = bodyStack;
        Grid.SetRow(scrollViewer, 1);
        _detailContentGrid.Children.Add(scrollViewer);

        drawerContainerGrid.Children.Add(_detailContentGrid);

        // 3. Delete Confirmation Overlay inside Drawer
        _deleteConfirmGrid = new Grid
        {
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8)
        };
        var delStack = new StackPanel { Spacing = 10 };
        delStack.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = "確定要刪除這張名片嗎？",
            Message = "名片資料與圖片將一併永久移除。"
        });
        _deleteSummaryText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
        delStack.Children.Add(_deleteSummaryText);

        _deleteErrorText = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Colors.Red), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        delStack.Children.Add(_deleteErrorText);

        var delBtnStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        _cancelDeleteBtn = new Button { Content = "取消", Width = 80 };
        _cancelDeleteBtn.Click += (s, e) => CancelDelete();
        delBtnStack.Children.Add(_cancelDeleteBtn);

        _confirmDeleteBtn = new Button { Content = "確認刪除", Width = 90, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        _confirmDeleteBtn.Click += async (s, e) => await ConfirmDeleteAsync();
        delBtnStack.Children.Add(_confirmDeleteBtn);
        delStack.Children.Add(delBtnStack);

        _deleteConfirmGrid.Children.Add(delStack);
        drawerContainerGrid.Children.Add(_deleteConfirmGrid);

        _detailDrawerBorder.Child = drawerContainerGrid;
        Grid.SetColumn(_detailDrawerBorder, 1);
        mainSplitGrid.Children.Add(_detailDrawerBorder);

        Grid.SetRow(mainSplitGrid, 2);
        _rootGrid.Children.Add(mainSplitGrid);
    }

    private void BuildFooter()
    {
        var footerGrid = new Grid
        {
            Margin = new Thickness(0, 10, 0, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto }, // Paging
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, // Center Info
                new ColumnDefinition { Width = GridLength.Auto }  // Page Size & Toggle Drawer
            }
        };

        // Paging buttons
        var pageStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        _firstPageBtn = new Button { Content = "⏮️ 第一頁", FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        _firstPageBtn.Click += async (s, e) => await LoadPageAsync(0);
        pageStack.Children.Add(_firstPageBtn);

        _prevPageBtn = new Button { Content = "◀️ 上一頁", FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        _prevPageBtn.Click += async (s, e) => await LoadPageAsync(_currentPage - 1);
        pageStack.Children.Add(_prevPageBtn);

        _pageInfoText = new TextBlock { Text = "第 1 頁", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        pageStack.Children.Add(_pageInfoText);

        _nextPageBtn = new Button { Content = "下一頁 ▶️", FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        _nextPageBtn.Click += async (s, e) => await LoadPageAsync(_currentPage + 1);
        pageStack.Children.Add(_nextPageBtn);

        _lastPageBtn = new Button { Content = "最末頁 ⏭️", FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        _lastPageBtn.Click += async (s, e) => await LoadPageAsync(_totalPages - 1);
        pageStack.Children.Add(_lastPageBtn);

        Grid.SetColumn(pageStack, 0);
        footerGrid.Children.Add(pageStack);

        // Center Count Summary & Selection Status
        var centerSummaryStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        _summaryCountText = new TextBlock
        {
            Text = "共 0 筆名片",
            FontSize = 12,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        };
        centerSummaryStack.Children.Add(_summaryCountText);

        _selectionStatusText = new TextBlock
        {
            Text = "",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center
        };
        centerSummaryStack.Children.Add(_selectionStatusText);

        _clearSelectionBtn = new Button
        {
            Content = "清除勾選",
            FontSize = 11,
            Padding = new Thickness(6, 2, 6, 2),
            Visibility = Visibility.Collapsed
        };
        _clearSelectionBtn.Click += (s, e) => ClearAllSelections();
        centerSummaryStack.Children.Add(_clearSelectionBtn);

        Grid.SetColumn(centerSummaryStack, 1);
        footerGrid.Children.Add(centerSummaryStack);

        // Right Settings: Page Size & Drawer Toggle
        var rightStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        _pageSizeComboBox = new ComboBox
        {
            ItemsSource = new[] { "每頁 15 筆", "每頁 30 筆", "每頁 50 筆", "每頁 100 筆" },
            SelectedIndex = 1,
            FontSize = 12,
            Padding = new Thickness(6, 2, 6, 2)
        };
        _pageSizeComboBox.SelectionChanged += async (s, e) =>
        {
            _pageSize = _pageSizeComboBox.SelectedIndex switch
            {
                0 => 15,
                1 => 30,
                2 => 50,
                3 => 100,
                _ => 30
            };
            await LoadPageAsync(0);
        };
        rightStack.Children.Add(_pageSizeComboBox);

        _toggleDrawerBtn = new Button { Content = "📋 收合明細", FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        _toggleDrawerBtn.Click += (s, e) =>
        {
            _isDetailDrawerOpen = !_isDetailDrawerOpen;
            _detailColumnDef.Width = _isDetailDrawerOpen ? new GridLength(340) : new GridLength(0);
            _detailDrawerBorder.Visibility = _isDetailDrawerOpen ? Visibility.Visible : Visibility.Collapsed;
            _toggleDrawerBtn.Content = _isDetailDrawerOpen ? "📋 收合明細" : "📋 展開明細";
        };
        rightStack.Children.Add(_toggleDrawerBtn);

        Grid.SetColumn(rightStack, 2);
        footerGrid.Children.Add(rightStack);

        Grid.SetRow(footerGrid, 3);
        _rootGrid.Children.Add(footerGrid);
    }

    private Button CreateSortHeaderButton(string title, string sortField, out TextBlock sortIndicator)
    {
        var btn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(text);

        sortIndicator = new TextBlock
        {
            Text = sortField == _currentSortField ? (_currentSortDescending ? "⯆" : "⯅") : "",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(sortIndicator);

        btn.Content = stack;
        btn.Click += async (s, e) =>
        {
            if (_currentSortField == sortField)
            {
                _currentSortDescending = !_currentSortDescending;
            }
            else
            {
                _currentSortField = sortField;
                _currentSortDescending = (sortField == "CreatedAt");
            }
            UpdateSortHeaderIndicators();
            await LoadPageAsync(0);
        };

        return btn;
    }

    private void UpdateSortHeaderIndicators()
    {
        _hdrNameSort.Text = _currentSortField == "Name" ? (_currentSortDescending ? "⯆" : "⯅") : "";
        _hdrCompanySort.Text = _currentSortField == "Company" ? (_currentSortDescending ? "⯆" : "⯅") : "";
        _hdrTitleSort.Text = _currentSortField == "Title" ? (_currentSortDescending ? "⯆" : "⯅") : "";
        _hdrTimeSort.Text = _currentSortField == "CreatedAt" ? (_currentSortDescending ? "⯆" : "⯅") : "";
    }

    private DataTemplate BuildDataTemplate()
    {
        string xaml = $@"
            <DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                <Grid ColumnSpacing='8' Padding='4,6' HorizontalAlignment='Stretch'>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width='{_colWidths[0]}' />
                        <ColumnDefinition Width='{_colWidths[1]}' />
                        <ColumnDefinition Width='{_colWidths[2]}' />
                        <ColumnDefinition Width='{_colWidths[3]}' />
                        <ColumnDefinition Width='{_colWidths[4]}' />
                        <ColumnDefinition Width='{_colWidths[5]}' />
                        <ColumnDefinition Width='{_colWidths[6]}' />
                        <ColumnDefinition Width='{_colWidths[7]}' />
                        <ColumnDefinition Width='{_colWidths[8]}' />
                        <ColumnDefinition Width='{_colWidths[9]}' />
                    </Grid.ColumnDefinitions>
                    <CheckBox Grid.Column='0' IsChecked='{{Binding IsSelected, Mode=TwoWay}}' HorizontalAlignment='Center' VerticalAlignment='Center' Margin='0' Padding='0' MinWidth='24' />
                    <TextBlock Grid.Column='1' Text='{{Binding DisplayTitle}}' ToolTipService.ToolTip='{{Binding DisplayTitle}}' FontWeight='SemiBold' TextTrimming='CharacterEllipsis' VerticalAlignment='Center' />
                    <TextBlock Grid.Column='2' Text='{{Binding DisplaySubtitle}}' ToolTipService.ToolTip='{{Binding DisplaySubtitle}}' TextTrimming='CharacterEllipsis' VerticalAlignment='Center' />
                    <TextBlock Grid.Column='3' Text='{{Binding Title}}' ToolTipService.ToolTip='{{Binding Title}}' Opacity='0.85' TextTrimming='CharacterEllipsis' VerticalAlignment='Center' />
                    <TextBlock Grid.Column='4' Text='{{Binding Phone}}' ToolTipService.ToolTip='{{Binding Phone}}' Opacity='0.85' TextTrimming='CharacterEllipsis' VerticalAlignment='Center' />
                    <TextBlock Grid.Column='5' Text='{{Binding Email}}' ToolTipService.ToolTip='{{Binding Email}}' Opacity='0.85' TextTrimming='CharacterEllipsis' VerticalAlignment='Center' />
                    <TextBlock Grid.Column='6' Text='{{Binding DisplayTags}}' ToolTipService.ToolTip='{{Binding DisplayTags}}' FontSize='11' Opacity='0.8' TextTrimming='CharacterEllipsis' VerticalAlignment='Center' />
                    <TextBlock Grid.Column='7' Text='{{Binding DisplayTime}}' ToolTipService.ToolTip='{{Binding DisplayTime}}' FontSize='11' Opacity='0.6' VerticalAlignment='Center' />
                    <TextBlock Grid.Column='8' Text='{{Binding HasImage}}' ToolTipService.ToolTip='名片圖檔狀態' VerticalAlignment='Center' HorizontalAlignment='Center' />
                    <TextBlock Grid.Column='9' Text='檢視/編輯' ToolTipService.ToolTip='雙擊名片列或點選右側明細「載回主畫面編輯」' VerticalAlignment='Center' HorizontalAlignment='Center' Opacity='0.75' FontSize='11' />
                </Grid>
            </DataTemplate>";
        return (DataTemplate)XamlReader.Load(xaml);
    }

    private FrameworkElement CreateColumnResizer(int colIndex)
    {
        var splitter = new Border
        {
            Width = 10,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, -10, -5, -10),
            CanDrag = false
        };
        ToolTipService.SetToolTip(splitter, "雙擊自動調整此欄寬度，或拖曳手動調整");

        bool isDragging = false;
        Point startPoint = default;
        double startWidth = 0;

        splitter.PointerEntered += (s, e) =>
        {
            splitter.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128));
        };
        splitter.PointerExited += (s, e) =>
        {
            if (!isDragging)
                splitter.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        };

        splitter.PointerPressed += (s, e) =>
        {
            if (e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed)
            {
                isDragging = true;
                splitter.CapturePointer(e.Pointer);
                startPoint = e.GetCurrentPoint(this).Position;
                startWidth = _colWidths[colIndex];
                splitter.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                e.Handled = true;
            }
        };

        splitter.PointerMoved += (s, e) =>
        {
            if (isDragging)
            {
                var currentPoint = e.GetCurrentPoint(this).Position;
                double delta = currentPoint.X - startPoint.X;
                double newWidth = Math.Max(50, startWidth + delta);
                _colWidths[colIndex] = newWidth;
                _headerColDefs[colIndex].Width = new GridLength(newWidth);
                e.Handled = true;
            }
        };

        splitter.PointerReleased += (s, e) =>
        {
            if (isDragging)
            {
                isDragging = false;
                splitter.ReleasePointerCapture(e.Pointer);
                splitter.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                _tableListView.ItemTemplate = BuildDataTemplate();
                e.Handled = true;
            }
        };

        splitter.PointerCanceled += (s, e) =>
        {
            if (isDragging)
            {
                isDragging = false;
                splitter.ReleasePointerCapture(e.Pointer);
                splitter.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                _tableListView.ItemTemplate = BuildDataTemplate();
            }
        };

        splitter.DoubleTapped += (s, e) =>
        {
            AutoFitColumn(colIndex);
            e.Handled = true;
        };

        return splitter;
    }

    private double MeasureStringWidth(string? text, double fontSize = 12, bool isBold = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        try
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = isBold ? FontWeights.SemiBold : FontWeights.Normal
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return tb.DesiredSize.Width;
        }
        catch
        {
            double width = 0;
            foreach (char c in text)
            {
                width += (c > 127) ? fontSize : (fontSize * 0.6);
            }
            return width;
        }
    }

    private double CalculateAutoFitWidth(int colIndex)
    {
        if (colIndex <= 0 || colIndex >= 10) return 40;

        double maxWidth = 0;

        // 1. Header Text Width
        string headerText = colIndex switch
        {
            1 => "姓名",
            2 => "公司",
            3 => "職稱",
            4 => "電話",
            5 => "Email",
            6 => "分類標籤",
            7 => "儲存時間",
            8 => "名片圖檔",
            9 => "操作",
            _ => ""
        };
        bool isSortable = (colIndex == 1 || colIndex == 2 || colIndex == 3 || colIndex == 7);
        double headerWidth = MeasureStringWidth(headerText, 12, true) + (isSortable ? 24 : 0);
        maxWidth = Math.Max(maxWidth, headerWidth);

        // 2. Data Rows Width
        if (_tableListView.ItemsSource is IEnumerable<ContactSummaryItem> items)
        {
            foreach (var item in items)
            {
                double w = 0;
                switch (colIndex)
                {
                    case 1:
                        w = MeasureStringWidth(item.DisplayTitle, 12, true);
                        break;
                    case 2:
                        w = MeasureStringWidth(item.DisplaySubtitle, 12, false);
                        break;
                    case 3:
                        w = MeasureStringWidth(item.Title, 12, false);
                        break;
                    case 4:
                        w = MeasureStringWidth(item.Phone, 12, false);
                        break;
                    case 5:
                        w = MeasureStringWidth(item.Email, 12, false);
                        break;
                    case 6:
                        w = MeasureStringWidth(item.DisplayTags, 11, false);
                        break;
                    case 7:
                        w = MeasureStringWidth(item.DisplayTime, 11, false);
                        break;
                    case 8:
                        w = 80;
                        break;
                    case 9:
                        w = 95;
                        break;
                }
                maxWidth = Math.Max(maxWidth, w);
            }
        }

        // 3. Extra padding for cell margin and aesthetics
        double targetWidth = maxWidth + 28;

        // 4. Clamping
        targetWidth = Math.Max(_minColWidths[colIndex], targetWidth);
        targetWidth = Math.Min(_maxColWidths[colIndex], targetWidth);

        return targetWidth;
    }

    public void AutoFitColumn(int colIndex)
    {
        if (colIndex <= 0 || colIndex >= 10) return;

        double fitWidth = CalculateAutoFitWidth(colIndex);
        _colWidths[colIndex] = fitWidth;
        _headerColDefs[colIndex].Width = new GridLength(fitWidth);
        _tableListView.ItemTemplate = BuildDataTemplate();
    }

    public void AutoFitAllColumns()
    {
        _colWidths[0] = 40;
        _headerColDefs[0].Width = new GridLength(40);
        for (int i = 1; i < 10; i++)
        {
            double fitWidth = CalculateAutoFitWidth(i);
            _colWidths[i] = fitWidth;
            _headerColDefs[i].Width = new GridLength(fitWidth);
        }
        _tableListView.ItemTemplate = BuildDataTemplate();
    }

    public async Task RefreshDataAsync()
    {
        await LoadTagChipsAsync();
        await LoadPageAsync(_currentPage);
    }

    private async Task LoadTagChipsAsync()
    {
        if (_storageService is not ITagStorageService tagsService) return;

        try
        {
            var pagedTags = await tagsService.GetTagsAsync(null, 0);
            _tagChipsPanel.Children.Clear();

            // "All" chip
            var allBtn = new Button
            {
                Content = "全部",
                FontSize = 11,
                Padding = new Thickness(10, 3, 10, 3),
                Style = (_filterTags.Count == 0 && !_untagged) ? (Style)Application.Current.Resources["AccentButtonStyle"] : null
            };
            allBtn.Click += async (s, e) =>
            {
                _filterTags = Array.Empty<string>();
                _untagged = false;
                await LoadTagChipsAsync();
                await LoadPageAsync(0);
            };
            _tagChipsPanel.Children.Add(allBtn);

            // "Untagged" chip
            var untaggedBtn = new Button
            {
                Content = "未分類",
                FontSize = 11,
                Padding = new Thickness(10, 3, 10, 3),
                Style = _untagged ? (Style)Application.Current.Resources["AccentButtonStyle"] : null
            };
            untaggedBtn.Click += async (s, e) =>
            {
                _untagged = true;
                _filterTags = Array.Empty<string>();
                await LoadTagChipsAsync();
                await LoadPageAsync(0);
            };
            _tagChipsPanel.Children.Add(untaggedBtn);

            // Individual Tag chips
            foreach (var tag in pagedTags.Items)
            {
                bool isSelected = _filterTags.Contains(tag.TagId);
                var tagChip = new Button
                {
                    Content = $"{tag.DisplayName} ({tag.UsageCount})",
                    FontSize = 11,
                    Padding = new Thickness(8, 3, 8, 3),
                    Style = isSelected ? (Style)Application.Current.Resources["AccentButtonStyle"] : null
                };
                tagChip.Click += async (s, e) =>
                {
                    _untagged = false;
                    _filterTags = isSelected
                        ? _filterTags.Where(t => t != tag.TagId).ToArray()
                        : _filterTags.Concat(new[] { tag.TagId }).ToArray();
                    await LoadTagChipsAsync();
                    await LoadPageAsync(0);
                };
                _tagChipsPanel.Children.Add(tagChip);
            }
        }
        catch
        {
            // Ignore tag chip load failure
        }
    }

    private async Task LoadPageAsync(int pageIndex)
    {
        if (_disposed) return;

        long gen = Interlocked.Increment(ref _pageQueryGeneration);
        _loadingProgressRing.IsActive = true;
        _loadingProgressRing.Visibility = Visibility.Visible;

        try
        {
            var query = new ContactQuery(_currentSearch, _filterTags, _matchAll, _untagged, _currentSortField, _currentSortDescending);
            var paged = await (_storageService is ITagStorageService tagService
                ? tagService.QueryContactsAsync(query, pageIndex, _pageSize)
                : _storageService.GetContactsPagedAsync(_currentSearch, pageIndex, _pageSize));

            if (gen != Volatile.Read(ref _pageQueryGeneration)) return;

            _currentPage = paged.PageIndex;
            _totalPages = paged.TotalPages;

            if (paged.TotalPages > 0 && paged.PageIndex >= paged.TotalPages)
            {
                await LoadPageAsync(paged.TotalPages - 1);
                return;
            }

            foreach (var item in paged.Items)
            {
                item.IsSelected = _selectedContactIds.Contains(item.ContactId);
                item.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(ContactSummaryItem.IsSelected))
                    {
                        if (item.IsSelected)
                            _selectedContactIds.Add(item.ContactId);
                        else
                            _selectedContactIds.Remove(item.ContactId);
                        UpdateSelectionStatus();
                    }
                };
            }

            _tableListView.ItemsSource = paged.Items;

            if (paged.TotalCount == 0)
            {
                _emptyNoticeText.Text = string.IsNullOrWhiteSpace(_currentSearch) ? "尚無已儲存名片" : "找不到符合條件的名片";
                _emptyNoticeText.Visibility = Visibility.Visible;
            }
            else
            {
                _emptyNoticeText.Visibility = Visibility.Collapsed;
            }

            _pageInfoText.Text = _totalPages > 0
                ? $"第 {_currentPage + 1} / {_totalPages} 頁"
                : "第 1 頁";
            _summaryCountText.Text = $"共 {paged.TotalCount} 筆名片 (每頁 {_pageSize} 筆)";
            UpdateSelectionStatus();

            _firstPageBtn.IsEnabled = paged.HasPreviousPage;
            _prevPageBtn.IsEnabled = paged.HasPreviousPage;
            _nextPageBtn.IsEnabled = paged.HasNextPage;
            _lastPageBtn.IsEnabled = paged.HasNextPage;

            if (_tableListView.SelectedItem == null)
            {
                ShowNoSelectionState();
            }
        }
        catch (Exception ex)
        {
            if (gen == Volatile.Read(ref _pageQueryGeneration))
            {
                _emptyNoticeText.Text = $"讀取名片清單失敗 ({ex.GetType().Name})。";
                _emptyNoticeText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (gen == Volatile.Read(ref _pageQueryGeneration))
            {
                _loadingProgressRing.IsActive = false;
                _loadingProgressRing.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ShowNoSelectionState()
    {
        Interlocked.Increment(ref _detailQueryGeneration);
        _currentDetail = null;
        _noSelectionGrid.Visibility = Visibility.Visible;
        _detailContentGrid.Visibility = Visibility.Collapsed;
        _deleteConfirmGrid.Visibility = Visibility.Collapsed;
    }

    private async void OnTableSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed || _isDeleting) return;

        if (_tableListView.SelectedItem is not ContactSummaryItem item)
        {
            ShowNoSelectionState();
            return;
        }

        await LoadContactDetailAsync(item.ContactId);
    }

    private async Task LoadContactDetailAsync(string contactId)
    {
        long gen = Interlocked.Increment(ref _detailQueryGeneration);

        try
        {
            var detail = await _storageService.GetContactDetailAsync(contactId);
            if (gen != Volatile.Read(ref _detailQueryGeneration)) return;

            _currentDetail = detail;
            if (_currentDetail == null)
            {
                ShowNoSelectionState();
                return;
            }

            _noSelectionGrid.Visibility = Visibility.Collapsed;
            _deleteConfirmGrid.Visibility = Visibility.Collapsed;
            _detailContentGrid.Visibility = Visibility.Visible;

            _detailTitleText.Text = _currentDetail.DisplayTitle;
            _detailSubtitleText.Text = _currentDetail.DisplaySubtitle;
            _detailMetaText.Text = $"版本: rev.{_currentDetail.Revision}  |  儲存時間: {(_currentDetail.CompletedAt ?? _currentDetail.CreatedAt).ToLocalTime():yyyy-MM-dd HH:mm}";
            _detailTagsText.Text = "分類標籤：" + _currentDetail.DisplayTags;
            _editTagsBtn.IsEnabled = !_currentDetail.PendingDelete;
            _reloadToScanBtn.IsEnabled = !_currentDetail.PendingDelete;

            // Load Values
            _detailValuesStack.Children.Clear();
            foreach (var val in _currentDetail.Values)
            {
                var rowBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Padding = new Thickness(0, 4, 0, 4) };
                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var labelText = new TextBlock { Text = val.Label, FontWeight = FontWeights.SemiBold, FontSize = 11, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Top };
                Grid.SetColumn(labelText, 0);

                var valText = new TextBlock { Text = val.RawValue, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                Grid.SetColumn(valText, 1);

                rowGrid.Children.Add(labelText);
                rowGrid.Children.Add(valText);
                rowBorder.Child = rowGrid;
                _detailValuesStack.Children.Add(rowBorder);
            }

            // Load Thumbnail Image
            if (_currentDetail.IsImageAvailable && _currentDetail.VerifiedImageBytes != null)
            {
                try
                {
                    using var stream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(_currentDetail.VerifiedImageBytes);
                        await writer.StoreAsync();
                    }
                    stream.Seek(0);

                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(stream);
                    if (gen != Volatile.Read(ref _detailQueryGeneration)) return;

                    _detailImage.Source = bmp;
                    _detailImage.Visibility = Visibility.Visible;
                    _detailImageNotice.Visibility = Visibility.Collapsed;
                    _openBigImageBtn.IsEnabled = true;
                }
                catch
                {
                    _detailImage.Source = null;
                    _detailImage.Visibility = Visibility.Collapsed;
                    _detailImageNotice.Text = "圖片解碼失敗";
                    _detailImageNotice.Visibility = Visibility.Visible;
                    _openBigImageBtn.IsEnabled = false;
                }
            }
            else
            {
                _detailImage.Source = null;
                _detailImage.Visibility = Visibility.Collapsed;
                _detailImageNotice.Text = "此名片未包含圖檔";
                _detailImageNotice.Visibility = Visibility.Visible;
                _openBigImageBtn.IsEnabled = false;
            }

            string? absPath = _storageService.GetCardImageAbsolutePath(contactId);
            _openSystemImageBtn.IsEnabled = !string.IsNullOrEmpty(absPath) && File.Exists(absPath);
        }
        catch
        {
            if (gen == Volatile.Read(ref _detailQueryGeneration))
            {
                ShowNoSelectionState();
            }
        }
    }

    private async void OnDetailOpenBigImageClicked(object sender, RoutedEventArgs e)
    {
        if (_currentDetail == null) return;
        string? absPath = _storageService.GetCardImageAbsolutePath(_currentDetail.ContactId);
        var viewerDialog = new CardImageViewerDialog(_currentDetail, absPath)
        {
            XamlRoot = this.XamlRoot
        };
        await viewerDialog.ShowAsync();
    }

    private void OnDetailOpenSystemImageClicked(object sender, RoutedEventArgs e)
    {
        if (_currentDetail == null) return;
        string? absPath = _storageService.GetCardImageAbsolutePath(_currentDetail.ContactId);
        if (!string.IsNullOrEmpty(absPath) && File.Exists(absPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = absPath, UseShellExecute = true });
            }
            catch
            {
                // Ignore launch error
            }
        }
    }

    private async void OnTagFilterClicked(object sender, RoutedEventArgs e)
    {
        if (_storageService is not ITagStorageService tags) return;

        var definitions = await tags.FindTagsAsync(_filterTags);
        var selected = definitions.Select(t => new TagChoice(t.TagId, t.DisplayName)).ToArray();

        var picker = new TagPickerPanel(tags, selected, false);
        var allCheck = new CheckBox { Content = "符合全部選取標籤（預設為任一）", IsChecked = _matchAll, Margin = new Thickness(0, 4, 0, 4) };
        var untaggedCheck = new CheckBox { Content = "只查未分類名片", IsChecked = _untagged, Margin = new Thickness(0, 4, 0, 4) };

        untaggedCheck.Checked += (s, ev) => picker.IsEnabled = false;
        untaggedCheck.Unchecked += (s, ev) => picker.IsEnabled = true;

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(picker);
        stack.Children.Add(allCheck);
        stack.Children.Add(untaggedCheck);

        var dialog = new ContentDialog
        {
            Title = "標籤進階篩選",
            Content = stack,
            PrimaryButtonText = "套用篩選",
            CloseButtonText = "取消",
            XamlRoot = this.XamlRoot,
            MaxWidth = 780
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1400.0;
        dialog.Resources["ContentDialogMinWidth"] = 480.0;

        var res = await dialog.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            _untagged = untaggedCheck.IsChecked == true;
            _matchAll = allCheck.IsChecked == true;
            _filterTags = _untagged ? Array.Empty<string>() : picker.Choices.Where(t => t.TagId != null).Select(t => t.TagId!).ToArray();
            await LoadTagChipsAsync();
            await LoadPageAsync(0);
        }
    }

    private async void OnManageTagsClicked(object sender, RoutedEventArgs e)
    {
        if (_storageService is not ITagStorageService tags) return;

        var catalog = new TagCatalogPanel(tags);
        var dialog = new ContentDialog
        {
            Title = "管理分類標籤",
            Content = catalog,
            CloseButtonText = "關閉",
            XamlRoot = this.XamlRoot,
            MaxWidth = 920
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1400.0;
        dialog.Resources["ContentDialogMinWidth"] = 480.0;
        dialog.Closing += (s, args) => { if (catalog.Busy) args.Cancel = true; };

        await dialog.ShowAsync();
        await LoadTagChipsAsync();
        await LoadPageAsync(_currentPage);
    }

    private async Task EditDetailTagsAsync()
    {
        if (_currentDetail == null || _storageService is not ITagStorageService tags) return;

        var currentChoices = _currentDetail.Tags.Select(t => new TagChoice(t.TagId, t.DisplayName)).ToList();
        var picker = new TagPickerPanel(tags, currentChoices, true);

        var dialog = new ContentDialog
        {
            Title = $"調整標籤：{_currentDetail.DisplayTitle}",
            Content = picker,
            PrimaryButtonText = "儲存標籤",
            CloseButtonText = "取消",
            XamlRoot = this.XamlRoot,
            MaxWidth = 780
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1400.0;
        dialog.Resources["ContentDialogMinWidth"] = 480.0;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var mutation = await tags.SetTagsAsync(_currentDetail.ContactId, _currentDetail.TagRevision, picker.Choices);
            if (mutation.Succeeded)
            {
                await LoadContactDetailAsync(_currentDetail.ContactId);
                await LoadTagChipsAsync();
                await LoadPageAsync(_currentPage);
            }
        }
    }

    private void OnStartDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_currentDetail == null) return;
        _deleteTargetContact = _currentDetail;

        _detailContentGrid.Visibility = Visibility.Collapsed;
        _deleteConfirmGrid.Visibility = Visibility.Visible;

        _deleteSummaryText.Text = $"名片：{_deleteTargetContact.DisplayTitle} ({_deleteTargetContact.DisplaySubtitle})";
        _deleteErrorText.Visibility = Visibility.Collapsed;
        _cancelDeleteBtn.Focus(FocusState.Programmatic);
    }

    private void CancelDelete()
    {
        _deleteTargetContact = null;
        _deleteConfirmGrid.Visibility = Visibility.Collapsed;
        _detailContentGrid.Visibility = Visibility.Visible;
    }

    private async Task ConfirmDeleteAsync()
    {
        if (_deleteTargetContact == null || _isDeleting) return;

        string contactId = _deleteTargetContact.ContactId;
        _isDeleting = true;
        _confirmDeleteBtn.IsEnabled = false;
        _cancelDeleteBtn.IsEnabled = false;
        _deleteErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var result = await _storageService.DeleteContactAsync(contactId);
            if (result.Succeeded)
            {
                ActiveContactDeleted?.Invoke(this, contactId);
                _deleteTargetContact = null;
                ShowNoSelectionState();
                await LoadPageAsync(_currentPage);
            }
            else
            {
                _deleteErrorText.Text = $"刪除未完成: {result.ErrorMessage ?? result.ErrorCode}";
                _deleteErrorText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            _deleteErrorText.Text = $"刪除失敗: {ex.Message}";
            _deleteErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            _isDeleting = false;
            _confirmDeleteBtn.IsEnabled = true;
            _cancelDeleteBtn.IsEnabled = true;
        }
    }

    private async Task<IReadOnlyList<ContactDetailModel>> GetContactsForExportAsync()
    {
        var allDetails = new List<ContactDetailModel>();
        int pageSize = 100;
        int pageIndex = 0;

        while (true)
        {
            var query = new ContactQuery(_currentSearch, _filterTags, _matchAll, _untagged, _currentSortField, _currentSortDescending);
            var paged = await (_storageService is ITagStorageService tagService
                ? tagService.QueryContactsAsync(query, pageIndex, pageSize)
                : _storageService.GetContactsPagedAsync(_currentSearch, pageIndex, pageSize));

            if (paged.Items.Count == 0) break;

            foreach (var item in paged.Items)
            {
                var detail = await _storageService.GetContactDetailAsync(item.ContactId);
                if (detail != null)
                {
                    allDetails.Add(detail);
                }
            }

            if (!paged.HasNextPage) break;
            pageIndex++;
        }

        return allDetails;
    }

    private IntPtr GetParentWindowHandle()
    {
        try
        {
            if (MainWindow.CurrentWindow != null)
            {
                return WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.CurrentWindow);
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    private async Task OnImportExcelCsvClickedAsync()
    {
        try
        {
            var dialog = new ImportContactsDialog(
                this.XamlRoot,
                _excelCsvService,
                _storageService,
                async () =>
                {
                    await LoadTagChipsAsync();
                    await LoadPageAsync(_currentPage);
                });
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("開啟匯入視窗失敗", $"發生錯誤：{ex.Message}");
        }
    }

    private void OnHeaderSelectAllChecked(object sender, RoutedEventArgs e)
    {
        if (_tableListView.ItemsSource is IEnumerable<ContactSummaryItem> items)
        {
            foreach (var item in items)
            {
                item.IsSelected = true;
                _selectedContactIds.Add(item.ContactId);
            }
            UpdateSelectionStatus();
        }
    }

    private void OnHeaderSelectAllUnchecked(object sender, RoutedEventArgs e)
    {
        if (_tableListView.ItemsSource is IEnumerable<ContactSummaryItem> items)
        {
            foreach (var item in items)
            {
                item.IsSelected = false;
                _selectedContactIds.Remove(item.ContactId);
            }
            UpdateSelectionStatus();
        }
    }

    private void ClearAllSelections()
    {
        _selectedContactIds.Clear();
        if (_tableListView.ItemsSource is IEnumerable<ContactSummaryItem> items)
        {
            foreach (var item in items)
            {
                item.IsSelected = false;
            }
        }
        UpdateSelectionStatus();
    }

    private void UpdateSelectionStatus()
    {
        int count = _selectedContactIds.Count;
        if (count > 0)
        {
            _selectionStatusText.Text = $"☑️ 已勾選 {count} 筆";
            _clearSelectionBtn.Visibility = Visibility.Visible;
        }
        else
        {
            _selectionStatusText.Text = string.Empty;
            _clearSelectionBtn.Visibility = Visibility.Collapsed;
        }

        if (_tableListView.ItemsSource is IEnumerable<ContactSummaryItem> items)
        {
            var list = items.ToList();
            if (list.Count > 0)
            {
                bool allChecked = list.All(x => x.IsSelected);
                bool anyChecked = list.Any(x => x.IsSelected);
                _headerSelectAllChk.Checked -= OnHeaderSelectAllChecked;
                _headerSelectAllChk.Unchecked -= OnHeaderSelectAllUnchecked;
                _headerSelectAllChk.IsChecked = allChecked ? true : (anyChecked ? null : false);
                _headerSelectAllChk.Checked += OnHeaderSelectAllChecked;
                _headerSelectAllChk.Unchecked += OnHeaderSelectAllUnchecked;
            }
        }
    }

    public async Task OnExportWithDialogClickedAsync(ExportCardsScope? defaultScope = null, ExportCardsFormat defaultFormat = ExportCardsFormat.Excel)
    {
        try
        {
            int selectedCount = _selectedContactIds.Count;
            if (selectedCount == 0 && _currentDetail != null)
            {
                selectedCount = 1;
            }

            var allFiltered = await GetContactsForExportAsync();
            int totalFiltered = allFiltered.Count;

            if (totalFiltered == 0 && selectedCount == 0)
            {
                await ShowNoticeDialogAsync("尚無名片資料", "目前名單中沒有任何名片可供匯出。");
                return;
            }

            var dialog = new ExportCardsDialog(this.XamlRoot, selectedCount, totalFiltered, defaultFormat);
            var options = await dialog.ShowAsync();
            if (options == null || !options.IsConfirmed) return;

            // Gather contacts
            List<ContactDetailModel> exportContacts;
            if (options.Scope == ExportCardsScope.SelectedOnly)
            {
                exportContacts = new List<ContactDetailModel>();
                if (_selectedContactIds.Count > 0)
                {
                    foreach (var id in _selectedContactIds)
                    {
                        var d = await _storageService.GetContactDetailAsync(id);
                        if (d != null) exportContacts.Add(d);
                    }
                }
                else if (_currentDetail != null)
                {
                    exportContacts.Add(_currentDetail);
                }

                if (exportContacts.Count == 0)
                {
                    await ShowNoticeDialogAsync("未勾選名片", "尚未勾選任何名片。");
                    return;
                }
            }
            else
            {
                exportContacts = allFiltered.ToList();
            }

            var savePicker = new FileSavePicker();
            IntPtr hwnd = GetParentWindowHandle();
            if (hwnd != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
            }
            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");

            if (options.Format == ExportCardsFormat.Excel)
            {
                savePicker.FileTypeChoices.Add("Excel 工作表 (*.xlsx)", new List<string> { ".xlsx" });
                savePicker.SuggestedFileName = $"名片匯出_{timestamp}.xlsx";
                var file = await savePicker.PickSaveFileAsync();
                if (file == null) return;

                byte[] bytes = _excelCsvService.ExportToExcel(exportContacts, options.IncludeTags, options.IncludeNotes);
                await FileIO.WriteBytesAsync(file, bytes);
                await ShowNoticeDialogAsync("匯出成功", $"已成功匯出 {exportContacts.Count} 筆名片至 Excel：\n{file.Path}");
            }
            else if (options.Format == ExportCardsFormat.Csv)
            {
                savePicker.FileTypeChoices.Add("CSV 檔案 (*.csv)", new List<string> { ".csv" });
                savePicker.SuggestedFileName = $"名片匯出_{timestamp}.csv";
                var file = await savePicker.PickSaveFileAsync();
                if (file == null) return;

                string csvContent = _excelCsvService.ExportToFullCsv(exportContacts, options.IncludeTags, options.IncludeNotes);
                await FileIO.WriteTextAsync(file, csvContent);
                await ShowNoticeDialogAsync("匯出成功", $"已成功匯出 {exportContacts.Count} 筆名片至 CSV：\n{file.Path}");
            }
            else if (options.Format == ExportCardsFormat.EncryptedPackage)
            {
                savePicker.FileTypeChoices.Add("MajiOCR 加密名片分享包 (*.majicard)", new List<string> { ".majicard" });
                savePicker.SuggestedFileName = $"名片分享包_{timestamp}.majicard";
                var file = await savePicker.PickSaveFileAsync();
                if (file == null) return;

                byte[] pkgBytes = _cardSharingCryptoService.CreateEncryptedPackage(
                    exportContacts,
                    options.Password,
                    options.IncludeTags,
                    options.IncludeNotes,
                    includeImages: true);

                await FileIO.WriteBytesAsync(file, pkgBytes);
                await ShowNoticeDialogAsync("加密分享包建立成功", $"已成功匯出 {exportContacts.Count} 筆加密名片分享包：\n{file.Path}\n\n請妥善保管並將保護密碼告知接收者。");
            }
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("匯出失敗", $"執行匯出時發生錯誤：{ex.Message}");
        }
    }

    private async Task OnImportEncryptedPackageClickedAsync()
    {
        try
        {
            var dialog = new ImportEncryptedCardsDialog(
                this.XamlRoot,
                _cardSharingCryptoService,
                _excelCsvService,
                _storageService,
                async () => await RefreshDataAsync());
            await dialog.ShowAsync(GetParentWindowHandle());
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("讀取加密分享包失敗", $"發生錯誤：{ex.Message}");
        }
    }

    private async Task<List<ContactDetailModel>> GetSelectedContactsForExportAsync()
    {
        var list = new List<ContactDetailModel>();
        if (_selectedContactIds.Count > 0)
        {
            foreach (var id in _selectedContactIds)
            {
                var d = await _storageService.GetContactDetailAsync(id);
                if (d != null) list.Add(d);
            }
        }
        else if (_currentDetail != null)
        {
            list.Add(_currentDetail);
        }
        return list;
    }

    private async Task OnExportSelectedExcelClickedAsync()
    {
        try
        {
            var contacts = await GetSelectedContactsForExportAsync();
            if (contacts.Count == 0)
            {
                await ShowNoticeDialogAsync("未選取任何名片", "請先點選列表中要匯出的名片，後續版本亦將支援勾選多張名片批量匯出。");
                return;
            }

            var savePicker = new FileSavePicker();
            IntPtr hwnd = GetParentWindowHandle();
            if (hwnd != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
            }

            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("Excel 工作表 (*.xlsx)", new List<string> { ".xlsx" });
            savePicker.SuggestedFileName = $"所選名片匯出_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";

            var file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            byte[] bytes = _excelCsvService.ExportToExcel(contacts);
            await FileIO.WriteBytesAsync(file, bytes);

            await ShowNoticeDialogAsync("匯出成功", $"已成功匯出 {contacts.Count} 筆所選名片至 Excel：\n{file.Path}");
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("匯出失敗", $"匯出所選名片時發生錯誤：{ex.Message}");
        }
    }

    private async Task OnExportSelectedCsvClickedAsync()
    {
        try
        {
            var contacts = await GetSelectedContactsForExportAsync();
            if (contacts.Count == 0)
            {
                await ShowNoticeDialogAsync("未選取任何名片", "請先點選列表中要匯出的名片，後續版本亦將支援勾選多張名片批量匯出。");
                return;
            }

            var savePicker = new FileSavePicker();
            IntPtr hwnd = GetParentWindowHandle();
            if (hwnd != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
            }

            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("CSV 檔案 (*.csv)", new List<string> { ".csv" });
            savePicker.SuggestedFileName = $"所選名片匯出_{DateTime.Now:yyyyMMdd_HHmm}.csv";

            var file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            string csvContent = _excelCsvService.ExportToFullCsv(contacts);
            await FileIO.WriteTextAsync(file, csvContent);

            await ShowNoticeDialogAsync("匯出成功", $"已成功匯出 {contacts.Count} 筆所選名片至 CSV：\n{file.Path}");
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("匯出失敗", $"匯出所選名片時發生錯誤：{ex.Message}");
        }
    }

    private async Task OnExportExcelClickedAsync()
    {
        try
        {
            var contacts = await GetContactsForExportAsync();
            if (contacts.Count == 0)
            {
                await ShowNoticeDialogAsync("尚無可匯出的名片", "目前名片清單或篩選結果中沒有任何名片資料。");
                return;
            }

            var savePicker = new FileSavePicker();
            IntPtr hwnd = GetParentWindowHandle();
            if (hwnd != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
            }

            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("Excel 活頁簿 (*.xlsx)", new List<string> { ".xlsx" });
            savePicker.SuggestedFileName = $"名片匯出_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";

            var file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            byte[] bytes = _excelCsvService.ExportToExcel(contacts);
            using (var stream = await file.OpenStreamForWriteAsync())
            {
                stream.SetLength(0);
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }

            await ShowNoticeDialogAsync("匯出成功", $"已成功匯出 {contacts.Count} 筆名片至 Excel 工作表：\n{file.Path}");
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("匯出失敗", $"發生錯誤：{ex.Message}");
        }
    }

    private async Task OnExportFullCsvClickedAsync()
    {
        try
        {
            var contacts = await GetContactsForExportAsync();
            if (contacts.Count == 0)
            {
                await ShowNoticeDialogAsync("尚無可匯出的名片", "目前名片清單或篩選結果中沒有任何名片資料。");
                return;
            }

            string csvContent = _excelCsvService.ExportToFullCsv(contacts);
            string defaultName = $"名片匯出_{DateTime.Now:yyyyMMdd_HHmm}.csv";
            string? savedPath = await _exportService.ExportToFileAsync(
                csvContent,
                defaultName,
                "CSV 檔案 (*.csv)",
                ".csv",
                GetParentWindowHandle());

            if (!string.IsNullOrEmpty(savedPath))
            {
                await ShowNoticeDialogAsync("匯出成功", $"已成功匯出 {contacts.Count} 筆名片至完整名片 CSV：\n{savedPath}\n\n以 Excel 開啟將自動正常顯示繁體中文。");
            }
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("匯出失敗", $"發生錯誤：{ex.Message}");
        }
    }

    private async Task OnExportOutlookClickedAsync()
    {
        try
        {
            var contacts = await GetContactsForExportAsync();
            if (contacts.Count == 0)
            {
                await ShowNoticeDialogAsync("尚無可匯出的名片", "目前名片清單或篩選結果中沒有任何名片資料。");
                return;
            }

            string csvContent = _exportService.GenerateOutlookCsv(contacts);
            string defaultName = $"名片匯出_Outlook_{DateTime.Now:yyyyMMdd_HHmm}.csv";
            string? savedPath = await _exportService.ExportToFileAsync(
                csvContent,
                defaultName,
                "CSV 檔案 (*.csv)",
                ".csv",
                GetParentWindowHandle());

            if (!string.IsNullOrEmpty(savedPath))
            {
                await ShowNoticeDialogAsync("匯出成功", $"已成功匯出 {contacts.Count} 筆名片至 Outlook CSV 表單：\n{savedPath}");
            }
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("匯出失敗", $"發生錯誤：{ex.Message}");
        }
    }

    private async Task OnExportGoogleClickedAsync()
    {
        try
        {
            var contacts = await GetContactsForExportAsync();
            if (contacts.Count == 0)
            {
                await ShowNoticeDialogAsync("尚無可匯出的名片", "目前名片清單或篩選結果中沒有任何名片資料。");
                return;
            }

            string csvContent = _exportService.GenerateGoogleCsv(contacts);
            string defaultName = $"名片匯出_Google_{DateTime.Now:yyyyMMdd_HHmm}.csv";
            string? savedPath = await _exportService.ExportToFileAsync(
                csvContent,
                defaultName,
                "CSV 檔案 (*.csv)",
                ".csv",
                GetParentWindowHandle());

            if (!string.IsNullOrEmpty(savedPath))
            {
                await ShowNoticeDialogAsync("匯出成功", $"已成功匯出 {contacts.Count} 筆名片至 Google Contacts CSV：\n{savedPath}\n\n您可至 contacts.google.com 點擊「匯入」選取此檔案。");
            }
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("匯出失敗", $"發生錯誤：{ex.Message}");
        }
    }

    private async Task OnExportVCardClickedAsync()
    {
        try
        {
            var contacts = await GetContactsForExportAsync();
            if (contacts.Count == 0)
            {
                await ShowNoticeDialogAsync("尚無可匯出的名片", "目前名片清單或篩選結果中沒有任何名片資料。");
                return;
            }

            string vcfContent = _exportService.GenerateVCard(contacts);
            string defaultName = $"名片匯出_{DateTime.Now:yyyyMMdd_HHmm}.vcf";
            string? savedPath = await _exportService.ExportToFileAsync(
                vcfContent,
                defaultName,
                "vCard 名片檔 (*.vcf)",
                ".vcf",
                GetParentWindowHandle());

            if (!string.IsNullOrEmpty(savedPath))
            {
                await ShowNoticeDialogAsync("匯出成功", $"已成功匯出 {contacts.Count} 筆名片至通用 vCard 檔案：\n{savedPath}\n\n雙擊此檔案即可直接以 Outlook 或 Windows 聯絡人開啟。");
            }
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("匯出失敗", $"發生錯誤：{ex.Message}");
        }
    }

    private (Expander Expander, TextBox ClientIdBox, PasswordBox ClientSecretBox) CreateOAuthSettingsExpander(
        string? initialClientId,
        string? initialClientSecret,
        string? customGuideNote = null)
    {
        bool hasCredentials = !string.IsNullOrWhiteSpace(initialClientId) && !string.IsNullOrWhiteSpace(initialClientSecret);
        var expander = new Expander
        {
            Header = "⚙️ Google OAuth 憑證設定",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsExpanded = !hasCredentials && !_authService.IsAuthenticated
        };

        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 6) };

        var clientIdBox = new TextBox
        {
            Text = initialClientId ?? string.Empty,
            PlaceholderText = "例如: xxxxxxxx.apps.googleusercontent.com",
            FontSize = 11
        };
        stack.Children.Add(new TextBlock
        {
            Text = "Google OAuth 2.0 Client ID (必填，桌面應用程式)：",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(clientIdBox);

        var clientSecretBox = new PasswordBox
        {
            Password = initialClientSecret ?? string.Empty,
            PlaceholderText = "請輸入 Google OAuth 2.0 Client Secret",
            FontSize = 11
        };
        stack.Children.Add(new TextBlock
        {
            Text = "Google OAuth 2.0 Client Secret (必填)：",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(clientSecretBox);

        var openConsoleBtn = new Button
        {
            Content = "🔗 開啟 Google Cloud 憑證頁面 (免費申請 Client ID / Secret)",
            FontSize = 11,
            Padding = new Thickness(8, 4, 8, 4)
        };
        openConsoleBtn.Click += (s, e) => _authService.OpenGoogleCloudConsole();
        stack.Children.Add(openConsoleBtn);

        var visualGuideExpander = new Expander
        {
            Header = "📖 3 步驟圖文申請指引 (點此查看官方截圖教學)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsExpanded = !hasCredentials && !_authService.IsAuthenticated
        };
        var visualGuideView = GoogleGuideDialog.CreateGuideView(() => _authService.OpenGoogleCloudConsole(), imageHeight: 250);
        visualGuideExpander.Content = visualGuideView;
        stack.Children.Add(visualGuideExpander);

        string defaultGuide = "💡 設定說明 (僅需設定一次，系統將永久保存)：\n" +
                              "1. 點擊上方按鈕前往 Google Cloud Console。\n" +
                              "2. 啟用「Google People API」與「Google Drive API」。\n" +
                              "3. 於「憑證」建立「OAuth 用戶端 ID」(類型選擇「桌面應用程式」)。\n" +
                              "4. 複製 Client ID 與 Client Secret 貼至上方欄位即可直接點擊登入。";

        if (!string.IsNullOrWhiteSpace(customGuideNote))
        {
            defaultGuide += $"\n\n{customGuideNote}";
        }

        var guideText = new TextBlock
        {
            Text = defaultGuide,
            FontSize = 11,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(guideText);

        expander.Content = stack;
        return (expander, clientIdBox, clientSecretBox);
    }

    private async Task OnSyncGoogleClickedAsync()
    {
        try
        {
            var contacts = await GetContactsForExportAsync();
            if (contacts.Count == 0)
            {
                await ShowNoticeDialogAsync("尚無可同步的名片", "目前名片清單或篩選結果中沒有任何名片資料。");
                return;
            }

            var stack = new StackPanel { Spacing = 14, Width = 560 };

            var scopeText = new TextBlock
            {
                Text = $"準備同步：共 {contacts.Count} 筆名片",
                FontWeight = FontWeights.Bold,
                FontSize = 14
            };
            stack.Children.Add(scopeText);

            var descText = new TextBlock
            {
                Text = "本功能將透過 Google People API 將名片資料建立至您的 Google 聯絡人。\n您可直接點擊下方「透過瀏覽器登入 Google 帳號」一鍵完成授權。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.8
            };
            stack.Children.Add(descText);

            // Auth Card Border
            var authCard = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                Padding = new Thickness(16),
                Margin = new Thickness(0, 4, 0, 4)
            };

            var authStack = new StackPanel { Spacing = 10 };

            var authHeaderStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            authHeaderStack.Children.Add(new FontIcon { Glyph = "\uE774", FontSize = 16, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
            authHeaderStack.Children.Add(new TextBlock { Text = "Google 帳號授權狀態", FontWeight = FontWeights.SemiBold, FontSize = 13 });
            authStack.Children.Add(authHeaderStack);

            var authStatusText = new TextBlock
            {
                Text = _authService.IsAuthenticated ? "🟢 已登入 Google 帳號" : "⚪ 尚未登入 Google 帳號",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = _authService.IsAuthenticated
                    ? new SolidColorBrush(Microsoft.UI.Colors.ForestGreen)
                    : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            authStack.Children.Add(authStatusText);

            var loginActionsStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

            var browserLoginBtn = new Button
            {
                Content = "🌐 透過瀏覽器登入 Google 帳號",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                Visibility = _authService.IsAuthenticated ? Visibility.Collapsed : Visibility.Visible
            };

            var logoutBtn = new Button
            {
                Content = "🚪 登出帳號",
                Visibility = _authService.IsAuthenticated ? Visibility.Visible : Visibility.Collapsed
            };

            var authProgressRing = new ProgressRing
            {
                Width = 20,
                Height = 20,
                IsActive = false,
                Visibility = Visibility.Collapsed
            };

            loginActionsStack.Children.Add(browserLoginBtn);
            loginActionsStack.Children.Add(logoutBtn);
            loginActionsStack.Children.Add(authProgressRing);
            authStack.Children.Add(loginActionsStack);

            // Load saved config
            var (savedId, savedSecret) = await _authService.LoadOAuthConfigAsync();

            var (advancedExpander, clientIdBox, clientSecretBox) = CreateOAuthSettingsExpander(
                savedId,
                savedSecret,
                "⭐ 零設定替代方案：您亦可使用選單中的「匯出 Google 聯絡人 (CSV)」，再至 contacts.google.com 一鍵匯入！");

            authStack.Children.Add(advancedExpander);
            authCard.Child = authStack;
            stack.Children.Add(authCard);

            var progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Width = 560,
                Visibility = Visibility.Collapsed
            };
            stack.Children.Add(progressBar);

            var statusText = new TextBlock
            {
                Text = "",
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(statusText);

            var scrollViewer = new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 680,
                MinWidth = 620
            };

            var dialog = new ContentDialog
            {
                Title = "☁️ Google 聯絡人同步",
                Content = scrollViewer,
                PrimaryButtonText = "開始同步",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot,
                MaxWidth = 780
            };
            dialog.Resources["ContentDialogMaxWidth"] = 1400.0;
            dialog.Resources["ContentDialogMinWidth"] = 620.0;
            dialog.Resources["ContentDialogMaxHeight"] = 1200.0;

            // Wire up Browser Login
            browserLoginBtn.Click += async (s, e) =>
            {
                string? customClientId = string.IsNullOrWhiteSpace(clientIdBox.Text) ? null : clientIdBox.Text.Trim();
                string? customClientSecret = string.IsNullOrWhiteSpace(clientSecretBox.Password) ? null : clientSecretBox.Password.Trim();

                if (string.IsNullOrWhiteSpace(customClientId) || string.IsNullOrWhiteSpace(customClientSecret))
                {
                    advancedExpander.IsExpanded = true;
                    authStatusText.Text = "⚠️ 請先於下方設定 Google OAuth Client ID 與 Client Secret，或點擊「開啟 Google Cloud」免費申請。";
                    authStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange);
                    return;
                }

                browserLoginBtn.IsEnabled = false;
                authProgressRing.IsActive = true;
                authProgressRing.Visibility = Visibility.Visible;
                authStatusText.Text = "⏳ 正在等待瀏覽器授權... 請於開啟的瀏覽器視窗中完成 Google 登入並同意授權。";
                authStatusText.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];

                var authResult = await _authService.LoginWithBrowserAsync(customClientId, customClientSecret);

                authProgressRing.IsActive = false;
                authProgressRing.Visibility = Visibility.Collapsed;
                browserLoginBtn.IsEnabled = true;

                if (authResult.Success)
                {
                    authStatusText.Text = "🟢 已成功登入 Google 帳號！";
                    authStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
                    browserLoginBtn.Visibility = Visibility.Collapsed;
                    logoutBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    advancedExpander.IsExpanded = true;
                    authStatusText.Text = $"🔴 授權未完成：{authResult.ErrorMessage}";
                    authStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                }
            };

            // Wire up Logout
            logoutBtn.Click += (s, e) =>
            {
                _authService.Logout();
                authStatusText.Text = "⚪ 尚未登入 Google 帳號";
                authStatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                browserLoginBtn.Visibility = Visibility.Visible;
                logoutBtn.Visibility = Visibility.Collapsed;
            };

            var progress = new Progress<GoogleSyncProgress>(p =>
            {
                progressBar.Value = p.Percentage;
                statusText.Text = p.StatusMessage;
            });

            dialog.PrimaryButtonClick += async (s, args) =>
            {
                args.Cancel = true; // prevent auto close while running
                dialog.IsPrimaryButtonEnabled = false;
                progressBar.Visibility = Visibility.Visible;
                statusText.Visibility = Visibility.Visible;

                string? token = _authService.CurrentAccessToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    statusText.Text = "🔴 請先完成 Google 帳號登入後再進行同步。";
                    statusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                    dialog.IsPrimaryButtonEnabled = true;
                    return;
                }

                var result = await _syncService.SyncToGoogleContactsAsync(contacts, token, progress);

                progressBar.Value = 100;
                statusText.Text = result.SummaryMessage;

                if (result.IsSuccess)
                {
                    statusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
                }
                else
                {
                    statusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                }

                dialog.IsPrimaryButtonEnabled = true;
                dialog.PrimaryButtonText = "再次同步";
                dialog.CloseButtonText = "完成關閉";
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("同步失敗", $"發生錯誤：{ex.Message}");
        }
    }

    private async Task OnBackupToGoogleDriveClickedAsync()
    {
        try
        {
            string storageRoot = StoragePathHelper.GetDefaultStorageRoot();
            var (savedId, savedSecret) = await _authService.LoadOAuthConfigAsync();

            var stack = new StackPanel { Spacing = 14, MaxWidth = 620 };

            var headerStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            headerStack.Children.Add(new FontIcon { Glyph = "\uE753", FontSize = 20, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
            headerStack.Children.Add(new TextBlock { Text = "Google Drive 雲端備份", FontSize = 16, FontWeight = FontWeights.Bold });
            stack.Children.Add(headerStack);

            // Auth Card
            var authCard = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                Padding = new Thickness(12)
            };

            var authStack = new StackPanel { Spacing = 10 };
            var authStatusText = new TextBlock
            {
                Text = _authService.IsAuthenticated ? "🟢 已登入 Google 帳號" : "⚪ 尚未登入 Google 帳號",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = _authService.IsAuthenticated
                    ? new SolidColorBrush(Microsoft.UI.Colors.ForestGreen)
                    : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            authStack.Children.Add(authStatusText);

            var loginActionsStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var browserLoginBtn = new Button
            {
                Content = "🌐 透過瀏覽器登入 Google 帳號",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                Visibility = _authService.IsAuthenticated ? Visibility.Collapsed : Visibility.Visible
            };
            var logoutBtn = new Button
            {
                Content = "🚪 登出帳號",
                Visibility = _authService.IsAuthenticated ? Visibility.Visible : Visibility.Collapsed
            };
            var authProgressRing = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
            loginActionsStack.Children.Add(browserLoginBtn);
            loginActionsStack.Children.Add(logoutBtn);
            loginActionsStack.Children.Add(authProgressRing);
            authStack.Children.Add(loginActionsStack);

            var (advancedExpander, clientIdBox, clientSecretBox) = CreateOAuthSettingsExpander(savedId, savedSecret);
            authStack.Children.Add(advancedExpander);

            authCard.Child = authStack;
            stack.Children.Add(authCard);

            // Backup Content Info Card
            var infoCard = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                Padding = new Thickness(12)
            };
            var infoStack = new StackPanel { Spacing = 6 };
            infoStack.Children.Add(new TextBlock { Text = "📦 雲端備份內容與規格：", FontWeight = FontWeights.SemiBold, FontSize = 13 });
            infoStack.Children.Add(new TextBlock { Text = "• 名片資料庫：contacts.sqlite3 (自動執行 WAL Checkpoint 確保一致性)", FontSize = 11, Opacity = 0.85 });
            infoStack.Children.Add(new TextBlock { Text = "• 名片原圖庫：card-images/ (所有已辨識與儲存的名片正反面圖檔)", FontSize = 11, Opacity = 0.85 });
            infoStack.Children.Add(new TextBlock { Text = "• 版本描述檔：manifest.json (包含版本號、時間戳、名片總筆數與 SHA-256)", FontSize = 11, Opacity = 0.85 });
            infoStack.Children.Add(new TextBlock { Text = "• 雲端存放位置：Google Drive「MajiOCR_Backups」資料夾", FontSize = 11, Opacity = 0.85 });
            infoCard.Child = infoStack;
            stack.Children.Add(infoCard);

            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Width = 560, Visibility = Visibility.Collapsed };
            stack.Children.Add(progressBar);

            var statusText = new TextBlock { Text = "", FontSize = 12, Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
            stack.Children.Add(statusText);

            var scrollViewer = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 650, MinWidth = 600 };
            var dialog = new ContentDialog
            {
                Title = "💾 備份至 Google Drive",
                Content = scrollViewer,
                PrimaryButtonText = "開始備份至雲端",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot,
                MaxWidth = 720
            };
            dialog.Resources["ContentDialogMaxWidth"] = 1400.0;
            dialog.Resources["ContentDialogMinWidth"] = 600.0;

            browserLoginBtn.Click += async (s, e) =>
            {
                string? customClientId = string.IsNullOrWhiteSpace(clientIdBox.Text) ? null : clientIdBox.Text.Trim();
                string? customClientSecret = string.IsNullOrWhiteSpace(clientSecretBox.Password) ? null : clientSecretBox.Password.Trim();
                if (string.IsNullOrWhiteSpace(customClientId) || string.IsNullOrWhiteSpace(customClientSecret))
                {
                    advancedExpander.IsExpanded = true;
                    authStatusText.Text = "⚠️ 請先設定 Google OAuth Client ID 與 Client Secret。";
                    authStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange);
                    return;
                }
                browserLoginBtn.IsEnabled = false;
                authProgressRing.IsActive = true;
                authProgressRing.Visibility = Visibility.Visible;
                authStatusText.Text = "⏳ 正在等待瀏覽器授權... 請於開啟的瀏覽器視窗中完成 Google 登入並同意授權。";
                authStatusText.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
                var authResult = await _authService.LoginWithBrowserAsync(customClientId, customClientSecret);
                authProgressRing.IsActive = false;
                authProgressRing.Visibility = Visibility.Collapsed;
                browserLoginBtn.IsEnabled = true;
                if (authResult.Success)
                {
                    authStatusText.Text = "🟢 已成功登入 Google 帳號！";
                    authStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
                    browserLoginBtn.Visibility = Visibility.Collapsed;
                    logoutBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    authStatusText.Text = $"🔴 授權失敗：{authResult.ErrorMessage}";
                    authStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                }
            };

            logoutBtn.Click += (s, e) =>
            {
                _authService.Logout();
                authStatusText.Text = "⚪ 尚未登入 Google 帳號";
                authStatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                browserLoginBtn.Visibility = Visibility.Visible;
                logoutBtn.Visibility = Visibility.Collapsed;
            };

            dialog.PrimaryButtonClick += async (s, args) =>
            {
                args.Cancel = true;
                dialog.IsPrimaryButtonEnabled = false;
                progressBar.Visibility = Visibility.Visible;
                statusText.Visibility = Visibility.Visible;

                string? token = _authService.CurrentAccessToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    statusText.Text = "🔴 請先點擊上方按鈕透過瀏覽器登入 Google 帳號。";
                    statusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                    dialog.IsPrimaryButtonEnabled = true;
                    return;
                }

                var progress = new Progress<BackupProgress>(p =>
                {
                    progressBar.Value = p.Percentage;
                    statusText.Text = p.StatusMessage;
                    statusText.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
                });

                // 1. Pack local ZIP with current database passphrase
                var (zipPath, manifest) = await _driveBackupService.CreateLocalBackupPackageAsync(storageRoot, "windows", _storageService.CurrentPassphrase, progress);

                // 2. Upload to Drive
                var result = await _driveBackupService.UploadBackupToDriveAsync(zipPath, manifest, token, progress);

                progressBar.Value = 100;
                statusText.Text = result.Message;
                if (result.Success)
                {
                    statusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
                    dialog.PrimaryButtonText = "再次備份";
                    dialog.CloseButtonText = "完成關閉";
                }
                else
                {
                    statusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                }
                dialog.IsPrimaryButtonEnabled = true;

                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("備份失敗", $"發生未預期錯誤：{ex.Message}");
        }
    }

    private async Task OnRestoreFromGoogleDriveClickedAsync()
    {
        try
        {
            string storageRoot = StoragePathHelper.GetDefaultStorageRoot();
            var (savedId, savedSecret) = await _authService.LoadOAuthConfigAsync();

            var stack = new StackPanel { Spacing = 14, MaxWidth = 660 };

            var headerStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            headerStack.Children.Add(new FontIcon { Glyph = "\uE754", FontSize = 20, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
            headerStack.Children.Add(new TextBlock { Text = "從 Google Drive 還原名片資料與圖片", FontSize = 16, FontWeight = FontWeights.Bold });
            stack.Children.Add(headerStack);

            // Auth Card
            var authCard = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                Padding = new Thickness(12)
            };

            var authStack = new StackPanel { Spacing = 10 };
            var authStatusText = new TextBlock
            {
                Text = _authService.IsAuthenticated ? "🟢 已登入 Google 帳號" : "⚪ 尚未登入 Google 帳號",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = _authService.IsAuthenticated
                    ? new SolidColorBrush(Microsoft.UI.Colors.ForestGreen)
                    : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            authStack.Children.Add(authStatusText);

            var loginActionsStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var browserLoginBtn = new Button
            {
                Content = "🌐 透過瀏覽器登入 Google 帳號",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                Visibility = _authService.IsAuthenticated ? Visibility.Collapsed : Visibility.Visible
            };
            var logoutBtn = new Button
            {
                Content = "🚪 登出帳號",
                Visibility = _authService.IsAuthenticated ? Visibility.Visible : Visibility.Collapsed
            };
            var refreshVersionsBtn = new Button
            {
                Content = "🔄 重新整理備份清單",
                Visibility = _authService.IsAuthenticated ? Visibility.Visible : Visibility.Collapsed
            };
            var authProgressRing = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
            loginActionsStack.Children.Add(browserLoginBtn);
            loginActionsStack.Children.Add(logoutBtn);
            loginActionsStack.Children.Add(refreshVersionsBtn);
            loginActionsStack.Children.Add(authProgressRing);
            authStack.Children.Add(loginActionsStack);

            var (advancedExpander, clientIdBox, clientSecretBox) = CreateOAuthSettingsExpander(savedId, savedSecret);
            authStack.Children.Add(advancedExpander);

            authCard.Child = authStack;
            stack.Children.Add(authCard);

            // Version Selection Area
            var versionAreaHeader = new TextBlock { Text = "📋 Google Drive 歷史備份版本：", FontWeight = FontWeights.SemiBold, FontSize = 13 };
            stack.Children.Add(versionAreaHeader);

            var versionsListStack = new StackPanel { Spacing = 8 };
            var versionScroll = new ScrollViewer
            {
                Content = versionsListStack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 220,
                MinHeight = 100,
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8)
            };
            stack.Children.Add(versionScroll);

            // In-place Password Input Card for Encrypted Backups
            var passwordCard = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.DarkGoldenrod),
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                Padding = new Thickness(12),
                Visibility = Visibility.Collapsed
            };

            var pwdStack = new StackPanel { Spacing = 6 };
            pwdStack.Children.Add(new TextBlock
            {
                Text = "🔐 所選備份受 SQLCipher 加密保護，請輸入原主密碼：",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkGoldenrod)
            });

            var restorePwdBox = new PasswordBox
            {
                PlaceholderText = "請輸入此備份的主密碼",
                PasswordChar = "●",
                IsPasswordRevealButtonEnabled = true
            };
            pwdStack.Children.Add(restorePwdBox);

            var pwdErrorText = new TextBlock
            {
                Text = "",
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap
            };
            pwdStack.Children.Add(pwdErrorText);

            passwordCard.Child = pwdStack;
            stack.Children.Add(passwordCard);

            var warningText = new TextBlock
            {
                Text = "⚠️ 還原提醒：還原所選版本將覆蓋本機目前的名片與圖片庫。\n系統將在執行還原前自動建立「本機安全復原快照」，以防任何意外。",
                FontSize = 11,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange)
            };
            stack.Children.Add(warningText);

            var modePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 4, 0, 4) };
            var rbMerge = new RadioButton { GroupName = "DriveRestoreMode", Content = "🔄 資料庫整併 (比對差異，推薦)", IsChecked = true };
            var rbOverwrite = new RadioButton { GroupName = "DriveRestoreMode", Content = "⚠️ 完全覆蓋現有名片庫" };
            modePanel.Children.Add(new TextBlock { Text = "處理方式：", FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
            modePanel.Children.Add(rbMerge);
            modePanel.Children.Add(rbOverwrite);
            stack.Children.Add(modePanel);

            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Width = 560, Visibility = Visibility.Collapsed };
            stack.Children.Add(progressBar);

            var statusText = new TextBlock { Text = "", FontSize = 12, Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
            stack.Children.Add(statusText);

            var scrollViewer = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 650, MinWidth = 620 };
            var dialog = new ContentDialog
            {
                Title = "📥 從 Google Drive 還原名片",
                Content = scrollViewer,
                PrimaryButtonText = "立即還原所選版本",
                CloseButtonText = "取消",
                XamlRoot = this.XamlRoot,
                MaxWidth = 740,
                IsPrimaryButtonEnabled = false
            };
            dialog.Resources["ContentDialogMaxWidth"] = 1400.0;
            dialog.Resources["ContentDialogMinWidth"] = 620.0;

            DriveBackupVersionItem? selectedVersion = null;
            var radioButtons = new List<RadioButton>();

            void SetSelectedVersion(DriveBackupVersionItem item)
            {
                selectedVersion = item;
                dialog.IsPrimaryButtonEnabled = true;
                if (item.IsPasswordProtected)
                {
                    passwordCard.Visibility = Visibility.Visible;
                    pwdErrorText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    passwordCard.Visibility = Visibility.Collapsed;
                    restorePwdBox.Password = string.Empty;
                    pwdErrorText.Visibility = Visibility.Collapsed;
                }
            }

            async Task LoadVersionsAsync()
            {
                string? token = _authService.CurrentAccessToken;

                if (string.IsNullOrWhiteSpace(token))
                {
                    versionsListStack.Children.Clear();
                    versionsListStack.Children.Add(new TextBlock { Text = "請先登入 Google 帳號以查詢雲端備份清單。", Opacity = 0.6, FontSize = 12, Margin = new Thickness(8) });
                    dialog.IsPrimaryButtonEnabled = false;
                    passwordCard.Visibility = Visibility.Collapsed;
                    return;
                }

                versionsListStack.Children.Clear();
                versionsListStack.Children.Add(new TextBlock { Text = "⏳ 正在查詢 Google Drive 備份資料夾...", FontSize = 12, Opacity = 0.8, Margin = new Thickness(8) });

                var items = await _driveBackupService.ListDriveBackupsAsync(token);
                versionsListStack.Children.Clear();
                radioButtons.Clear();
                selectedVersion = null;
                dialog.IsPrimaryButtonEnabled = false;
                passwordCard.Visibility = Visibility.Collapsed;

                if (items.Count == 0)
                {
                    versionsListStack.Children.Add(new TextBlock { Text = "雲端硬碟尚未找到任何備份檔案 (MajiOCR_Backups)。\n請先使用「備份至 Google Drive」功能建立第一筆備份！", FontSize = 12, Opacity = 0.8, Margin = new Thickness(8) });
                    return;
                }

                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var itemCard = new Border
                    {
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                        Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
                        Padding = new Thickness(10, 8, 10, 8)
                    };

                    var cardGrid = new Grid { ColumnSpacing = 10 };
                    cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var rb = new RadioButton { GroupName = "DriveVersionsGroup", IsChecked = i == 0 };
                    Grid.SetColumn(rb, 0);
                    cardGrid.Children.Add(rb);
                    radioButtons.Add(rb);

                    var infoCol = new StackPanel { Spacing = 2 };
                    var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    titleLine.Children.Add(new TextBlock { Text = item.FileName, FontWeight = FontWeights.SemiBold, FontSize = 12 });
                    
                    // Platform badge
                    titleLine.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(item.Platform == "android" ? Microsoft.UI.Colors.Teal : Microsoft.UI.Colors.SteelBlue),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 1, 6, 1),
                        Child = new TextBlock { Text = item.Platform.ToUpperInvariant(), FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
                    });

                    // Encryption badge (AC-03)
                    titleLine.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(item.IsPasswordProtected ? Microsoft.UI.Colors.DarkGoldenrod : Microsoft.UI.Colors.SlateGray),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 1, 6, 1),
                        Child = new TextBlock
                        {
                            Text = item.IsPasswordProtected ? "🔒 已加密 (SQLCipher)" : "🔓 未加密",
                            FontSize = 10,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
                        }
                    });
                    infoCol.Children.Add(titleLine);

                    string sizeStr = item.FileSizeBytes > 0 ? $"{item.FileSizeBytes / 1024} KB" : "未知大小";
                    string timeStr = item.CreatedAtUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
                    infoCol.Children.Add(new TextBlock
                    {
                        Text = $"📅 備份時間: {timeStr} | 📇 名片: {item.ContactCount} 筆 | 🖼️ 圖片: {item.ImageCount} 張 | 💾 大小: {sizeStr}",
                        FontSize = 11,
                        Opacity = 0.8
                    });

                    Grid.SetColumn(infoCol, 1);
                    cardGrid.Children.Add(infoCol);

                    itemCard.Child = cardGrid;

                    var capturedItem = item;
                    rb.Checked += (s, e) => SetSelectedVersion(capturedItem);

                    if (i == 0)
                    {
                        SetSelectedVersion(item);
                    }

                    versionsListStack.Children.Add(itemCard);
                }
            }

            browserLoginBtn.Click += async (s, e) =>
            {
                string? customClientId = string.IsNullOrWhiteSpace(clientIdBox.Text) ? null : clientIdBox.Text.Trim();
                string? customClientSecret = string.IsNullOrWhiteSpace(clientSecretBox.Password) ? null : clientSecretBox.Password.Trim();
                if (string.IsNullOrWhiteSpace(customClientId) || string.IsNullOrWhiteSpace(customClientSecret))
                {
                    advancedExpander.IsExpanded = true;
                    authStatusText.Text = "⚠️ 請先於下方設定 Google OAuth Client ID 與 Client Secret。";
                    authStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkOrange);
                    return;
                }

                browserLoginBtn.IsEnabled = false;
                authProgressRing.IsActive = true;
                authProgressRing.Visibility = Visibility.Visible;
                authStatusText.Text = "⏳ 正在等待瀏覽器授權... 請於開啟的瀏覽器視窗中完成 Google 登入並同意授權。";
                authStatusText.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];

                var authResult = await _authService.LoginWithBrowserAsync(customClientId, customClientSecret);
                authProgressRing.IsActive = false;
                authProgressRing.Visibility = Visibility.Collapsed;
                browserLoginBtn.IsEnabled = true;
                if (authResult.Success)
                {
                    authStatusText.Text = "🟢 已成功登入 Google 帳號！";
                    authStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
                    browserLoginBtn.Visibility = Visibility.Collapsed;
                    logoutBtn.Visibility = Visibility.Visible;
                    refreshVersionsBtn.Visibility = Visibility.Visible;
                    await LoadVersionsAsync();
                }
                else
                {
                    advancedExpander.IsExpanded = true;
                    authStatusText.Text = $"🔴 授權失敗：{authResult.ErrorMessage}";
                    authStatusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                }
            };

            logoutBtn.Click += (s, e) =>
            {
                _authService.Logout();
                authStatusText.Text = "⚪ 尚未登入 Google 帳號";
                authStatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                browserLoginBtn.Visibility = Visibility.Visible;
                logoutBtn.Visibility = Visibility.Collapsed;
                refreshVersionsBtn.Visibility = Visibility.Collapsed;
                versionsListStack.Children.Clear();
                dialog.IsPrimaryButtonEnabled = false;
                passwordCard.Visibility = Visibility.Collapsed;
            };

            refreshVersionsBtn.Click += async (s, e) => await LoadVersionsAsync();

            if (_authService.IsAuthenticated)
            {
                _ = LoadVersionsAsync();
            }

            dialog.PrimaryButtonClick += async (s, args) =>
            {
                if (selectedVersion == null) return;
                args.Cancel = true;

                string? enteredPassphrase = null;
                if (selectedVersion.IsPasswordProtected)
                {
                    enteredPassphrase = restorePwdBox.Password;
                    if (string.IsNullOrEmpty(enteredPassphrase))
                    {
                        pwdErrorText.Text = "⚠️ 此備份受 SQLCipher 密碼保護，請輸入備份主密碼以執行解密校驗。";
                        pwdErrorText.Visibility = Visibility.Visible;
                        restorePwdBox.Focus(FocusState.Programmatic);
                        return;
                    }
                    pwdErrorText.Visibility = Visibility.Collapsed;
                }

                dialog.IsPrimaryButtonEnabled = false;
                progressBar.Visibility = Visibility.Visible;
                statusText.Visibility = Visibility.Visible;

                string? token = _authService.CurrentAccessToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    statusText.Text = "🔴 請先點擊上方按鈕透過瀏覽器登入 Google 帳號。";
                    statusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                    dialog.IsPrimaryButtonEnabled = true;
                    return;
                }

                var progress = new Progress<BackupProgress>(p =>
                {
                    progressBar.Value = p.Percentage;
                    statusText.Text = p.StatusMessage;
                    statusText.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
                });

                string tempZip = Path.Combine(Path.GetTempPath(), $"restore_{Guid.NewGuid():N}.zip");
                try
                {
                    var downloadResult = await _driveBackupService.DownloadBackupFromDriveAsync(selectedVersion.FileId, tempZip, token!, progress);
                    if (!downloadResult.Success)
                    {
                        progressBar.Value = 100;
                        statusText.Text = downloadResult.Message;
                        statusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                        dialog.IsPrimaryButtonEnabled = true;
                        return;
                    }

                    if (rbMerge.IsChecked == true)
                    {
                        dialog.Hide();
                        await ExecuteDatabaseMergeFlowAsync(tempZip, selectedVersion.FileName, enteredPassphrase);
                        return;
                    }

                    var restoreResult = await _driveBackupService.RestoreLocalBackupPackageAsync(tempZip, storageRoot, enteredPassphrase, progress);
                    progressBar.Value = 100;
                    statusText.Text = restoreResult.Message;

                    if (restoreResult.Success)
                    {
                        statusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
                        dialog.CloseButtonText = "完成關閉";

                        // Sync local settings and update in-memory passphrase (AC-09, AC-10)
                        if (restoreResult.Manifest?.IsPasswordProtected == true || (enteredPassphrase != null && StorageService.IsDatabaseEncrypted(Path.Combine(storageRoot, StoragePathHelper.DatabaseFileName))))
                        {
                            var (hash, salt) = PasswordSecurityService.HashPassword(enteredPassphrase!);
                            _settingsService.UpdateSecuritySettings(true, hash, salt);
                            _storageService.SetCurrentPassphrase(enteredPassphrase);
                        }
                        else
                        {
                            _settingsService.UpdateSecuritySettings(false, null, null);
                            _storageService.SetCurrentPassphrase(null);
                        }

                        await RefreshDataAsync();
                    }
                    else
                    {
                        statusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                        if (selectedVersion.IsPasswordProtected)
                        {
                            pwdErrorText.Text = restoreResult.Message;
                            pwdErrorText.Visibility = Visibility.Visible;
                            restorePwdBox.SelectAll();
                            restorePwdBox.Focus(FocusState.Programmatic);
                        }
                    }
                }
                catch (Exception ex)
                {
                    progressBar.Value = 100;
                    statusText.Text = $"🔴 還原過程發生例外錯誤：{ex.Message}";
                    statusText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                    try
                    {
                        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_error.log");
                        File.AppendAllText(logPath, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Restore Error: {ex}\n\n");
                    }
                    catch { }
                }
                finally
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                }
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("還原失敗", $"發生未預期錯誤：{ex.Message}");
        }
    }

    private async Task ShowNoticeDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 },
            CloseButtonText = "確定",
            XamlRoot = this.XamlRoot,
            MaxWidth = 600
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1400.0;
        dialog.Resources["ContentDialogMinWidth"] = 400.0;
        await dialog.ShowAsync();
    }

    #region Public / External Access for Backup, Restore, and Export

    public async Task ImportExcelCsvAsync() => await OnImportExcelCsvClickedAsync();
    public async Task ImportEncryptedPackageAsync() => await OnImportEncryptedPackageClickedAsync();
    public async Task ExportWithDialogAsync() => await OnExportWithDialogClickedAsync();
    public async Task ExportExcelAsync() => await OnExportExcelClickedAsync();
    public async Task ExportFullCsvAsync() => await OnExportFullCsvClickedAsync();
    public async Task ExportOutlookAsync() => await OnExportOutlookClickedAsync();
    public async Task ExportGoogleAsync() => await OnExportGoogleClickedAsync();
    public async Task ExportVCardAsync() => await OnExportVCardClickedAsync();
    public async Task SyncGoogleContactsAsync() => await OnSyncGoogleClickedAsync();
    public async Task BackupToGoogleDriveAsync() => await OnBackupToGoogleDriveClickedAsync();
    public async Task RestoreFromGoogleDriveAsync() => await OnRestoreFromGoogleDriveClickedAsync();
    public async Task BackupLocalZipAsync() => await OnBackupLocalZipClickedAsync();
    public async Task RestoreLocalZipAsync() => await OnRestoreLocalZipClickedAsync();

    public async Task ShowBackupDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "💾 備份名片資料",
            PrimaryButtonText = "💾 備份至本機 ZIP 檔案",
            SecondaryButtonText = "☁️ 備份至 Google Drive",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
        var sp = new StackPanel { Spacing = 8, MaxWidth = 500 };
        sp.Children.Add(new TextBlock
        {
            Text = "請選擇名片資料備份方式：\n\n" +
                   "• 本機 ZIP 檔案：將名片資料庫與名片原圖打包為 .zip 封裝檔，可自由保存於電腦資料夾或外接隨身碟。\n\n" +
                   "• Google Drive 雲端：自動建立備份包並上傳至您的 Google Drive「MajiOCR_Backups」資料夾，支援跨裝置還原。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        });
        dialog.Content = sp;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await OnBackupLocalZipClickedAsync();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await OnBackupToGoogleDriveClickedAsync();
        }
    }

    public async Task ShowRestoreDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "📥 還原名片資料",
            PrimaryButtonText = "📂 從本機 ZIP 檔案還原",
            SecondaryButtonText = "☁️ 從 Google Drive 還原",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
        var sp = new StackPanel { Spacing = 8, MaxWidth = 500 };
        sp.Children.Add(new TextBlock
        {
            Text = "請選擇還原名片資料來源：\n\n" +
                   "⚠️ 注意：還原將會以備份檔中的內容覆蓋目前的本機名片資料庫與圖檔！\n\n" +
                   "• 本機 ZIP 檔案：選取電腦上的 MajiOCR 備份封裝檔 (*.zip) 進行還原。\n\n" +
                   "• Google Drive 雲端：從您的 Google 雲端硬碟讀取歷史備份版本並選擇還原。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        });
        dialog.Content = sp;

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await OnRestoreLocalZipClickedAsync();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await OnRestoreFromGoogleDriveClickedAsync();
        }
    }

    private async Task OnBackupLocalZipClickedAsync()
    {
        try
        {
            string storageRoot = StoragePathHelper.GetDefaultStorageRoot();

            var savePicker = new FileSavePicker();
            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            savePicker.FileTypeChoices.Add("MajiOCR 備份封裝檔 (*.zip)", new List<string> { ".zip" });
            savePicker.SuggestedFileName = $"MajiOCR_Backup_{DateTime.Now:yyyyMMdd_HHmmss}_windows.zip";

            IntPtr hwnd = GetParentWindowHandle();
            if (hwnd != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
            }

            var destFile = await savePicker.PickSaveFileAsync();
            if (destFile == null) return;

            var (zipPath, manifest) = await _driveBackupService.CreateLocalBackupPackageAsync(
                storageRoot, "windows", _storageService.CurrentPassphrase);

            File.Copy(zipPath, destFile.Path, true);
            try { File.Delete(zipPath); } catch { }

            await ShowNoticeDialogAsync("備份成功",
                $"已成功將名片資料打包備份至本機：\n{destFile.Path}\n\n" +
                $"• 名片總筆數：{manifest.ContactCount} 筆\n" +
                $"• 名片圖檔數：{manifest.ImageCount} 張\n" +
                $"• 建立時間：{manifest.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("本機備份失敗", $"發生錯誤：{ex.Message}");
        }
    }

    private async Task OnRestoreLocalZipClickedAsync()
    {
        try
        {
            var openPicker = new FileOpenPicker();
            openPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            openPicker.FileTypeFilter.Add(".zip");

            IntPtr hwnd = GetParentWindowHandle();
            if (hwnd != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hwnd);
            }

            var srcFile = await openPicker.PickSingleFileAsync();
            if (srcFile == null) return;

            string storageRoot = StoragePathHelper.GetDefaultStorageRoot();

            var confirmDialog = new ContentDialog
            {
                Title = "📦 還原或整併名片備份",
                Content = new TextBlock
                {
                    Text = $"來源備份檔：\n{srcFile.Path}\n\n" +
                           $"請選擇處理方式：\n\n" +
                           $"• 【🔄 整併資料庫 (推薦)】：比對差異並顯示更新時間，讓您選擇保留本機、採納外部或兩側同時保留。\n\n" +
                           $"• 【⚠️ 整包覆蓋】：以該備份檔完全取代目前名片庫 (現有名片將被覆蓋)。",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520
                },
                PrimaryButtonText = "🔄 整併資料庫",
                SecondaryButtonText = "⚠️ 整包覆蓋",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };
            var confirmResult = await confirmDialog.ShowAsync();
            if (confirmResult == ContentDialogResult.None) return;

            if (confirmResult == ContentDialogResult.Primary)
            {
                await ExecuteDatabaseMergeFlowAsync(srcFile.Path, srcFile.Name);
            }
            else if (confirmResult == ContentDialogResult.Secondary)
            {
                var result = await _driveBackupService.RestoreLocalBackupPackageAsync(
                    srcFile.Path, storageRoot, _storageService.CurrentPassphrase);

                if (result.Success)
                {
                    await ShowNoticeDialogAsync("還原成功", result.Message + "\n\n名片資料庫與圖檔庫已還原完畢。");
                    await RefreshDataAsync();
                }
                else
                {
                    await ShowNoticeDialogAsync("還原失敗", result.Message);
                }
            }
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("本機還原失敗", $"發生錯誤：{ex.Message}");
        }
    }

    private async Task<bool> ExecuteDatabaseMergeFlowAsync(string zipFilePath, string sourceDisplayName, string? enteredPassphrase = null)
    {
        string storageRoot = StoragePathHelper.GetDefaultStorageRoot();
        string tempStagingDir = Path.Combine(Path.GetTempPath(), $"merge_staging_{Guid.NewGuid():N}");
        try
        {
            if (!File.Exists(zipFilePath))
            {
                await ShowNoticeDialogAsync("檔案不存在", $"找不到指定的備份檔案：{zipFilePath}");
                return false;
            }

            Directory.CreateDirectory(tempStagingDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipFilePath, tempStagingDir);

            string remoteDbPath = Path.Combine(tempStagingDir, StoragePathHelper.DatabaseFileName);
            if (!File.Exists(remoteDbPath))
            {
                await ShowNoticeDialogAsync("備份檔案無效", "備份包內缺少 contacts.sqlite3 資料庫檔案。");
                return false;
            }

            bool isRemoteEncrypted = StorageService.IsDatabaseEncrypted(remoteDbPath);
            string? remotePassphrase = enteredPassphrase;
            if (isRemoteEncrypted && string.IsNullOrEmpty(remotePassphrase))
            {
                if (!string.IsNullOrEmpty(_storageService.CurrentPassphrase) &&
                    StorageService.TryVerifyPassphrase(remoteDbPath, _storageService.CurrentPassphrase, out _))
                {
                    remotePassphrase = _storageService.CurrentPassphrase;
                }
                else
                {
                    var pwdPromptDialog = new ContentDialog
                    {
                        Title = "🔐 備份資料庫受密碼保護",
                        PrimaryButtonText = "解鎖比對",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = this.XamlRoot
                    };
                    var pwdStack = new StackPanel { Spacing = 8, Width = 360 };
                    pwdStack.Children.Add(new TextBlock { Text = "外部備份資料庫已受 SQLCipher 加密，請輸入該備份檔的主密碼以進行資料比對：" });
                    var pwdBox = new PasswordBox { PlaceholderText = "請輸入備份密碼" };
                    var pwdErr = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
                    pwdStack.Children.Add(pwdBox);
                    pwdStack.Children.Add(pwdErr);
                    pwdPromptDialog.Content = pwdStack;

                    pwdPromptDialog.PrimaryButtonClick += (s, e) =>
                    {
                        if (string.IsNullOrEmpty(pwdBox.Password) || !StorageService.TryVerifyPassphrase(remoteDbPath, pwdBox.Password, out var err))
                        {
                            e.Cancel = true;
                            pwdErr.Text = "密碼錯誤或無法解密該資料庫。";
                            pwdErr.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            remotePassphrase = pwdBox.Password;
                        }
                    };

                    var res = await pwdPromptDialog.ShowAsync();
                    if (res != ContentDialogResult.Primary || string.IsNullOrEmpty(remotePassphrase))
                    {
                        return false;
                    }
                }
            }

            var diffReport = await _databaseMergeService.CompareDatabasesAsync(
                storageRoot,
                tempStagingDir,
                _storageService.CurrentPassphrase,
                remotePassphrase);

            var mergeDialog = new DatabaseMergeDialog(this.XamlRoot, diffReport, sourceDisplayName);
            bool shouldProceed = await mergeDialog.ShowAsync();
            if (!shouldProceed) return false;

            var mergeResult = await _databaseMergeService.ExecuteMergeAsync(
                storageRoot,
                tempStagingDir,
                diffReport,
                _storageService.CurrentPassphrase,
                remotePassphrase);

            if (mergeResult.Success)
            {
                await ShowNoticeDialogAsync("資料庫整併成功", mergeResult.Message);
                await RefreshDataAsync();
                return true;
            }
            else
            {
                await ShowNoticeDialogAsync("資料庫整併失敗", mergeResult.Message);
                return false;
            }
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("整併過程發生錯誤", ex.Message);
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempStagingDir))
                {
                    Directory.Delete(tempStagingDir, true);
                }
            }
            catch { }
        }
    }

    public async Task ShowDatabaseSyncDialogAsync()
    {
        await OnDatabaseSyncClickedAsync();
    }

    private async Task OnDatabaseSyncClickedAsync()
    {
        if (this.XamlRoot == null) return;
        var syncDialog = new DatabaseSyncDialog(
            this.XamlRoot,
            _authService,
            _driveBackupService,
            _databaseMergeService,
            _storageService,
            _settingsService,
            RefreshDataAsync);
        await syncDialog.ShowAsync();
    }

    #endregion
}
