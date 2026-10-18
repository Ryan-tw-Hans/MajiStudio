using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows;

/// <summary>
/// 專業級資料庫同步與衝突差異比對對話框 (WinUI 3 現代化商務儀表板)
/// 具備：儀表統計卡、一鍵智慧決策、左右並列雙欄對照、差異欄位色塊高亮、三態視覺化裁決卡與即時搜尋過濾
/// </summary>
public class DatabaseMergeDialog
{
    private readonly XamlRoot _xamlRoot;
    private readonly DatabaseMergeDiffReport _report;
    private readonly string _sourceBackupName;

    private class ConflictCardBinding
    {
        public ContactDiffItem Item { get; set; } = null!;
        public Border CardBorder { get; set; } = null!;
        public RadioButton RbLocal { get; set; } = null!;
        public RadioButton RbRemote { get; set; } = null!;
        public RadioButton RbBoth { get; set; } = null!;
        public TextBlock DecisionHintText { get; set; } = null!;
    }

    private readonly List<ConflictCardBinding> _bindings = new();

    public DatabaseMergeDialog(XamlRoot xamlRoot, DatabaseMergeDiffReport report, string sourceBackupName)
    {
        _xamlRoot = xamlRoot;
        _report = report;
        _sourceBackupName = sourceBackupName;
    }

