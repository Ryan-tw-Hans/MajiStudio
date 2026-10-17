using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.System;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows;

public partial class SavedCardsDialog : ContentDialog
{
    private readonly IStorageService _storageService;
    public event EventHandler<string>? ActiveContactDeleted;
    private long _pageQueryGeneration = 0;
    private long _detailQueryGeneration = 0;
    private bool _isDeleting = false;
    private ContactDetailModel? _deleteTargetContact = null;
    private int _currentPage = 0;
    private int _totalPages = 0;
    private string? _currentSearch = null;
    private ContactDetailModel? _currentDetail = null;
    private bool _closed;
    private XamlRoot? _hostRoot;
    private Grid _rootGrid = null!;
    private Grid _leftPane = null!;
    private Border _rightPane = null!;

    // Layout controls
    private ColumnDefinition _leftColumnDef = null!;

    // Left column controls
    private TextBox _searchBox = null!;
    private Button _searchBtn = null!;
    private Button _clearSearchBtn = null!;
    private ListView _contactsListView = null!;
    private TextBlock _emptyNoticeText = null!;
    private TextBlock _pageInfoText = null!;
    private Button _prevPageBtn = null!;
    private Button _nextPageBtn = null!;

    // Right column controls
    private Grid _noSelectionNoticeGrid = null!;
    private Grid _detailViewGrid = null!;
    private TextBlock _detailTitleText = null!;
    private TextBlock _detailSubtitleText = null!;
    private TextBlock _detailMetaText = null!;
    private Button _deleteCardBtn = null!;
    private Image _verifiedCardImage = null!;
    private TextBlock _imageNoticeText = null!;
    private StackPanel _valuesStackPanel = null!;

    // In-panel delete confirmation controls
    private Grid _deleteConfirmGrid = null!;
    private TextBlock _deleteSummaryText = null!;
    private TextBlock _deleteErrorText = null!;
    private Button _cancelDeleteBtn = null!;
    private Button _confirmDeleteBtn = null!;

