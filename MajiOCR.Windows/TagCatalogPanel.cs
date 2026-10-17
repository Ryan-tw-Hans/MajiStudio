using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows;

public sealed class TagCatalogPanel : UserControl
{
    private readonly ITagStorageService _storage;

    // 視圖切換容器
    private readonly Grid _normalView = new();
    private readonly StackPanel _confirmView = new() { Spacing = 12 };

    // 集合資料來源 (ObservableCollection 提供即時 UI 變更通知)
    private readonly ObservableCollection<TagDisplayItem> _tagItems = new();
    private readonly ObservableCollection<MemberDisplayItem> _memberItems = new();

    // 左欄元件 (標籤搜尋與清單)
    private readonly TextBox _searchBox = new()
    {
        PlaceholderText = "搜尋標籤名稱...",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button _searchBtn = new() { Content = "搜尋" };
    private readonly ListView _tagsListView = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        MaxHeight = 360,
        MinHeight = 260,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button _catalogPrev = new() { Content = "◀ 上一頁", IsEnabled = false };
    private readonly Button _catalogNext = new() { Content = "下一頁 ▶", IsEnabled = false };
    private readonly TextBlock _catalogPages = new()
    {
        Text = "0/0（共 0 項）",
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        FontSize = 12
    };

    // 右欄元件 (操作卡片與狀態反饋)
    private readonly InfoBar _noticeInfoBar = new()
    {
        IsOpen = false,
        IsClosable = true,
        Margin = new Thickness(0, 0, 0, 8)
    };

    // 卡片 1: 建立新標籤
    private readonly TextBox _newNameTextBox = new()
    {
        Header = "新標籤名稱",
        PlaceholderText = "輸入新標籤名稱...",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button _addBtn = new()
    {
        Content = "＋ 新增標籤",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    // 卡片 2: 選取標籤維護
    private readonly TextBlock _selectedPrompt = new()
    {
        Text = "← 請從左側清單點選標籤進行管理",
        FontStyle = Windows.UI.Text.FontStyle.Italic,
        FontSize = 12,
        Margin = new Thickness(0, 2, 0, 4)
    };
    private readonly TextBox _renameTextBox = new()
    {
        Header = "修改標籤名稱",
        PlaceholderText = "請先選取標籤...",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        IsEnabled = false
    };
    private readonly Button _renameBtn = new()
    {
        Content = "✏️ 更名",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        IsEnabled = false
    };
    private readonly Button _removeBtn = new()
    {
        Content = "🗑️ 刪除標籤",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        IsEnabled = false
    };

    // 刪除確認視圖元件
    private readonly InfoBar _confirmWarningInfoBar = new()
    {
        Severity = InfoBarSeverity.Warning,
        IsOpen = true,
        IsClosable = false
    };
    private readonly ListView _membersListView = new()
    {
        SelectionMode = ListViewSelectionMode.None,
        MaxHeight = 280,
        MinHeight = 200,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button _memberPrev = new() { Content = "◀ 上一頁", IsEnabled = false };
    private readonly Button _memberNext = new() { Content = "下一頁 ▶", IsEnabled = false };
    private readonly TextBlock _memberPages = new()
    {
        Text = "0/0（共 0 張）",
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        FontSize = 12
    };
    private readonly Button _confirmDeleteBtn = new()
    {
        Content = "⚠️ 確認移除標籤",
        HorizontalAlignment = HorizontalAlignment.Right
    };
    private readonly Button _cancelDeleteBtn = new()
    {
        Content = "取消返回",
        HorizontalAlignment = HorizontalAlignment.Right
    };

    private TagDeletePreview? _preview;
    private int _page, _memberPage;
    private bool _previewListed;
    private long _generation;

    public bool Busy { get; private set; }
    public event EventHandler? Changed;

    public TagCatalogPanel(ITagStorageService storage)
    {
        _storage = storage;

        // 設定根容器寬度約束
        Width = 840;
        MinWidth = 760;
        MaxWidth = 960;

        // 綁定集合
        _tagsListView.ItemsSource = _tagItems;
        _membersListView.ItemsSource = _memberItems;

        // 初始化主佈局 (Normal View: 左右雙欄 Grid)
        BuildNormalView();

        // 初始化刪除確認視圖 (Confirm View: 垂直警告與名片列表)
        BuildConfirmView();

        var rootContainer = new StackPanel
        {
            Spacing = 14,
            Children = { _normalView, _confirmView }
        };
        _confirmView.Visibility = Visibility.Collapsed;

        Content = new ScrollViewer
        {
            Content = rootContainer,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        SetupEvents();

        Loaded += async (_, _) => await LoadAsync(0);
        _ = LoadAsync(0);
    }

    private void BuildNormalView()
    {
        _normalView.ColumnDefinitions.Clear();
        _normalView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        _normalView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18, GridUnitType.Pixel) });
        _normalView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

        // ===== 左欄：標籤搜尋、清單與分頁 =====
        var leftPanel = new Grid();
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 搜尋列
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 清單
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 分頁列

        // 搜尋列
        var searchGrid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_searchBox, 0);
        Grid.SetColumn(_searchBtn, 1);
        searchGrid.Children.Add(_searchBox);
        searchGrid.Children.Add(_searchBtn);
        Grid.SetRow(searchGrid, 0);
        leftPanel.Children.Add(searchGrid);

        // 標籤清單邊框容器
        var listBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] ?? new SolidColorBrush(Colors.LightGray),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] ?? new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(2),
            Margin = new Thickness(0, 0, 0, 8)
        };

        // 標籤項目 Template
        try
        {
            _tagsListView.ItemTemplate = (DataTemplate)XamlReader.Load(@"
                <DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                    <Grid Padding=""8,6"" Margin=""0,1"" ColumnSpacing=""8"" VerticalAlignment=""Center"">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width=""Auto""/>
                            <ColumnDefinition Width=""*""/>
                            <ColumnDefinition Width=""Auto""/>
                        </Grid.ColumnDefinitions>
                        <FontIcon Grid.Column=""0"" Glyph=""&#xE8EC;"" FontSize=""13"" VerticalAlignment=""Center"" Foreground=""{ThemeResource AccentTextFillColorPrimaryBrush}""/>
                        <TextBlock Grid.Column=""1"" Text=""{Binding DisplayName}"" FontWeight=""SemiBold"" FontSize=""13"" VerticalAlignment=""Center"" TextTrimming=""CharacterEllipsis""/>
                        <Border Grid.Column=""2"" BorderThickness=""1"" CornerRadius=""9"" Padding=""6,1"" VerticalAlignment=""Center"" Background=""{ThemeResource LayerFillColorDefaultBrush}"" BorderBrush=""{ThemeResource CardStrokeColorDefaultBrush}"">
                            <TextBlock Text=""{Binding UsageBadge}"" FontSize=""11"" VerticalAlignment=""Center"" Foreground=""{ThemeResource TextFillColorSecondaryBrush}""/>
                        </Border>
                    </Grid>
                </DataTemplate>");
        }
        catch
        {
            _tagsListView.DisplayMemberPath = nameof(TagDisplayItem.DisplayLine);
        }

        listBorder.Child = _tagsListView;
        Grid.SetRow(listBorder, 1);
        leftPanel.Children.Add(listBorder);

        // 分頁控制列
        var pagerGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        pagerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pagerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pagerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_catalogPrev, 0);
        Grid.SetColumn(_catalogPages, 1);
        Grid.SetColumn(_catalogNext, 2);
        pagerGrid.Children.Add(_catalogPrev);
        pagerGrid.Children.Add(_catalogPages);
        pagerGrid.Children.Add(_catalogNext);
        Grid.SetRow(pagerGrid, 2);
        leftPanel.Children.Add(pagerGrid);

        Grid.SetColumn(leftPanel, 0);
        _normalView.Children.Add(leftPanel);

        // ===== 右欄：操作與維護卡片 =====
        var rightPanel = new StackPanel { Spacing = 10 };

        // 狀態通知 InfoBar
        rightPanel.Children.Add(_noticeInfoBar);

        // 卡片 1: 建立新標籤
        var addCard = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] ?? new SolidColorBrush(Colors.LightGray),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] ?? new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(12)
        };
        var addStack = new StackPanel { Spacing = 8 };
        var addTitle = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        addTitle.Children.Add(new FontIcon { Glyph = "\uE710", FontSize = 13, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] ?? new SolidColorBrush(Colors.DodgerBlue) });
        addTitle.Children.Add(new TextBlock { Text = "建立新標籤", FontWeight = FontWeights.Bold, FontSize = 13 });
        addStack.Children.Add(addTitle);
        addStack.Children.Add(_newNameTextBox);
        try
        {
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var styleObj) && styleObj is Style s)
            {
                _addBtn.Style = s;
            }
        }
        catch { }
        addStack.Children.Add(_addBtn);
        addCard.Child = addStack;
        rightPanel.Children.Add(addCard);

        // 卡片 2: 選取標籤維護
        var manageCard = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] ?? new SolidColorBrush(Colors.LightGray),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] ?? new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(12)
        };
        var manageStack = new StackPanel { Spacing = 8 };
        var manageTitle = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        manageTitle.Children.Add(new FontIcon { Glyph = "\uE70F", FontSize = 13, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] ?? new SolidColorBrush(Colors.DodgerBlue) });
        manageTitle.Children.Add(new TextBlock { Text = "選取標籤維護", FontWeight = FontWeights.Bold, FontSize = 13 });
        manageStack.Children.Add(manageTitle);
        manageStack.Children.Add(_selectedPrompt);
        manageStack.Children.Add(_renameTextBox);

        var manageButtons = new Grid { ColumnSpacing = 8 };
        manageButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        manageButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_renameBtn, 0);
        Grid.SetColumn(_removeBtn, 1);
        _removeBtn.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 219, 58, 58));
        manageButtons.Children.Add(_renameBtn);
        manageButtons.Children.Add(_removeBtn);
        manageStack.Children.Add(manageButtons);

        manageStack.Children.Add(new TextBlock
        {
            Text = "ℹ️ 更名會同步更新所有使用此標籤的名片；名片文字與圖片均完整保留。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] ?? new SolidColorBrush(Colors.Gray),
            Margin = new Thickness(0, 4, 0, 0)
        });

        manageCard.Child = manageStack;
        rightPanel.Children.Add(manageCard);

        Grid.SetColumn(rightPanel, 2);
        _normalView.Children.Add(rightPanel);
    }

    private void BuildConfirmView()
    {
        _confirmView.Children.Add(_confirmWarningInfoBar);

        var listCard = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] ?? new SolidColorBrush(Colors.LightGray),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] ?? new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(10)
        };
        var listStack = new StackPanel { Spacing = 8 };
        listStack.Children.Add(new TextBlock { Text = "正使用此標籤的名片清單：", FontWeight = FontWeights.SemiBold, FontSize = 13 });

        try
        {
            _membersListView.ItemTemplate = (DataTemplate)XamlReader.Load(@"
                <DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                    <Grid Padding=""8,6"" Margin=""0,1"" ColumnSpacing=""8"" VerticalAlignment=""Center"">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width=""Auto""/>
                            <ColumnDefinition Width=""*""/>
                            <ColumnDefinition Width=""Auto""/>
                        </Grid.ColumnDefinitions>
                        <FontIcon Grid.Column=""0"" Glyph=""&#xE77B;"" FontSize=""13"" VerticalAlignment=""Center"" Foreground=""{ThemeResource TextFillColorSecondaryBrush}""/>
                        <StackPanel Grid.Column=""1"" VerticalAlignment=""Center"">
                            <TextBlock Text=""{Binding TitleAndCompany}"" FontWeight=""SemiBold"" FontSize=""13"" TextTrimming=""CharacterEllipsis""/>
                            <TextBlock Text=""{Binding DisplayTime}"" FontSize=""11"" Foreground=""{ThemeResource TextFillColorTertiaryBrush}""/>
                        </StackPanel>
                        <Border Grid.Column=""2"" BorderThickness=""1"" CornerRadius=""6"" Padding=""6,2"" VerticalAlignment=""Center"" Background=""{ThemeResource LayerFillColorDefaultBrush}"" BorderBrush=""{ThemeResource CardStrokeColorDefaultBrush}"">
                            <TextBlock Text=""{Binding DisplayStatus}"" FontSize=""11""/>
                        </Border>
                    </Grid>
                </DataTemplate>");
        }
        catch
        {
            _membersListView.DisplayMemberPath = nameof(MemberDisplayItem.FullLine);
        }

        listStack.Children.Add(_membersListView);

        // 分頁控制
        var memberPager = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        memberPager.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        memberPager.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        memberPager.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_memberPrev, 0);
        Grid.SetColumn(_memberPages, 1);
        Grid.SetColumn(_memberNext, 2);
        memberPager.Children.Add(_memberPrev);
        memberPager.Children.Add(_memberPages);
        memberPager.Children.Add(_memberNext);
        listStack.Children.Add(memberPager);

        listCard.Child = listStack;
        _confirmView.Children.Add(listCard);

        _confirmView.Children.Add(new TextBlock
        {
            Text = "⚠️ 提示：確認移除後將清除上述名片中的此標籤；名片本身、文字與圖片皆不會刪除。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] ?? new SolidColorBrush(Colors.Gray)
        });

        // 底部按鈕操作列
        var actionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 6, 0, 0)
        };
        _confirmDeleteBtn.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 196, 43, 28));
        _confirmDeleteBtn.Foreground = new SolidColorBrush(Colors.White);
        actionButtons.Children.Add(_cancelDeleteBtn);
        actionButtons.Children.Add(_confirmDeleteBtn);
        _confirmView.Children.Add(actionButtons);
    }

    private void SetupEvents()
    {
        // 搜尋
        _searchBtn.Click += async (_, _) => await LoadAsync(0);
        _searchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await LoadAsync(0);
            }
        };

        // 清單選取聯動
        _tagsListView.SelectionChanged += (_, _) =>
        {
            if (_tagsListView.SelectedItem is TagDisplayItem item)
            {
                _renameTextBox.Text = item.DisplayName;
                _renameTextBox.IsEnabled = true;
                _renameBtn.IsEnabled = true;
                _removeBtn.IsEnabled = true;
                _selectedPrompt.Text = $"目前選取：🏷️ {item.DisplayName}（{item.UsageCount} 張名片）";
            }
            else
            {
                _renameTextBox.Text = "";
                _renameTextBox.IsEnabled = false;
                _renameBtn.IsEnabled = false;
                _removeBtn.IsEnabled = false;
                _selectedPrompt.Text = "← 請從左側清單點選標籤進行管理";
            }
        };

        // 分頁
        _catalogPrev.Click += async (_, _) => await LoadAsync(_page - 1);
        _catalogNext.Click += async (_, _) => await LoadAsync(_page + 1);

        // 新增標籤
        _addBtn.Click += async (_, _) => await OnAddTagClicked();
        _newNameTextBox.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await OnAddTagClicked();
            }
        };

        // 更名選取標籤
        _renameBtn.Click += async (_, _) => await OnRenameTagClicked();
        _renameTextBox.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await OnRenameTagClicked();
            }
        };

        // 刪除選取標籤
        _removeBtn.Click += async (_, _) =>
        {
            if (_tagsListView.SelectedItem is TagDisplayItem item)
            {
                await PreviewAsync(item.TagId);
            }
            else
            {
                ShowNotice("請先由左側選取欲刪除的標籤。", InfoBarSeverity.Warning);
            }
        };

        // 刪除確認視圖分頁
        _memberPrev.Click += async (_, _) => await LoadMembersAsync(_memberPage - 1);
        _memberNext.Click += async (_, _) => await LoadMembersAsync(_memberPage + 1);

        // 取消刪除
        _cancelDeleteBtn.Click += (_, _) =>
        {
            if (!Busy)
            {
                _generation++;
                _preview = null;
                _confirmView.Visibility = Visibility.Collapsed;
                _normalView.Visibility = Visibility.Visible;
                _noticeInfoBar.IsOpen = false;
            }
        };

        // 確認刪除
        _confirmDeleteBtn.Click += async (_, _) =>
        {
            var preview = _preview;
            if (preview is null || Busy || !_previewListed) return;

            Busy = true;
            _confirmDeleteBtn.IsEnabled = false;
            _cancelDeleteBtn.IsEnabled = false;
            _memberPrev.IsEnabled = false;
            _memberNext.IsEnabled = false;

            try
            {
                var result = await _storage.DeleteTagAsync(preview);

                if (result.Succeeded)
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                    _preview = null;
                    _confirmView.Visibility = Visibility.Collapsed;
                    _normalView.Visibility = Visibility.Visible;
                    ShowNotice($"已成功移除標籤「{preview.Tag.DisplayName}」；名片與圖片均完整保留。", InfoBarSeverity.Success);
                    await LoadAsync(0);
                }
                else
                {
                    ShowNotice(result.Error ?? "移除標籤失敗。", InfoBarSeverity.Error);
                    await PreviewAsync(preview.Tag.TagId);
                }
            }
            catch
            {
                ShowNotice("無法移除標籤，請重新整理後再試。", InfoBarSeverity.Error);
            }
            finally
            {
                Busy = false;
                _cancelDeleteBtn.IsEnabled = true;
                if (_preview is not null)
                {
                    _confirmDeleteBtn.IsEnabled = !_preview.Busy && _previewListed;
                    await LoadMembersAsync(_memberPage);
                }
            }
        };
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        _noticeInfoBar.Title = severity == InfoBarSeverity.Success ? "成功" : (severity == InfoBarSeverity.Error ? "錯誤" : "提示");
        _noticeInfoBar.Message = message;
        _noticeInfoBar.Severity = severity;
        _noticeInfoBar.IsOpen = true;
    }

    private async Task OnAddTagClicked()
    {
        string name = _newNameTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowNotice("請輸入新標籤名稱。", InfoBarSeverity.Warning);
            return;
        }

        if (Busy) return;
        Busy = true;
        _normalView.IsHitTestVisible = false;

        try
        {
            var res = await _storage.CreateTagAsync(name);
            if (res.Succeeded)
            {
                _newNameTextBox.Text = "";
                _searchBox.Text = ""; // 清空搜尋框以確保剛建立的標籤立即可見
                ShowNotice($"標籤「{name}」已成功建立並已選取。", InfoBarSeverity.Success);
                Changed?.Invoke(this, EventArgs.Empty);

                // 即時重新載入第 0 頁，並自動高亮選取新標籤
                await LoadAsync(0, res.Tag?.TagId);
            }
            else
            {
                ShowNotice(res.Error ?? "建立標籤未成功。", InfoBarSeverity.Error);
            }
        }
        catch
        {
            ShowNotice("無法建立標籤。", InfoBarSeverity.Error);
        }
        finally
        {
            Busy = false;
            _normalView.IsHitTestVisible = true;
        }
    }

    private async Task OnRenameTagClicked()
    {
        if (_tagsListView.SelectedItem is not TagDisplayItem item)
        {
            ShowNotice("請先選取欲更名的標籤。", InfoBarSeverity.Warning);
            return;
        }

        string newName = _renameTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(newName))
        {
            ShowNotice("請輸入標籤新名稱。", InfoBarSeverity.Warning);
            return;
        }

        if (Busy) return;
        Busy = true;
        _normalView.IsHitTestVisible = false;

        try
        {
            var res = await _storage.RenameTagAsync(item.Definition, newName);
            if (res.Succeeded)
            {
                ShowNotice($"標籤已更名為「{newName}」。", InfoBarSeverity.Success);
                Changed?.Invoke(this, EventArgs.Empty);

                // 即時重新載入並保持選取
                await LoadAsync(_page, item.TagId);
            }
            else
            {
                ShowNotice(res.Error ?? "更名未成功。", InfoBarSeverity.Error);
            }
        }
        catch
        {
            ShowNotice("無法保存標籤變更。", InfoBarSeverity.Error);
        }
        finally
        {
            Busy = false;
            _normalView.IsHitTestVisible = true;
        }
    }

    private async Task LoadAsync(int page, string? selectTagId = null)
    {
        long generation = ++_generation;
        try
        {
            var result = await _storage.GetTagsAsync(_searchBox.Text, Math.Max(0, page));
            if (generation != _generation) return;

            _page = result.PageIndex;

            // 透過 ObservableCollection 即時通知 UI 重繪
            _tagItems.Clear();
            foreach (var tag in result.Items)
            {
                _tagItems.Add(new TagDisplayItem(tag));
            }

            _catalogPrev.IsEnabled = result.HasPreviousPage;
            _catalogNext.IsEnabled = result.HasNextPage;
            _catalogPages.Text = $"{_page + 1}/{Math.Max(1, result.TotalPages)}（共 {result.TotalCount} 項）";

            // 恢復或設定選取
            if (!string.IsNullOrEmpty(selectTagId))
            {
                var target = _tagItems.FirstOrDefault(t => t.TagId == selectTagId);
                _tagsListView.SelectedItem = target;
            }
            else
            {
                _tagsListView.SelectedItem = null;
            }
        }
        catch
        {
            ShowNotice("無法讀取標籤清單。", InfoBarSeverity.Error);
        }
    }

    private async Task PreviewAsync(string id)
    {
        long generation = ++_generation;
        try
        {
            var preview = await _storage.PreviewTagDeleteAsync(id);
            if (generation != _generation) return;

            _previewListed = false;
            _confirmDeleteBtn.IsEnabled = false;
            _preview = preview;

            _normalView.Visibility = Visibility.Collapsed;
            _confirmView.Visibility = Visibility.Visible;

            _confirmWarningInfoBar.Title = $"移除標籤確認：『{preview.Tag.DisplayName}』";
            _confirmWarningInfoBar.Message = $"目前共有 {preview.ContactCount} 張名片使用此標籤。\n移除標籤僅會解除關聯，名片本體、文字與圖片皆會完整保留。";

            if (preview.Busy)
            {
                _confirmWarningInfoBar.Message += "\n⚠️ 注意：有名片正在保存或刪除中，請稍候再行確認。";
            }

            await LoadMembersAsync(0);
        }
        catch
        {
            ShowNotice("標籤狀態已變動，請重新載入。", InfoBarSeverity.Error);
        }
    }

    private async Task LoadMembersAsync(int page)
    {
        var preview = _preview;
        if (preview is null) return;
        long generation = ++_generation;

        _previewListed = false;
        _confirmDeleteBtn.IsEnabled = false;
        _memberPrev.IsEnabled = false;
        _memberNext.IsEnabled = false;

        try
        {
            var current = await _storage.PreviewTagDeleteAsync(preview.Tag.TagId);
            if (current.MembershipToken != preview.MembershipToken || current.Tag.Revision != preview.Tag.Revision)
            {
                if (generation == _generation)
                {
                    _previewListed = false;
                    _confirmDeleteBtn.IsEnabled = false;
                    ShowNotice("使用名片清單已變動，請取消後重新列出清單。", InfoBarSeverity.Warning);
                }
                return;
            }

            var result = await _storage.QueryContactsAsync(new(null, new[] { preview.Tag.TagId }), Math.Max(0, page));
            if (generation != _generation) return;

            _memberPage = result.PageIndex;
            _previewListed = true;
            _confirmDeleteBtn.IsEnabled = !preview.Busy && !Busy;

            _memberItems.Clear();
            foreach (var item in result.Items)
            {
                _memberItems.Add(new MemberDisplayItem(item));
            }

            _memberPrev.IsEnabled = result.HasPreviousPage && !Busy;
            _memberNext.IsEnabled = result.HasNextPage && !Busy;
            _memberPages.Text = $"{_memberPage + 1}/{Math.Max(1, result.TotalPages)}（共 {result.TotalCount} 張）";
        }
        catch
        {
            ShowNotice("無法列出使用名片，請重新確認。", InfoBarSeverity.Error);
            _confirmDeleteBtn.IsEnabled = false;
        }
    }
}

public class TagDisplayItem
{
    public TagDefinition Definition { get; }
    public string DisplayName => Definition.DisplayName;
    public string TagId => Definition.TagId;
    public int UsageCount => Definition.UsageCount;
    public string UsageBadge => $"{UsageCount} 張";
    public string DisplayLine => $"{DisplayName}（{UsageCount} 張名片）";

    public TagDisplayItem(TagDefinition def)
    {
        Definition = def;
    }
}

public class MemberDisplayItem
{
    public string DisplayTitle { get; }
    public string Company { get; }
    public string DisplayTime { get; }
    public string DisplayStatus { get; }
    public string TitleAndCompany => string.IsNullOrWhiteSpace(Company) ? DisplayTitle : $"{DisplayTitle}　({Company})";
    public string FullLine => $"{DisplayTitle}　{Company}　{DisplayTime}　{DisplayStatus}";

    public MemberDisplayItem(ContactSummaryItem item)
    {
        DisplayTitle = item.DisplayTitle;
        Company = item.Company;
        DisplayTime = item.DisplayTime;
        DisplayStatus = item.DisplayStatus;
    }
}
