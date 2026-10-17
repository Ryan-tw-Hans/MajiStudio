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
using Microsoft.UI.Xaml.Media;
using Windows.System;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows;

public sealed class TagPickerPanel : UserControl
{
    private readonly ITagStorageService _storage;
    private readonly List<TagChoice> _choices = new();
    private readonly bool _allowNew;

    // 集合資料來源
    private readonly ObservableCollection<TagChoiceDisplayItem> _selectedItems = new();
    private readonly ObservableCollection<TagPickerOptionItem> _optionItems = new();

    // 左欄元件 (已選標籤與新增)
    private readonly TextBlock _selectedCountHeader = new()
    {
        Text = "📌 已選取的分類標籤 (0)",
        FontWeight = FontWeights.Bold,
        FontSize = 14
    };
    private readonly ListView _selectedListView = new()
    {
        SelectionMode = ListViewSelectionMode.None,
        MaxHeight = 220,
        MinHeight = 140,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock _emptySelectedNotice = new()
    {
        Text = "目前未選取任何標籤（名片將標記為未分類）。",
        FontSize = 12,
        FontStyle = Windows.UI.Text.FontStyle.Italic,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] ?? new SolidColorBrush(Colors.Gray),
        Margin = new Thickness(4, 8, 4, 8),
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly Button _clearAllBtn = new()
    {
        Content = "清空全部選取",
        FontSize = 11,
        HorizontalAlignment = HorizontalAlignment.Right
    };
    private readonly TextBox _newTagNameBox = new()
    {
        Header = "建立新標籤並立即加入",
        PlaceholderText = "輸入標籤名稱...",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button _addNewBtn = new()
    {
        Content = "＋ 加入選擇",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly InfoBar _pickerNotice = new()
    {
        IsOpen = false,
        IsClosable = true,
        Margin = new Thickness(0, 4, 0, 0)
    };

    // 右欄元件 (既有標籤清單與搜尋)
    private readonly TextBox _searchBox = new()
    {
        PlaceholderText = "搜尋既有標籤...",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button _searchBtn = new() { Content = "搜尋" };
    private readonly ListView _optionsListView = new()
    {
        SelectionMode = ListViewSelectionMode.None,
        MaxHeight = 260,
        MinHeight = 180,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button _prevBtn = new() { Content = "◀ 上一頁", IsEnabled = false };
    private readonly Button _nextBtn = new() { Content = "下一頁 ▶", IsEnabled = false };
    private readonly TextBlock _pageText = new()
    {
        Text = "0/0（共 0 項）",
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        FontSize = 12
    };

    private int _page;
    private long _generation;
    private IReadOnlyList<TagDefinition> _definitions = Array.Empty<TagDefinition>();

    public IReadOnlyList<TagChoice> Choices => _choices.ToArray();

    public TagPickerPanel(ITagStorageService storage, IEnumerable<TagChoice> choices, bool allowNew = true)
    {
        _storage = storage;
        _allowNew = allowNew;

        foreach (var c in choices)
        {
            if (!_choices.Any(x => x.Token == c.Token))
            {
                _choices.Add(c);
            }
        }

        Width = 720;
        MinWidth = 660;
        MaxWidth = 780;

        BuildLayout();
        SetupEvents();
        SyncSelectedCollection();

        Loaded += async (_, _) => await LoadPageAsync(0);
        _ = LoadPageAsync(0);
    }

    private void BuildLayout()
    {
        var grid = new Grid
        {
            ColumnSpacing = 18,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 左欄: 已選
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 右欄: 挑選

        // ===== 左欄：已選標籤 + 快速新增 =====
        var leftPanel = new StackPanel { Spacing = 10 };

        // 標題與清空按鈕
        var leftHeaderGrid = new Grid();
        leftHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        leftHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_selectedCountHeader, 0);
        Grid.SetColumn(_clearAllBtn, 1);
        leftHeaderGrid.Children.Add(_selectedCountHeader);
        leftHeaderGrid.Children.Add(_clearAllBtn);
        leftPanel.Children.Add(leftHeaderGrid);

        // 已選清單卡片
        var selectedBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] ?? new SolidColorBrush(Colors.LightGray),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] ?? new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(4)
        };

        var selectedHost = new Grid();
        _selectedListView.ItemsSource = _selectedItems;
        selectedHost.Children.Add(_selectedListView);
        selectedHost.Children.Add(_emptySelectedNotice);
        selectedBorder.Child = selectedHost;
        leftPanel.Children.Add(selectedBorder);

        // 快速新增卡片
        if (_allowNew)
        {
            var addCard = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] ?? new SolidColorBrush(Colors.LightGray),
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] ?? new SolidColorBrush(Colors.Transparent),
                Padding = new Thickness(10)
            };
            var addStack = new StackPanel { Spacing = 6 };
            addStack.Children.Add(_newTagNameBox);
            try
            {
                if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var styleObj) && styleObj is Style s)
                {
                    _addNewBtn.Style = s;
                }
            }
            catch { }
            addStack.Children.Add(_addNewBtn);
            addCard.Child = addStack;
            leftPanel.Children.Add(addCard);
        }

        leftPanel.Children.Add(_pickerNotice);
        Grid.SetColumn(leftPanel, 0);
        grid.Children.Add(leftPanel);

        // ===== 右欄：既有標籤目錄與搜尋 =====
        var rightPanel = new Grid();
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 搜尋
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 清單
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 分頁

        // 搜尋列
        var searchGrid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_searchBox, 0);
        Grid.SetColumn(_searchBtn, 1);
        searchGrid.Children.Add(_searchBox);
        searchGrid.Children.Add(_searchBtn);
        Grid.SetRow(searchGrid, 0);
        rightPanel.Children.Add(searchGrid);

        // 標籤選項清單卡片
        var optionsBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] ?? new SolidColorBrush(Colors.LightGray),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] ?? new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(2),
            Margin = new Thickness(0, 0, 0, 8)
        };

        _optionsListView.ItemsSource = _optionItems;
        optionsBorder.Child = _optionsListView;
        Grid.SetRow(optionsBorder, 1);
        rightPanel.Children.Add(optionsBorder);

        // 分頁控制列
        var pagerGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        pagerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pagerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pagerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_prevBtn, 0);
        Grid.SetColumn(_pageText, 1);
        Grid.SetColumn(_nextBtn, 2);
        pagerGrid.Children.Add(_prevBtn);
        pagerGrid.Children.Add(_pageText);
        pagerGrid.Children.Add(_nextBtn);
        Grid.SetRow(pagerGrid, 2);
        rightPanel.Children.Add(pagerGrid);

        Grid.SetColumn(rightPanel, 1);
        grid.Children.Add(rightPanel);

        Content = new ScrollViewer
        {
            Content = grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private void SetupEvents()
    {
        // 搜尋
        _searchBtn.Click += async (_, _) => await LoadPageAsync(0);
        _searchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await LoadPageAsync(0);
            }
        };

        // 分頁
        _prevBtn.Click += async (_, _) => await LoadPageAsync(_page - 1);
        _nextBtn.Click += async (_, _) => await LoadPageAsync(_page + 1);

        // 清空
        _clearAllBtn.Click += (_, _) =>
        {
            _choices.Clear();
            SyncSelectedCollection();
            RefreshOptionCheckStates();
        };

        // 新增標籤加入選擇
        _addNewBtn.Click += (_, _) => OnAddNewChoice();
        _newTagNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                OnAddNewChoice();
            }
        };
    }

    private void OnAddNewChoice()
    {
        string raw = _newTagNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            ShowNotice("請輸入標籤名稱。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            string display = TagNames.Display(raw);
            var existing = _definitions.FirstOrDefault(t => TagNames.Key(t.DisplayName) == TagNames.Key(display));
            var choice = existing is null ? new TagChoice(null, display) : new TagChoice(existing.TagId, existing.DisplayName);

            if (!_choices.Any(c => c.Token == choice.Token))
            {
                _choices.Add(choice);
                _newTagNameBox.Text = "";
                ShowNotice($"標籤「{display}」已加入選擇；將在儲存名片時保存至標籤庫。", InfoBarSeverity.Success);
                SyncSelectedCollection();
                RefreshOptionCheckStates();
            }
            else
            {
                ShowNotice($"標籤「{display}」已在選取清單中。", InfoBarSeverity.Informational);
            }
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowNotice(string msg, InfoBarSeverity severity)
    {
        _pickerNotice.Title = severity == InfoBarSeverity.Success ? "成功" : (severity == InfoBarSeverity.Error ? "錯誤" : "提示");
        _pickerNotice.Message = msg;
        _pickerNotice.Severity = severity;
        _pickerNotice.IsOpen = true;
    }

    private void SyncSelectedCollection()
    {
        _selectedItems.Clear();
        foreach (var choice in _choices)
        {
            var item = new TagChoiceDisplayItem(choice);
            item.RemoveRequested += (_, _) =>
            {
                _choices.RemoveAll(c => c.Token == choice.Token);
                SyncSelectedCollection();
                RefreshOptionCheckStates();
            };
            _selectedItems.Add(item);
        }

        _selectedCountHeader.Text = $"📌 已選取的分類標籤 ({_choices.Count})";
        _emptySelectedNotice.Visibility = _choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _clearAllBtn.IsEnabled = _choices.Count > 0;
    }

    private void RefreshOptionCheckStates()
    {
        foreach (var opt in _optionItems)
        {
            opt.IsChecked = _choices.Any(c => c.TagId == opt.TagId || TagNames.Key(c.DisplayName) == TagNames.Key(opt.DisplayName));
        }
    }

    private async Task LoadPageAsync(int page)
    {
        long generation = ++_generation;
        try
        {
            var result = await _storage.GetTagsAsync(_searchBox.Text, Math.Max(0, page));
            if (generation != _generation) return;

            _page = result.PageIndex;
            _definitions = result.Items;

            _optionItems.Clear();
            foreach (var tag in result.Items)
            {
                bool isChecked = _choices.Any(c => c.TagId == tag.TagId || TagNames.Key(c.DisplayName) == TagNames.Key(tag.DisplayName));
                var opt = new TagPickerOptionItem(tag, isChecked);
                opt.CheckChanged += (s, checkedState) =>
                {
                    if (checkedState)
                    {
                        if (!_choices.Any(c => c.TagId == tag.TagId))
                        {
                            _choices.Add(new TagChoice(tag.TagId, tag.DisplayName));
                        }
                    }
                    else
                    {
                        _choices.RemoveAll(c => c.TagId == tag.TagId || TagNames.Key(c.DisplayName) == TagNames.Key(tag.DisplayName));
                    }
                    SyncSelectedCollection();
                };
                _optionItems.Add(opt);
            }

            _prevBtn.IsEnabled = result.HasPreviousPage;
            _nextBtn.IsEnabled = result.HasNextPage;
            _pageText.Text = $"{_page + 1}/{Math.Max(1, result.TotalPages)}（共 {result.TotalCount} 項）";
        }
        catch
        {
            if (generation == _generation)
            {
                ShowNotice("無法讀取標籤清單，請稍後重試。", InfoBarSeverity.Error);
            }
        }
    }
}

public sealed class TagChoiceDisplayItem : UserControl
{
    public TagChoice Choice { get; }
    public event EventHandler? RemoveRequested;

    public TagChoiceDisplayItem(TagChoice choice)
    {
        Choice = choice;

        var grid = new Grid
        {
            Padding = new Thickness(8, 4, 6, 4),
            Margin = new Thickness(0, 1, 0, 1),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"] ?? new SolidColorBrush(Colors.Transparent),
            CornerRadius = new CornerRadius(6)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE8EC",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] ?? new SolidColorBrush(Colors.DodgerBlue),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var title = new TextBlock
        {
            Text = choice.DisplayName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var delBtn = new Button
        {
            Content = "✕",
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 219, 58, 58)),
            VerticalAlignment = VerticalAlignment.Center
        };
        delBtn.Click += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(delBtn, 2);
        grid.Children.Add(delBtn);

        Content = grid;
    }
}

public sealed class TagPickerOptionItem : UserControl
{
    private readonly CheckBox _checkBox;
    public TagDefinition Definition { get; }
    public string TagId => Definition.TagId;
    public string DisplayName => Definition.DisplayName;
    public event EventHandler<bool>? CheckChanged;

    public bool IsChecked
    {
        get => _checkBox.IsChecked == true;
        set
        {
            _checkBox.IsChecked = value;
        }
    }

    public TagPickerOptionItem(TagDefinition def, bool initialChecked)
    {
        Definition = def;

        var grid = new Grid
        {
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 1, 0, 1),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _checkBox = new CheckBox
        {
            Content = def.DisplayName,
            IsChecked = initialChecked,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        _checkBox.Checked += (_, _) => CheckChanged?.Invoke(this, true);
        _checkBox.Unchecked += (_, _) => CheckChanged?.Invoke(this, false);

        Grid.SetColumn(_checkBox, 0);
        grid.Children.Add(_checkBox);

        var badge = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"] ?? new SolidColorBrush(Colors.Transparent),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] ?? new SolidColorBrush(Colors.LightGray)
        };
        badge.Child = new TextBlock
        {
            Text = $"{def.UsageCount} 張",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] ?? new SolidColorBrush(Colors.Gray)
        };
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        Content = grid;
    }
}