    public SavedCardsDialog(IStorageService storageService)
    {
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));

        Title = "已存名片";
        CloseButtonText = "關閉";
        DefaultButton = ContentDialogButton.Close;
        Resources["ContentDialogMaxWidth"] = 1024.0;
        Resources["ContentDialogMaxHeight"] = 840.0;

        Closing += OnDialogClosing;

        BuildVisualTree();

        Loaded += async (s, e) =>
        {
            _closed = false;
            if (_hostRoot != null) _hostRoot.Changed -= OnHostSizeChanged;
            _hostRoot = XamlRoot;
            if (_hostRoot != null) _hostRoot.Changed += OnHostSizeChanged;
            UpdateAdaptiveLayout();
            await LoadPageAsync(0);
        };
        Closed += (s, e) =>
        {
            _closed = true;
            Interlocked.Increment(ref _pageQueryGeneration);
            Interlocked.Increment(ref _detailQueryGeneration);
            if (_hostRoot != null) _hostRoot.Changed -= OnHostSizeChanged;
            _hostRoot = null;
        };
    }

    private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_isDeleting || _tagBusy || _catalogPanel?.Busy == true)
        {
            args.Cancel = true;
            return;
        }

        if (HandleUnsavedTagClose()) { args.Cancel=true; return; }

        // Invalidate in-flight query generations on close
        Interlocked.Increment(ref _pageQueryGeneration);
        Interlocked.Increment(ref _detailQueryGeneration);
    }

    private void BuildVisualTree()
    {
        var rootGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MaxWidth = 960,
            MaxHeight = 640
        };
        _rootGrid = rootGrid;
        _leftColumnDef = new ColumnDefinition { Width = new GridLength(340) };
        rootGrid.ColumnDefinitions.Add(_leftColumnDef);
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });

        // Left Column (Search & List)
        var leftGrid = new Grid { Margin = new Thickness(0, 0, 12, 0) };
        _leftPane = leftGrid;
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Search Bar
        var searchGrid = new Grid { Margin = new Thickness(0, 0, 0, 8), ColumnSpacing = 6 };
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _searchBox = new TextBox { PlaceholderText = "搜尋姓名、公司或欄位內容" };
        _searchBox.KeyDown += OnSearchBoxKeyDown;
        Grid.SetColumn(_searchBox, 0);

        _searchBtn = new Button { Content = "搜尋" };
        _searchBtn.Click += OnSearchClicked;
        Grid.SetColumn(_searchBtn, 1);

        _clearSearchBtn = new Button { Content = "清空" };
        _clearSearchBtn.Click += OnClearSearchClicked;
        Grid.SetColumn(_clearSearchBtn, 2);

        searchGrid.Children.Add(_searchBox);
        searchGrid.Children.Add(_searchBtn);
        searchGrid.Children.Add(_clearSearchBtn);
        var searchSection = new StackPanel { Spacing=6 };
        searchSection.Children.Add(searchGrid);
        searchSection.Children.Add(BuildTagFilters());
        Grid.SetRow(searchSection, 0);
        leftGrid.Children.Add(searchSection);

        // Contacts ListView
        _contactsListView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        _contactsListView.SelectionChanged += OnContactSelectionChanged;

        string itemTemplateXaml = @"
            <DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
                <Grid Margin='0,4' RowDefinitions='Auto,Auto,Auto,Auto'>
                    <TextBlock Grid.Row='0' Text='{Binding DisplayTitle}' FontWeight='SemiBold' FontSize='14' TextTrimming='CharacterEllipsis' />
                    <TextBlock Grid.Row='1' Text='{Binding DisplaySubtitle}' FontSize='12' Opacity='0.8' TextTrimming='CharacterEllipsis' />
                    <StackPanel Grid.Row='2' Orientation='Horizontal' Spacing='8' Margin='0,4,0,0'>
                        <TextBlock Text='{Binding DisplayTime}' FontSize='11' Opacity='0.6' VerticalAlignment='Center' />
                        <Border BorderThickness='1' CornerRadius='4' Padding='4,1'>
                            <TextBlock Text='{Binding DisplayStatus}' FontSize='10' />
                        </Border>
                    </StackPanel>
                    <TextBlock Grid.Row='3' Text='{Binding DisplayTags}' FontSize='11' TextWrapping='Wrap' Margin='0,4,0,0' />
                </Grid>
            </DataTemplate>";
        _contactsListView.ItemTemplate = (DataTemplate)XamlReader.Load(itemTemplateXaml);

        Grid.SetRow(_contactsListView, 1);
        leftGrid.Children.Add(_contactsListView);

        // Empty Notice Text
        _emptyNoticeText = new TextBlock
        {
            Text = "尚無已儲存名片",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 16),
            Visibility = Visibility.Collapsed,
            Opacity = 0.7
        };
        Grid.SetRow(_emptyNoticeText, 2);
        leftGrid.Children.Add(_emptyNoticeText);

        // Paging Row
        var pagingGrid = new Grid { Margin = new Thickness(0, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        pagingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pagingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pagingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _prevPageBtn = new Button { Content = "上一頁" };
        _prevPageBtn.Click += OnPrevPageClicked;
        Grid.SetColumn(_prevPageBtn, 0);

        _pageInfoText = new TextBlock { Text = "第 1 頁", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        Grid.SetColumn(_pageInfoText, 1);

        _nextPageBtn = new Button { Content = "下一頁" };
        _nextPageBtn.Click += OnNextPageClicked;
        Grid.SetColumn(_nextPageBtn, 2);

        pagingGrid.Children.Add(_prevPageBtn);
        pagingGrid.Children.Add(_pageInfoText);
        pagingGrid.Children.Add(_nextPageBtn);
        Grid.SetRow(pagingGrid, 3);
        leftGrid.Children.Add(pagingGrid);

        Grid.SetColumn(leftGrid, 0);
        rootGrid.Children.Add(leftGrid);

        // Right Column (Detail & In-Panel Delete)
        var rightBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12)
        };
        var rightContainerGrid = new Grid();

        // No selection placeholder
        _noSelectionNoticeGrid = new Grid();
        var placeholderText = new TextBlock
        {
            Text = "請由左側清單選擇一張名片以檢視明細。",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6
        };
        _noSelectionNoticeGrid.Children.Add(placeholderText);
        rightContainerGrid.Children.Add(_noSelectionNoticeGrid);

        // Detail View Grid
        _detailViewGrid = new Grid { Visibility = Visibility.Collapsed };
        _detailViewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _detailViewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _detailViewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var detailHeaderGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        detailHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerStack = new StackPanel();
        _detailTitleText = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold };
        _detailSubtitleText = new TextBlock { FontSize = 13, Opacity = 0.8 };
        _detailMetaText = new TextBlock { FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 2, 0, 0) };
        headerStack.Children.Add(_detailTitleText);
        headerStack.Children.Add(_detailSubtitleText);
        headerStack.Children.Add(_detailMetaText);
        Grid.SetColumn(headerStack, 0);

        _deleteCardBtn = new Button { Content = "刪除名片", VerticalAlignment = VerticalAlignment.Top };
        _deleteCardBtn.Click += OnStartDeleteClicked;
        Grid.SetColumn(_deleteCardBtn, 1);

        detailHeaderGrid.Children.Add(headerStack);
        detailHeaderGrid.Children.Add(_deleteCardBtn);
        Grid.SetRow(detailHeaderGrid, 0);
        _detailViewGrid.Children.Add(detailHeaderGrid);

        var separator = new MenuFlyoutSeparator { Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(separator, 1);
        _detailViewGrid.Children.Add(separator);

        var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var detailContentStack = new StackPanel { Spacing = 12 };

        _detailTags = new TextBlock { TextWrapping=TextWrapping.Wrap };
        detailContentStack.Children.Add(_detailTags);
        _editTagsButton = new Button {Content="編輯標籤"};
        _editTagsButton.Click += async (_,_) => await EditContactTagsAsync();
        detailContentStack.Children.Add(_editTagsButton);

        // Image Section
        var imageBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            MinHeight = 140,
            MaxHeight = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4)
        };
        var imageInnerGrid = new Grid();
        _verifiedCardImage = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _imageNoticeText = new TextBlock
        {
            Text = "圖片無法使用；已確認文字仍保留",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 230, 80, 0)),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12)
        };
        imageInnerGrid.Children.Add(_verifiedCardImage);
        imageInnerGrid.Children.Add(_imageNoticeText);
        imageBorder.Child = imageInnerGrid;
        detailContentStack.Children.Add(imageBorder);

        var openImgBtn = new Button { Content = "🖼️ 開啟大圖檢視", FontSize = 11, Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Center };
        openImgBtn.Click += async (s, e) =>
        {
            if (_currentDetail != null)
            {
                string? abs = _storageService.GetCardImageAbsolutePath(_currentDetail.ContactId);
                var viewer = new CardImageViewerDialog(_currentDetail, abs) { XamlRoot = this.XamlRoot };
                await viewer.ShowAsync();
            }
        };
        detailContentStack.Children.Add(openImgBtn);

        // Values List Section
        var fieldsLabel = new TextBlock { Text = "已確認欄位明細", FontWeight = FontWeights.SemiBold, FontSize = 13 };
        detailContentStack.Children.Add(fieldsLabel);

        _valuesStackPanel = new StackPanel();
        detailContentStack.Children.Add(_valuesStackPanel);

        scrollViewer.Content = detailContentStack;
        Grid.SetRow(scrollViewer, 2);
        _detailViewGrid.Children.Add(scrollViewer);
        rightContainerGrid.Children.Add(_detailViewGrid);

        // In-Panel Delete Confirmation Grid
        _deleteConfirmGrid = new Grid
        {
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(16)
        };
        var deleteStack = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Stretch };

        var deleteInfoBar = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = "確定要刪除這張名片嗎？",
            Message = "名片資料及保存的圖片將一併移除，不提供回收桶。"
        };
        deleteStack.Children.Add(deleteInfoBar);

        _deleteSummaryText = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
        deleteStack.Children.Add(_deleteSummaryText);

        _deleteErrorText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        deleteStack.Children.Add(_deleteErrorText);

        var btnStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 12 };
        _cancelDeleteBtn = new Button { Content = "取消", Width = 90 };
        _cancelDeleteBtn.Click += OnCancelDeleteClicked;

        _confirmDeleteBtn = new Button { Content = "確認刪除", Width = 100 };
        _confirmDeleteBtn.Click += OnConfirmDeleteClicked;

        btnStack.Children.Add(_cancelDeleteBtn);
        btnStack.Children.Add(_confirmDeleteBtn);
        deleteStack.Children.Add(btnStack);

        _deleteConfirmGrid.Children.Add(deleteStack);
        rightContainerGrid.Children.Add(_deleteConfirmGrid);

        rightBorder.Child = rightContainerGrid;
        _rightPane = rightBorder;
        Grid.SetColumn(rightBorder, 1);
        rootGrid.Children.Add(rightBorder);

        Content = rootGrid;
    }

    private void OnHostSizeChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateAdaptiveLayout();

    private void UpdateAdaptiveLayout()
    {
        if (_hostRoot == null || _closed) return;
        double width = Math.Max(0, Math.Min(920, _hostRoot.Size.Width - 80));
        double height = Math.Max(0, Math.Min(640, _hostRoot.Size.Height - 180));
        _rootGrid.Width = width;
        _rootGrid.Height = height;
        bool stacked = width < 680;
        _leftColumnDef.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(320);
        _rootGrid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _rootGrid.RowDefinitions[0].Height = stacked ? new GridLength(0.38, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        _rootGrid.RowDefinitions[1].Height = stacked ? new GridLength(0.62, GridUnitType.Star) : new GridLength(0);
        _leftPane.Margin = stacked ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 12, 0);
        Grid.SetColumn(_rightPane, stacked ? 0 : 1);
        Grid.SetRow(_rightPane, stacked ? 1 : 0);
    }

    private async Task LoadPageAsync(int pageIndex)
    {
        if (_closed) return;
        long pageGen = Interlocked.Increment(ref _pageQueryGeneration);
        try
        {
            var paged = await (_storageService is ITagStorageService tags
                ? tags.QueryContactsAsync(new ContactQuery(_currentSearch,_filterTags,_matchAll,_untagged),pageIndex)
                : _storageService.GetContactsPagedAsync(_currentSearch, pageIndex, 30));
            if (pageGen != Volatile.Read(ref _pageQueryGeneration)) return;

            _currentPage = paged.PageIndex;
            _totalPages = paged.TotalPages;

            if(paged.TotalPages > 0 && paged.PageIndex >= paged.TotalPages) { await LoadPageAsync(paged.TotalPages-1);return; }
            _contactsListView.ItemsSource = paged.Items;

            if (paged.TotalCount == 0)
            {
                _emptyNoticeText.Text = string.IsNullOrWhiteSpace(_currentSearch)
                    ? "尚無已儲存名片"
                    : "找不到符合的名片";
                _emptyNoticeText.Visibility = Visibility.Visible;
            }
            else
            {
                _emptyNoticeText.Visibility = Visibility.Collapsed;
            }

            _pageInfoText.Text = _totalPages > 0
                ? $"第 {_currentPage + 1} / {_totalPages} 頁 (共 {paged.TotalCount} 筆)"
                : "第 1 頁 (共 0 筆)";

            _prevPageBtn.IsEnabled = paged.HasPreviousPage;
            _nextPageBtn.IsEnabled = paged.HasNextPage;

            if (_contactsListView.SelectedItem == null)
            {
                ShowNoSelectionState();
            }
        }
        catch (Exception ex)
        {
            if (pageGen == Volatile.Read(ref _pageQueryGeneration))
            {
                _emptyNoticeText.Text = $"讀取名片清單失敗 ({ex.GetType().Name})。";
                _emptyNoticeText.Visibility = Visibility.Visible;
            }
        }
    }

    private void ShowNoSelectionState()
    {
        Interlocked.Increment(ref _detailQueryGeneration);
        _currentDetail = null;
        _noSelectionNoticeGrid.Visibility = Visibility.Visible;
        _detailViewGrid.Visibility = Visibility.Collapsed;
        _deleteConfirmGrid.Visibility = Visibility.Collapsed;
    }

    private async void OnContactSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_closed || _deleteTargetContact != null || _tagWorkspace != null) return;
        if (_contactsListView.SelectedItem is not ContactSummaryItem item)
        {
            ShowNoSelectionState();
            return;
        }

        long gen = Interlocked.Increment(ref _detailQueryGeneration);

        try
        {
            var detail = await _storageService.GetContactDetailAsync(item.ContactId);
            if (gen != Volatile.Read(ref _detailQueryGeneration))
            {
                return;
            }

            _currentDetail = detail;
            if (_currentDetail == null)
            {
                ShowNoSelectionState();
                return;
            }

            _noSelectionNoticeGrid.Visibility = Visibility.Collapsed;
            _deleteConfirmGrid.Visibility = Visibility.Collapsed;
            _detailViewGrid.Visibility = Visibility.Visible;

            _detailTags.Text = "分類標籤："+_currentDetail.DisplayTags;
            _editTagsButton.IsEnabled = !_currentDetail.PendingDelete && (_currentDetail.PersistenceState=="Ready" || _currentDetail.PersistenceState=="ImageMissing");
            _detailTitleText.Text = _currentDetail.DisplayTitle;
            _detailSubtitleText.Text = _currentDetail.DisplaySubtitle;
            _detailMetaText.Text = $"版本: rev.{_currentDetail.Revision}  |  來源: {_currentDetail.CompletionSource}  |  儲存時間: {(_currentDetail.CompletedAt ?? _currentDetail.CreatedAt).ToLocalTime():yyyy-MM-dd HH:mm:ss}";

            _valuesStackPanel.Children.Clear();
            foreach (var val in _currentDetail.Values)
            {
                var rowBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 6, 0, 6) };
                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var labelText = new TextBlock { Text = val.Label, FontWeight = FontWeights.SemiBold, FontSize = 12, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Top };
                Grid.SetColumn(labelText, 0);

                var valStack = new StackPanel();
                var valText = new TextBlock { Text = val.RawValue, FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                var timeText = new TextBlock { Text = val.LockedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), FontSize = 10, Opacity = 0.5, Margin = new Thickness(0, 2, 0, 0) };
                valStack.Children.Add(valText);
                valStack.Children.Add(timeText);
                Grid.SetColumn(valStack, 1);

                rowGrid.Children.Add(labelText);
                rowGrid.Children.Add(valStack);
                rowBorder.Child = rowGrid;
                _valuesStackPanel.Children.Add(rowBorder);
            }

            // Attempt image decode, but if it fails, preserve text details
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
                    _verifiedCardImage.Source = bmp;
                    _verifiedCardImage.Visibility = Visibility.Visible;
                    _imageNoticeText.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    if (gen != Volatile.Read(ref _detailQueryGeneration)) return;
                    _verifiedCardImage.Source = null;
                    _verifiedCardImage.Visibility = Visibility.Collapsed;
                    _imageNoticeText.Text = "圖片解碼失敗；已確認文字仍保留";
                    _imageNoticeText.Visibility = Visibility.Visible;
                }
            }
            else
            {
                _verifiedCardImage.Source = null;
                _verifiedCardImage.Visibility = Visibility.Collapsed;
                _imageNoticeText.Text = !string.IsNullOrEmpty(_currentDetail.ImageNotice)
                    ? _currentDetail.ImageNotice
                    : "圖片無法使用；已確認文字仍保留";
                _imageNoticeText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception)
        {
            if (gen == Volatile.Read(ref _detailQueryGeneration))
            {
                ShowNoSelectionState();
            }
        }
    }

    private async void OnSearchClicked(object sender, RoutedEventArgs e)
    {
        _currentSearch = string.IsNullOrWhiteSpace(_searchBox.Text) ? null : _searchBox.Text.Trim();
        await LoadPageAsync(0);
    }

    private async void OnClearSearchClicked(object sender, RoutedEventArgs e)
    {
        _searchBox.Text = string.Empty;
        _currentSearch = null;
        _filterTags = Array.Empty<string>();_matchAll=false;_untagged=false;UpdateFilterSummary();
        await LoadPageAsync(0);
    }

    private async void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            _currentSearch = string.IsNullOrWhiteSpace(_searchBox.Text) ? null : _searchBox.Text.Trim();
            await LoadPageAsync(0);
        }
    }

    private async void OnPrevPageClicked(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 0)
        {
            await LoadPageAsync(_currentPage - 1);
        }
    }

    private async void OnNextPageClicked(object sender, RoutedEventArgs e)
    {
        if (_currentPage + 1 < _totalPages)
        {
            await LoadPageAsync(_currentPage + 1);
        }
    }

    private void OnStartDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_currentDetail == null) return;

        _deleteTargetContact = _currentDetail;
        Interlocked.Increment(ref _pageQueryGeneration);
        Interlocked.Increment(ref _detailQueryGeneration);

        // Lock list selection and navigation during delete confirmation
        _contactsListView.IsEnabled = false;
        _searchBox.IsEnabled = false;
        _searchBtn.IsEnabled = false;
        _clearSearchBtn.IsEnabled = false;
        _prevPageBtn.IsEnabled = false;
        _nextPageBtn.IsEnabled = false;

        _detailViewGrid.Visibility = Visibility.Collapsed;
        _deleteConfirmGrid.Visibility = Visibility.Visible;

        _deleteSummaryText.Text = $"名片：{_deleteTargetContact.DisplayTitle}" +
            (!string.IsNullOrEmpty(_deleteTargetContact.DisplaySubtitle) ? $" ({_deleteTargetContact.DisplaySubtitle})" : string.Empty);
        _deleteErrorText.Visibility = Visibility.Collapsed;
        _confirmDeleteBtn.Content = "確認刪除";

        _cancelDeleteBtn.Focus(FocusState.Programmatic);
    }

    private void OnCancelDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_isDeleting) return;
        _deleteTargetContact = null;

        // Unlock list and navigation
        _contactsListView.IsEnabled = true;
        _searchBox.IsEnabled = true;
        _searchBtn.IsEnabled = true;
        _clearSearchBtn.IsEnabled = true;
        _prevPageBtn.IsEnabled = _currentPage > 0;
        _nextPageBtn.IsEnabled = _currentPage + 1 < _totalPages;

        _deleteConfirmGrid.Visibility = Visibility.Collapsed;
        _detailViewGrid.Visibility = Visibility.Visible;
    }

    private async void OnConfirmDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (_deleteTargetContact == null || _isDeleting) return;
        string contactToDelete = _deleteTargetContact.ContactId;

        _isDeleting = true;
        _confirmDeleteBtn.IsEnabled = false;
        _cancelDeleteBtn.IsEnabled = false;
        _deleteErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var result = await _storageService.DeleteContactAsync(contactToDelete);
            if (result.Succeeded)
            {
                ActiveContactDeleted?.Invoke(this, contactToDelete);

                int targetPage = _currentPage;
                if (_contactsListView.Items.Count <= 1 && _currentPage > 0)
                {
                    targetPage = _currentPage - 1;
                }

                _deleteTargetContact = null;
                _contactsListView.IsEnabled = true;
                _searchBox.IsEnabled = true;
                _searchBtn.IsEnabled = true;
                _clearSearchBtn.IsEnabled = true;

                ShowNoSelectionState();
                await LoadPageAsync(targetPage);
            }
            else
            {
                _deleteErrorText.Text = $"刪除尚未完成: {result.ErrorMessage ?? result.ErrorCode}。可點選「重試刪除」再次嘗試。";
                _deleteErrorText.Visibility = Visibility.Visible;
                _confirmDeleteBtn.Content = "重試刪除";
            }
        }
        catch (Exception ex)
        {
            _deleteErrorText.Text = $"刪除失敗 ({ex.GetType().Name})。可點選「重試刪除」再次嘗試。";
            _deleteErrorText.Visibility = Visibility.Visible;
            _confirmDeleteBtn.Content = "重試刪除";
        }
        finally
        {
            _isDeleting = false;
            _confirmDeleteBtn.IsEnabled = true;
            _cancelDeleteBtn.IsEnabled = true;
        }
    }
}
