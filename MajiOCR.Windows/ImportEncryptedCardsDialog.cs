using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows;

public class ImportEncryptedCardsDialog
{
    private readonly XamlRoot _xamlRoot;
    private readonly ICardSharingCryptoService _cryptoService;
    private readonly IContactExcelCsvService _excelCsvService;
    private readonly IStorageService _storageService;
    private readonly Func<Task> _onDataRefreshed;

    public ImportEncryptedCardsDialog(
        XamlRoot xamlRoot,
        ICardSharingCryptoService cryptoService,
        IContactExcelCsvService excelCsvService,
        IStorageService storageService,
        Func<Task> onDataRefreshed)
    {
        _xamlRoot = xamlRoot;
        _cryptoService = cryptoService;
        _excelCsvService = excelCsvService;
        _storageService = storageService;
        _onDataRefreshed = onDataRefreshed;
    }

    public async Task ShowAsync(IntPtr hwnd)
    {
        // 1. Pick *.majicard file
        var openPicker = new FileOpenPicker();
        if (hwnd != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hwnd);
        }
        openPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        openPicker.FileTypeFilter.Add(".majicard");

        var file = await openPicker.PickSingleFileAsync();
        if (file == null) return;

        byte[] packageBytes;
        using (var stream = await file.OpenStreamForReadAsync())
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            packageBytes = ms.ToArray();
        }

        // 2. Ask Password
        string? password = await PromptPasswordAsync(file.Name);
        if (password == null) return; // User cancelled

        // 3. Decrypt
        CardSharingPackage pkg;
        try
        {
            pkg = _cryptoService.DecryptPackage(packageBytes, password);
        }
        catch (Exception ex)
        {
            await ShowNoticeAsync("解密失敗", ex.Message);
            return;
        }

        if (pkg.Cards.Count == 0)
        {
            await ShowNoticeAsync("分享包為空", "此加密分享包內沒有任何名片資料。");
            return;
        }

        // 4. Show Card Selection & Import Dialog
        await ShowSelectionAndImportDialogAsync(pkg, file.Name);
    }

    private async Task<string?> PromptPasswordAsync(string fileName)
    {
        string? password = null;
        var dialog = new ContentDialog
        {
            XamlRoot = _xamlRoot,
            Title = "🔐 解密名片分享包",
            PrimaryButtonText = "解密開啟",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var sp = new StackPanel { Spacing = 10, MaxWidth = 380 };
        sp.Children.Add(new TextBlock
        {
            Text = $"檔案：{fileName}\n請輸入傳送者設定的分享保護密碼：",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });

        var pwdBox = new PasswordBox { PlaceholderText = "請輸入解密密碼" };
        sp.Children.Add(pwdBox);

        var errorText = new TextBlock
        {
            Text = "密碼不能為空。",
            FontSize = 11,
            Foreground = new SolidColorBrush(Colors.Red),
            Visibility = Visibility.Collapsed
        };
        sp.Children.Add(errorText);

        dialog.Content = sp;
        dialog.PrimaryButtonClick += (s, e) =>
        {
            if (string.IsNullOrWhiteSpace(pwdBox.Password))
            {
                errorText.Visibility = Visibility.Visible;
                e.Cancel = true;
            }
            else
            {
                password = pwdBox.Password;
            }
        };

        var res = await dialog.ShowAsync();
        return res == ContentDialogResult.Primary ? password : null;
    }

    private async Task ShowSelectionAndImportDialogAsync(CardSharingPackage pkg, string fileName)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _xamlRoot,
            Title = $"📥 匯入加密分享包名片 (共 {pkg.Cards.Count} 筆)",
            PrimaryButtonText = "匯入勾選的名片",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var rootStack = new StackPanel { Spacing = 12, MaxWidth = 540 };

        // Top info & Strategy
        var infoCard = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10)
        };
        var infoStack = new StackPanel { Spacing = 6 };
        infoStack.Children.Add(new TextBlock
        {
            Text = $"來源檔案：{fileName}（匯出時間：{pkg.ExportedAt:yyyy-MM-dd HH:mm}）",
            FontSize = 12,
            Opacity = 0.8
        });

        var stratRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        stratRow.Children.Add(new TextBlock { Text = "重複處理策略：", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        var stratCombo = new ComboBox
        {
            ItemsSource = new[] { "略過重複名片 (推薦)", "覆蓋更新現有名片", "一律新增為獨立名片" },
            SelectedIndex = 0,
            FontSize = 12
        };
        stratRow.Children.Add(stratCombo);
        infoStack.Children.Add(stratRow);
        infoCard.Child = infoStack;
        rootStack.Children.Add(infoCard);

        // Selection header: Select All / None & count
        var selHeader = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        selHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var chkSelectAll = new CheckBox
        {
            Content = "全選所有名片",
            IsChecked = true,
            FontSize = 12
        };
        Grid.SetColumn(chkSelectAll, 0);
        selHeader.Children.Add(chkSelectAll);

        var selCountText = new TextBlock
        {
            Text = $"已勾選 {pkg.Cards.Count} / {pkg.Cards.Count} 筆",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(selCountText, 1);
        selHeader.Children.Add(selCountText);
        rootStack.Children.Add(selHeader);

        // Card items container (Scrollable list)
        var listStack = new StackPanel { Spacing = 6 };

        void UpdateCount()
        {
            int c = pkg.Cards.Count(x => x.IsSelected);
            selCountText.Text = $"已勾選 {c} / {pkg.Cards.Count} 筆";
            dialog.IsPrimaryButtonEnabled = c > 0;
        }

        foreach (var card in pkg.Cards)
        {
            card.IsSelected = true;

            var itemBorder = new Border
            {
                Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6)
            };

            var itemGrid = new Grid();
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var itemChk = new CheckBox
            {
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            itemChk.Checked += (s, e) => { card.IsSelected = true; UpdateCount(); };
            itemChk.Unchecked += (s, e) => { card.IsSelected = false; UpdateCount(); };
            Grid.SetColumn(itemChk, 0);
            itemGrid.Children.Add(itemChk);

            var cardInfoStack = new StackPanel { Spacing = 2 };
            cardInfoStack.Children.Add(new TextBlock
            {
                Text = $"{card.DisplayTitle}  |  {card.Title}  |  {card.DisplaySubtitle}",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12
            });

            string phoneEmail = $"電話: {card.DisplayPhone}   Email: {card.Email}";
            cardInfoStack.Children.Add(new TextBlock
            {
                Text = phoneEmail,
                FontSize = 11,
                Opacity = 0.75
            });

            if (card.Tags.Count > 0)
            {
                cardInfoStack.Children.Add(new TextBlock
                {
                    Text = $"標籤: {string.Join("、", card.Tags)}",
                    FontSize = 10,
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                });
            }

            Grid.SetColumn(cardInfoStack, 1);
            itemGrid.Children.Add(cardInfoStack);

            itemBorder.Child = itemGrid;
            listStack.Children.Add(itemBorder);
        }

        chkSelectAll.Checked += (s, e) =>
        {
            foreach (var child in listStack.Children)
            {
                if (child is Border b && b.Child is Grid g && g.Children[0] is CheckBox cb)
                    cb.IsChecked = true;
            }
            foreach (var card in pkg.Cards) card.IsSelected = true;
            UpdateCount();
        };

        chkSelectAll.Unchecked += (s, e) =>
        {
            foreach (var child in listStack.Children)
            {
                if (child is Border b && b.Child is Grid g && g.Children[0] is CheckBox cb)
                    cb.IsChecked = false;
            }
            foreach (var card in pkg.Cards) card.IsSelected = false;
            UpdateCount();
        };

        var scroll = new ScrollViewer { Content = listStack, MaxHeight = 320 };
        rootStack.Children.Add(scroll);

        var progressRing = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        rootStack.Children.Add(progressRing);

        dialog.Content = rootStack;

        dialog.PrimaryButtonClick += async (s, e) =>
        {
            var selectedCards = pkg.Cards.Where(x => x.IsSelected).ToList();
            if (selectedCards.Count == 0) return;

            var deferral = e.GetDeferral();
            try
            {
                dialog.IsPrimaryButtonEnabled = false;
                progressRing.Visibility = Visibility.Visible;

                var strategy = stratCombo.SelectedIndex switch
                {
                    1 => ImportDuplicateStrategy.OverwriteExisting,
                    2 => ImportDuplicateStrategy.AlwaysAdd,
                    _ => ImportDuplicateStrategy.SkipDuplicate
                };

                // Convert to ImportContactDraft
                var drafts = selectedCards.Select(c => new ImportContactDraft
                {
                    Name = c.Name,
                    Company = c.Company,
                    Department = c.Department,
                    Title = c.Title,
                    Mobile = c.Mobile,
                    Phone = c.Phone,
                    Email = c.Email,
                    Address = c.Address,
                    TaxId = c.TaxId,
                    Website = c.Website,
                    Tags = c.Tags,
                    Notes = c.Notes
                }).ToList();

                var importResult = await _excelCsvService.ExecuteImportAsync(drafts, strategy, _storageService);

                progressRing.Visibility = Visibility.Collapsed;

                if (_onDataRefreshed != null)
                {
                    await _onDataRefreshed();
                }

                await ShowNoticeAsync(
                    importResult.Succeeded ? "🎉 名片匯入成功" : "⚠️ 名片匯入完成 (部分失敗)",
                    $"已成功處理 {selectedCards.Count} 筆加密分享名片：\n" + importResult.SummaryMessage);
            }
            catch (Exception ex)
            {
                progressRing.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = true;
                await ShowNoticeAsync("匯入失敗", $"執行匯入時發生錯誤：{ex.Message}");
                e.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    private async Task ShowNoticeAsync(string title, string message)
    {
        var notice = new ContentDialog
        {
            XamlRoot = _xamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 },
            CloseButtonText = "確定"
        };
        await notice.ShowAsync();
    }
}