    public async Task<bool> ShowAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _xamlRoot,
            Title = "🔄 資料庫同步差異比對與衝突裁決中心",
            PrimaryButtonText = "🚀 確認並套用同步",
            CloseButtonText = "取消同步",
            DefaultButton = ContentDialogButton.Primary
        };

        // 設置專業尺寸與自適應寬度
        dialog.Resources["ContentDialogMaxWidth"] = 1200.0;
        dialog.Resources["ContentDialogMinWidth"] = 860.0;

        var rootGrid = new Grid
        {
            Width = 920,
            MaxHeight = 720,
            RowSpacing = 12
        };
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: 頂部來源與儀表統計卡
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: 快捷操作列 & 搜尋過濾
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2: 比對主滾動區
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3: 底部安全防護宣告

        // ==========================================
        // 區塊 0: 來源資訊與四色儀表統計卡
        // ==========================================
        var headerPanel = new StackPanel { Spacing = 10 };
        Grid.SetRow(headerPanel, 0);

        var sourceText = new TextBlock
        {
            Text = $"🌐 比對來源：{_sourceBackupName}",
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        headerPanel.Children.Add(sourceText);

        var statsGrid = new Grid { ColumnSpacing = 10 };
        for (int i = 0; i < 4; i++) statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        statsGrid.Children.Add(CreateStatCard("💻 本機獨有", $"{_report.OnlyInLocalCount} 筆", "整併時將安全保留", ColorHelper.FromArgb(255, 0, 120, 215), 0));
        statsGrid.Children.Add(CreateStatCard("☁️ 外部新增", $"{_report.OnlyInRemoteCount} 筆", "將自動同步匯入", ColorHelper.FromArgb(255, 16, 137, 62), 1));
        statsGrid.Children.Add(CreateStatCard("⚪ 完全一致", $"{_report.IdenticalCount} 筆", "雙端內容相同無須異動", ColorHelper.FromArgb(255, 108, 117, 125), 2));
        statsGrid.Children.Add(CreateStatCard("⚠️ 內容衝突", $"{_report.ConflictedCount} 筆", _report.ConflictedCount > 0 ? "需由您決定保留方案" : "無衝突，可直通整併", _report.ConflictedCount > 0 ? ColorHelper.FromArgb(255, 216, 59, 1) : ColorHelper.FromArgb(255, 108, 117, 125), 3));

        headerPanel.Children.Add(statsGrid);
        rootGrid.Children.Add(headerPanel);

        // ==========================================
        // 區塊 1: 快捷操作列 & 即時搜尋過濾
        // ==========================================
        var toolBarGrid = new Grid { ColumnSpacing = 12 };
        Grid.SetRow(toolBarGrid, 1);
        toolBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });

        var actionButtonsStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        if (_report.ConflictedCount > 0)
        {
            var btnKeepNewer = new Button
            {
                Content = "⭐ 一鍵以較新時間為準 (推薦)",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"]
            };
            ToolTipService.SetToolTip(btnKeepNewer, "系統自動分析更新時間，各筆衝突皆採用最後編輯的最新版本");
            btnKeepNewer.Click += (_, _) => SetAllResolutions(MergeResolutionStrategy.KeepNewer);

            var btnKeepBoth = new Button
            {
                Content = "👥 全部兩側同時保留"
            };
            ToolTipService.SetToolTip(btnKeepBoth, "本機資料維持不變，外部修改自動另存為新名片 [同步複本]，確保 100% 零覆蓋");
            btnKeepBoth.Click += (_, _) => SetAllResolutions(MergeResolutionStrategy.KeepBoth);

            var btnKeepLocal = new Button { Content = "全部保留本機" };
            ToolTipService.SetToolTip(btnKeepLocal, "所有衝突均以目前本機電腦的名片內容為準");
            btnKeepLocal.Click += (_, _) => SetAllResolutions(MergeResolutionStrategy.KeepLocal);

            var btnKeepRemote = new Button { Content = "全部採納外部" };
            ToolTipService.SetToolTip(btnKeepRemote, "所有衝突均採納外部/雲端的修改覆寫本機");
            btnKeepRemote.Click += (_, _) => SetAllResolutions(MergeResolutionStrategy.KeepRemote);

            actionButtonsStack.Children.Add(btnKeepNewer);
            actionButtonsStack.Children.Add(btnKeepBoth);
            actionButtonsStack.Children.Add(btnKeepLocal);
            actionButtonsStack.Children.Add(btnKeepRemote);
        }
        else
        {
            actionButtonsStack.Children.Add(new TextBlock
            {
                Text = "✅ 本次同步無任何資料衝突，點擊右下角按鈕即可直接套用整併。",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 16, 137, 62)),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        Grid.SetColumn(actionButtonsStack, 0);
        toolBarGrid.Children.Add(actionButtonsStack);

        var filterBox = new TextBox
        {
            PlaceholderText = "🔍 快速過濾名片姓名/公司...",
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(filterBox, 1);
        toolBarGrid.Children.Add(filterBox);
        rootGrid.Children.Add(toolBarGrid);

        // ==========================================
        // 區塊 2: 比對主滾動區 (ScrollViewer)
        // ==========================================
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 4, 8, 4)
        };
        Grid.SetRow(scrollViewer, 2);

        var cardsContainer = new StackPanel { Spacing = 12 };

        if (_report.ConflictedCount > 0)
        {
            foreach (var item in _report.ConflictedItems)
            {
                cardsContainer.Children.Add(BuildProfessionalConflictCard(item));
            }
        }

        // 外部新增清單展開卡
        if (_report.OnlyInRemoteCount > 0)
        {
            cardsContainer.Children.Add(BuildAdditionExpander("☁️ 外部/雲端新增名片", _report.OnlyInRemoteCount, "這些名片在外部存在但在本機尚未建立，整併時將自動為您匯入本機庫", _report.OnlyInRemoteItems, isRemote: true));
        }

        // 本機獨有名片展開卡
        if (_report.OnlyInLocalCount > 0)
        {
            cardsContainer.Children.Add(BuildAdditionExpander("💻 本機現有名片", _report.OnlyInLocalCount, "這些名片僅存在於本機，整併時將安全完整保留", _report.OnlyInLocalItems, isRemote: false));
        }

        scrollViewer.Content = cardsContainer;
        rootGrid.Children.Add(scrollViewer);

        // 即時搜尋過濾掛接
        filterBox.TextChanged += (s, e) =>
        {
            string query = filterBox.Text.Trim();
            foreach (var b in _bindings)
            {
                bool match = string.IsNullOrEmpty(query) ||
                             b.Item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             b.Item.Company.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             b.Item.JobTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             b.Item.Phone.Contains(query, StringComparison.OrdinalIgnoreCase);
                b.CardBorder.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            }
        };

        // ==========================================
        // 區塊 3: 底部安全防護宣告
        // ==========================================
        var footerBorder = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(15, 0, 120, 215)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(40, 0, 120, 215)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8)
        };
        Grid.SetRow(footerBorder, 3);

        var footerStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        footerStack.Children.Add(new FontIcon { Glyph = "\uE72E", FontSize = 14, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 215)) });
        footerStack.Children.Add(new TextBlock
        {
            Text = "安全承諾：點擊確認後，系統將在寫入前自動於本地 backups/ 目錄建立快照，且全流程於原子化交易中執行，確保 100% 零資料遺失。",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        footerBorder.Child = footerStack;
        rootGrid.Children.Add(footerBorder);

        dialog.Content = rootGrid;

        var dialogResult = await dialog.ShowAsync();
        return dialogResult == ContentDialogResult.Primary;
    }

    private static Border CreateStatCard(string title, string count, string subtitle, Windows.UI.Color accentColor, int colIndex)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(18, accentColor.R, accentColor.G, accentColor.B)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(60, accentColor.R, accentColor.G, accentColor.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10)
        };
        Grid.SetColumn(card, colIndex);

        var stack = new StackPanel { Spacing = 2 };
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        Grid.SetColumn(titleText, 0);

        var countText = new TextBlock
        {
            Text = count,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(accentColor)
        };
        Grid.SetColumn(countText, 1);

        topRow.Children.Add(titleText);
        topRow.Children.Add(countText);
        stack.Children.Add(topRow);

        var subtitleText = new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Opacity = 0.85,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
        };
        stack.Children.Add(subtitleText);

        card.Child = stack;
        return card;
    }

    private FrameworkElement BuildProfessionalConflictCard(ContactDiffItem item)
    {
        var cardBorder = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14)
        };

        var mainStack = new StackPanel { Spacing = 10 };

        // 1. 名片卡片 Header
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerTitleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var nameText = new TextBlock
        {
            Text = item.DisplayName,
            FontSize = 16,
            FontWeight = FontWeights.Bold
        };
        headerTitleStack.Children.Add(nameText);

        if (!string.IsNullOrEmpty(item.Company))
        {
            headerTitleStack.Children.Add(new TextBlock
            {
                Text = item.Company,
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        if (!string.IsNullOrEmpty(item.JobTitle))
        {
            headerTitleStack.Children.Add(new TextBlock
            {
                Text = $"• {item.JobTitle}",
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        Grid.SetColumn(headerTitleStack, 0);

        // 時間差標記徽章
        var diffSummaryBadge = new Border
        {
            Background = item.IsRemoteNewer
                ? new SolidColorBrush(ColorHelper.FromArgb(28, 16, 137, 62))
                : new SolidColorBrush(ColorHelper.FromArgb(28, 0, 120, 215)),
            BorderBrush = item.IsRemoteNewer
                ? new SolidColorBrush(ColorHelper.FromArgb(120, 16, 137, 62))
                : new SolidColorBrush(ColorHelper.FromArgb(120, 0, 120, 215)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3)
        };
        diffSummaryBadge.Child = new TextBlock
        {
            Text = item.TimeDifferenceSummary,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = item.IsRemoteNewer
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 16, 137, 62))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 215))
        };
        Grid.SetColumn(diffSummaryBadge, 1);

        headerGrid.Children.Add(headerTitleStack);
        headerGrid.Children.Add(diffSummaryBadge);
        mainStack.Children.Add(headerGrid);

        // 2. 左右並列雙欄對照表 (Side-by-Side Diff Table)
        var comparisonGrid = new Grid { ColumnSpacing = 14 };
        comparisonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        comparisonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左欄：本機電腦版本
        var localColumnBorder = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(12, 0, 120, 215)),
            BorderBrush = !item.IsRemoteNewer ? new SolidColorBrush(ColorHelper.FromArgb(100, 0, 120, 215)) : new SolidColorBrush(ColorHelper.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Thickness(!item.IsRemoteNewer ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12)
        };
        var localColStack = new StackPanel { Spacing = 6 };
        var localHeaderRow = new Grid();
        localHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        localHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        localHeaderRow.Children.Add(new TextBlock { Text = "💻 本機版本", FontWeight = FontWeights.Bold, FontSize = 13, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 215)) });
        if (!item.IsRemoteNewer)
        {
            var localNewerTag = new Border
            {
                Background = new SolidColorBrush(ColorHelper.FromArgb(30, 0, 120, 215)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Child = new TextBlock { Text = "⭐ 本機較新", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 215)) }
            };
            Grid.SetColumn(localNewerTag, 1);
            localHeaderRow.Children.Add(localNewerTag);
        }
        localColStack.Children.Add(localHeaderRow);

        localColStack.Children.Add(new TextBlock
        {
            Text = $"📅 最後更新：{item.LocalTimeFormatted}",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        localColStack.Children.Add(new MenuFlyoutSeparator { Margin = new Thickness(0, 2, 0, 4) });

        // 右欄：外部雲端版本
        var remoteColumnBorder = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(14, 16, 137, 62)),
            BorderBrush = item.IsRemoteNewer ? new SolidColorBrush(ColorHelper.FromArgb(120, 16, 137, 62)) : new SolidColorBrush(ColorHelper.FromArgb(40, 128, 128, 128)),
            BorderThickness = new Thickness(item.IsRemoteNewer ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12)
        };
        var remoteColStack = new StackPanel { Spacing = 6 };
        var remoteHeaderRow = new Grid();
        remoteHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        remoteHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        remoteHeaderRow.Children.Add(new TextBlock { Text = "☁️ 外部/雲端版本", FontWeight = FontWeights.Bold, FontSize = 13, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 16, 137, 62)) });
        if (item.IsRemoteNewer)
        {
            var remoteNewerTag = new Border
            {
                Background = new SolidColorBrush(ColorHelper.FromArgb(35, 16, 137, 62)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Child = new TextBlock { Text = "🟢 外部較新 (推薦)", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 16, 137, 62)) }
            };
            Grid.SetColumn(remoteNewerTag, 1);
            remoteHeaderRow.Children.Add(remoteNewerTag);
        }
        remoteColStack.Children.Add(remoteHeaderRow);

        remoteColStack.Children.Add(new TextBlock
        {
            Text = $"📅 最後更新：{item.RemoteTimeFormatted}",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        remoteColStack.Children.Add(new MenuFlyoutSeparator { Margin = new Thickness(0, 2, 0, 4) });

        // 渲染詳細欄位比對項目
        foreach (var diff in item.Differences)
        {
            localColStack.Children.Add(CreateFieldRow(diff.FieldName, diff.LocalValue, isChanged: true));
            remoteColStack.Children.Add(CreateFieldRow(diff.FieldName, diff.RemoteValue, isChanged: true));
        }

        localColumnBorder.Child = localColStack;
        remoteColumnBorder.Child = remoteColStack;

        Grid.SetColumn(localColumnBorder, 0);
        Grid.SetColumn(remoteColumnBorder, 1);
        comparisonGrid.Children.Add(localColumnBorder);
        comparisonGrid.Children.Add(remoteColumnBorder);
        mainStack.Children.Add(comparisonGrid);

        // 3. 裁決控制選項列 (三態視覺卡)
        var decisionBorder = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(16, 128, 128, 128)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8)
        };

        var decisionGrid = new Grid();
        decisionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        decisionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        decisionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var decisionLabel = new TextBlock
        {
            Text = "您的裁決：",
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(decisionLabel, 0);

        var radioPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, VerticalAlignment = VerticalAlignment.Center };
        var group = "conflict_group_" + item.ContactId;
        var rbLocal = new RadioButton { GroupName = group, Content = "💻 保留本機" };
        var rbRemote = new RadioButton { GroupName = group, Content = "☁️ 採納外部" };
        var rbBoth = new RadioButton { GroupName = group, Content = "👥 兩側同時保留 (外部將另存新名片)" };

        radioPanel.Children.Add(rbLocal);
        radioPanel.Children.Add(rbRemote);
        radioPanel.Children.Add(rbBoth);
        Grid.SetColumn(radioPanel, 1);

        var decisionHintText = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(decisionHintText, 2);

        decisionGrid.Children.Add(decisionLabel);
        decisionGrid.Children.Add(radioPanel);
        decisionGrid.Children.Add(decisionHintText);
        decisionBorder.Child = decisionGrid;
        mainStack.Children.Add(decisionBorder);

        cardBorder.Child = mainStack;

        var binding = new ConflictCardBinding
        {
            Item = item,
            CardBorder = cardBorder,
            RbLocal = rbLocal,
            RbRemote = rbRemote,
            RbBoth = rbBoth,
            DecisionHintText = decisionHintText
        };
        _bindings.Add(binding);

        // 掛接按鈕選取互動與文字即時提示
        void RefreshCardState(MergeResolutionStrategy strategy)
        {
            item.Resolution = strategy;
            switch (strategy)
            {
                case MergeResolutionStrategy.KeepNewer:
                    if (item.IsRemoteNewer)
                    {
                        rbRemote.IsChecked = true;
                        decisionHintText.Text = "👉 依時間判定：採納較新的外部修改";
                    }
                    else
                    {
                        rbLocal.IsChecked = true;
                        decisionHintText.Text = "👉 依時間判定：保留較新的本機修改";
                    }
                    break;
                case MergeResolutionStrategy.KeepLocal:
                    rbLocal.IsChecked = true;
                    decisionHintText.Text = "👉 鎖定：維持本機現狀，忽略外部修改";
                    break;
                case MergeResolutionStrategy.KeepRemote:
                    rbRemote.IsChecked = true;
                    decisionHintText.Text = "👉 鎖定：以外部版本完全覆寫本機該名片";
                    break;
                case MergeResolutionStrategy.KeepBoth:
                    rbBoth.IsChecked = true;
                    decisionHintText.Text = "👉 零遺失：外部自動複製另存新名片 [同步複本]";
                    break;
            }
        }

        RefreshCardState(item.Resolution);

        rbLocal.Checked += (_, _) => RefreshCardState(MergeResolutionStrategy.KeepLocal);
        rbRemote.Checked += (_, _) => RefreshCardState(MergeResolutionStrategy.KeepRemote);
        rbBoth.Checked += (_, _) => RefreshCardState(MergeResolutionStrategy.KeepBoth);

        return cardBorder;
    }

    private static FrameworkElement CreateFieldRow(string fieldName, string value, bool isChanged)
    {
        var rowBorder = new Border
        {
            Background = isChanged ? new SolidColorBrush(ColorHelper.FromArgb(20, 255, 140, 0)) : null,
            BorderBrush = isChanged ? new SolidColorBrush(ColorHelper.FromArgb(70, 255, 140, 0)) : null,
            BorderThickness = new Thickness(isChanged ? 1 : 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 1, 0, 1)
        };

        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nameBlock = new TextBlock
        {
            Text = fieldName,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        Grid.SetColumn(nameBlock, 0);

        var valBlock = new TextBlock
        {
            Text = string.IsNullOrEmpty(value) ? "（無資料）" : value,
            FontSize = 12,
            FontWeight = isChanged ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = isChanged
                ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(valBlock, 1);

        rowGrid.Children.Add(nameBlock);
        rowGrid.Children.Add(valBlock);
        rowBorder.Child = rowGrid;
        return rowBorder;
    }

    private static FrameworkElement BuildAdditionExpander(string title, int count, string description, IEnumerable<ContactDiffItem> items, bool isRemote)
    {
        var expander = new Expander
        {
            Header = $"{title} ({count} 筆)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsExpanded = false
        };

        var contentStack = new StackPanel { Spacing = 6, Padding = new Thickness(8) };
        contentStack.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        var listBorder = new Border
        {
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10)
        };

        var itemsStack = new StackPanel { Spacing = 4 };
        foreach (var item in items.Take(50))
        {
            string timeStr = isRemote ? item.RemoteTimeFormatted : item.LocalTimeFormatted;
            itemsStack.Children.Add(new TextBlock
            {
                Text = $"• {item.DisplayName}  {(string.IsNullOrEmpty(item.Company) ? "" : $"({item.Company})")}  {(string.IsNullOrEmpty(item.Phone) ? "" : $"📱 {item.Phone}")}  -  更新於 {timeStr}",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }
        if (count > 50)
        {
            itemsStack.Children.Add(new TextBlock { Text = $"...以及其餘 {count - 50} 筆", FontSize = 11, Opacity = 0.6 });
        }
        listBorder.Child = itemsStack;
        contentStack.Children.Add(listBorder);

        expander.Content = contentStack;
        return expander;
    }

    private void SetAllResolutions(MergeResolutionStrategy strategy)
    {
        foreach (var b in _bindings)
        {
            b.Item.Resolution = strategy;
            switch (strategy)
            {
                case MergeResolutionStrategy.KeepNewer:
                    if (b.Item.IsRemoteNewer)
                    {
                        b.RbRemote.IsChecked = true;
                        b.DecisionHintText.Text = "👉 依時間判定：採納較新的外部修改";
                    }
                    else
                    {
                        b.RbLocal.IsChecked = true;
                        b.DecisionHintText.Text = "👉 依時間判定：保留較新的本機修改";
                    }
                    break;
                case MergeResolutionStrategy.KeepLocal:
                    b.RbLocal.IsChecked = true;
                    b.DecisionHintText.Text = "👉 鎖定：維持本機現狀，忽略外部修改";
                    break;
                case MergeResolutionStrategy.KeepRemote:
                    b.RbRemote.IsChecked = true;
                    b.DecisionHintText.Text = "👉 鎖定：以外部版本完全覆寫本機該名片";
                    break;
                case MergeResolutionStrategy.KeepBoth:
                    b.RbBoth.IsChecked = true;
                    b.DecisionHintText.Text = "👉 零遺失：外部自動複製另存新名片 [同步複本]";
                    break;
            }
        }
    }
}
