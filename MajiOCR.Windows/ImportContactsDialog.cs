using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows;

/// <summary>
/// 批次匯入名片資料對話框 (WinUI 3)
/// 符合 RS-ISSUE-0054 與 DP-ISSUE-0054 規格：支援 Excel (*.xlsx) 與通用 CSV (*.csv) 智慧解析與重複裁決
/// </summary>
public class ImportContactsDialog
{
    private readonly XamlRoot _xamlRoot;
    private readonly IContactExcelCsvService _excelCsvService;
    private readonly IStorageService _storageService;
    private readonly Func<Task> _onDataRefreshed;

    private IReadOnlyList<ImportContactDraft> _parsedDrafts = Array.Empty<ImportContactDraft>();
    private string? _selectedFilePath;

    public ImportContactsDialog(
        XamlRoot xamlRoot,
        IContactExcelCsvService excelCsvService,
        IStorageService storageService,
        Func<Task> onDataRefreshed)
    {
        _xamlRoot = xamlRoot;
        _excelCsvService = excelCsvService;
        _storageService = storageService;
        _onDataRefreshed = onDataRefreshed;
    }

    public async Task ShowAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _xamlRoot,
            Title = "📥 批次匯入名片資料 (Excel / CSV)",
            PrimaryButtonText = "🚀 開始執行匯入",
            CloseButtonText = "關閉",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };

        dialog.Resources["ContentDialogMaxWidth"] = 800.0;
        dialog.Resources["ContentDialogMinWidth"] = 680.0;
        dialog.MaxWidth = 800.0;

        var rootStack = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 720 };

        // 1. 說明文字
        var introInfo = new InfoBar
        {
            IsOpen = true,
            Severity = InfoBarSeverity.Informational,
            IsClosable = false,
            Title = "支援 Excel (*.xlsx) 與通用 CSV (*.csv) 格式",
            Message = "系統具備智慧標題識別能力，能自動識別繁中、簡中與英文常見欄位名稱（如姓名、公司、職稱、電話、Email、地址、標籤等）。"
        };
        rootStack.Children.Add(introInfo);

        // 2. 範本下載卡片
        var templateCard = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12)
        };
        var templateStack = new StackPanel { Spacing = 8 };
        templateStack.Children.Add(new TextBlock
        {
            Text = "📄 標準名片表單範本：",
            FontWeight = FontWeights.Bold,
            FontSize = 13
        });
        templateStack.Children.Add(new TextBlock
        {
            Text = "若尚未建立名片表格，可先下載標準範本填寫後再行匯入：",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        var templateBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 4, 0, 0) };

        var downloadExcelBtn = new Button { Content = "📊 下載標準 Excel 範本 (*.xlsx)" };
        downloadExcelBtn.Click += async (s, e) => await OnDownloadTemplateClickedAsync(isExcel: true);
        templateBtnRow.Children.Add(downloadExcelBtn);

        var downloadCsvBtn = new Button { Content = "📁 下載標準 CSV 範本 (*.csv)" };
        downloadCsvBtn.Click += async (s, e) => await OnDownloadTemplateClickedAsync(isExcel: false);
        templateBtnRow.Children.Add(downloadCsvBtn);

        templateStack.Children.Add(templateBtnRow);
        templateCard.Child = templateStack;
        rootStack.Children.Add(templateCard);

        // 3. 檔案選取卡片
        var fileCard = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12)
        };
        var fileStack = new StackPanel { Spacing = 8 };
        fileStack.Children.Add(new TextBlock
        {
            Text = "📂 選取要匯入的名片檔案：",
            FontWeight = FontWeights.Bold,
            FontSize = 13
        });

        var fileActionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var pickFileBtn = new Button
        {
            Content = "🔍 瀏覽選取檔案...",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        fileActionRow.Children.Add(pickFileBtn);

        var fileStatusText = new TextBlock
        {
            Text = "尚未選取檔案 (支援 *.xlsx, *.csv)",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        };
        fileActionRow.Children.Add(fileStatusText);
        fileStack.Children.Add(fileActionRow);

        fileCard.Child = fileStack;
        rootStack.Children.Add(fileCard);

        // 4. 重複資料裁決卡片
        var strategyCard = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12)
        };
        var strategyStack = new StackPanel { Spacing = 8 };
        strategyStack.Children.Add(new TextBlock
        {
            Text = "⚙️ 重複名片處理策略 (判定標準：同姓名且同公司或同電話)：",
            FontWeight = FontWeights.Bold,
            FontSize = 13
        });

        var rbSkip = new RadioButton
        {
            GroupName = "DuplicateStrategy",
            Content = "⏩ 跳過重複名片（若系統已有重複記錄則略過不匯入）- 推薦",
            IsChecked = true
        };
        var rbOverwrite = new RadioButton
        {
            GroupName = "DuplicateStrategy",
            Content = "🔄 覆蓋更新現有（以匯入資料更新系統既有名片，補齊未填欄位與標籤）"
        };
        var rbAlwaysAdd = new RadioButton
        {
            GroupName = "DuplicateStrategy",
            Content = "➕ 一律建立為新名片（即使重複亦建立獨立名片記錄）"
        };

        strategyStack.Children.Add(rbSkip);
        strategyStack.Children.Add(rbOverwrite);
        strategyStack.Children.Add(rbAlwaysAdd);
        strategyCard.Child = strategyStack;
        rootStack.Children.Add(strategyCard);

        // 5. 資料預覽區塊
        var previewCard = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Visibility = Visibility.Collapsed
        };
        var previewStack = new StackPanel { Spacing = 8 };
        var previewTitle = new TextBlock
        {
            Text = "👀 名片資料預覽 (前 5 筆)：",
            FontWeight = FontWeights.Bold,
            FontSize = 13
        };
        previewStack.Children.Add(previewTitle);

        var previewItemsStack = new StackPanel { Spacing = 6 };
        previewStack.Children.Add(previewItemsStack);
        previewCard.Child = previewStack;
        rootStack.Children.Add(previewCard);

        // 6. 進度指示器
        var progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        rootStack.Children.Add(progressBar);

        dialog.Content = new ScrollViewer
        {
            Content = rootStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 600
        };

        // 檔案選取點擊事件
        pickFileBtn.Click += async (s, e) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                IntPtr hwnd = GetParentWindowHandle();
                if (hwnd != IntPtr.Zero)
                {
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                }

                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add(".xlsx");
                picker.FileTypeFilter.Add(".csv");

                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                _selectedFilePath = file.Path;
                string ext = Path.GetExtension(file.Path);

                using (var stream = await file.OpenStreamForReadAsync())
                {
                    _parsedDrafts = _excelCsvService.ParseImportFile(stream, ext);
                }

                if (_parsedDrafts.Count == 0)
                {
                    fileStatusText.Text = $"⚠️ 已選取：{file.Name}，但未解析出任何有效名片資料，請確認標題列是否包含「姓名」或「公司」。";
                    fileStatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
                    dialog.IsPrimaryButtonEnabled = false;
                    previewCard.Visibility = Visibility.Collapsed;
                    return;
                }

                fileStatusText.Text = $"✅ 已選取：{file.Name} (成功解析 {_parsedDrafts.Count} 筆名片資料)";
                fileStatusText.Foreground = new SolidColorBrush(Colors.ForestGreen);
                dialog.IsPrimaryButtonEnabled = true;

                // 渲染前 5 筆預覽
                previewItemsStack.Children.Clear();
                int previewLimit = Math.Min(5, _parsedDrafts.Count);
                previewTitle.Text = $"👀 名片資料預覽 (前 {previewLimit} 筆，共 {_parsedDrafts.Count} 筆)：";

                for (int i = 0; i < previewLimit; i++)
                {
                    var d = _parsedDrafts[i];
                    var rowBorder = new Border
                    {
                        Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8, 6, 8, 6)
                    };
                    var rowStack = new StackPanel { Spacing = 2 };
                    rowStack.Children.Add(new TextBlock
                    {
                        Text = $"#{i + 1}  {d.DisplayTitle}  |  {d.Title}  |  {d.DisplaySubtitle}",
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 12
                    });
                    string contactInfo = $"電話: {d.DisplayPhone}  |  Email: {d.Email}  |  標籤: {d.DisplayTags}";
                    rowStack.Children.Add(new TextBlock
                    {
                        Text = contactInfo,
                        FontSize = 11,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    });
                    rowBorder.Child = rowStack;
                    previewItemsStack.Children.Add(rowBorder);
                }
                previewCard.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                fileStatusText.Text = $"❌ 讀取檔案失敗：{ex.Message}";
                fileStatusText.Foreground = new SolidColorBrush(Colors.Red);
                dialog.IsPrimaryButtonEnabled = false;
                previewCard.Visibility = Visibility.Collapsed;
            }
        };

        // 執行匯入
        dialog.PrimaryButtonClick += async (s, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                if (_parsedDrafts.Count == 0) return;

                dialog.IsPrimaryButtonEnabled = false;
                progressBar.Visibility = Visibility.Visible;

                var strategy = rbSkip.IsChecked == true
                    ? ImportDuplicateStrategy.SkipDuplicate
                    : (rbOverwrite.IsChecked == true ? ImportDuplicateStrategy.OverwriteExisting : ImportDuplicateStrategy.AlwaysAdd);

                var importResult = await _excelCsvService.ExecuteImportAsync(
                    _parsedDrafts,
                    strategy,
                    _storageService);

                progressBar.Visibility = Visibility.Collapsed;

                if (_onDataRefreshed != null)
                {
                    await _onDataRefreshed();
                }

                await ShowNoticeDialogAsync(
                    importResult.Succeeded ? "🎉 名片匯入成功" : "⚠️ 名片匯入完成 (部分失敗)",
                    importResult.SummaryMessage +
                    (importResult.Errors.Count > 0 ? "\n\n錯誤詳情：\n" + string.Join("\n", importResult.Errors.Take(5)) : ""));
            }
            catch (Exception ex)
            {
                progressBar.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = true;
                await ShowNoticeDialogAsync("匯入失敗", $"執行匯入時發生非預期錯誤：{ex.Message}");
                e.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    private async Task OnDownloadTemplateClickedAsync(bool isExcel)
    {
        try
        {
            var savePicker = new FileSavePicker();
            IntPtr hwnd = GetParentWindowHandle();
            if (hwnd != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
            }

            savePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            if (isExcel)
            {
                savePicker.FileTypeChoices.Add("Excel 活頁簿 (*.xlsx)", new List<string> { ".xlsx" });
                savePicker.SuggestedFileName = "MajiOCR_名片匯入標準範本.xlsx";
            }
            else
            {
                savePicker.FileTypeChoices.Add("CSV 檔案 (*.csv)", new List<string> { ".csv" });
                savePicker.SuggestedFileName = "MajiOCR_名片匯入標準範本.csv";
            }

            var file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            if (isExcel)
            {
                byte[] bytes = _excelCsvService.GenerateExcelTemplate();
                using var stream = await file.OpenStreamForWriteAsync();
                stream.SetLength(0);
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }
            else
            {
                string csv = _excelCsvService.GenerateCsvTemplate();
                byte[] bytes = Encoding.UTF8.GetBytes(csv);
                using var stream = await file.OpenStreamForWriteAsync();
                stream.SetLength(0);
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }

            await ShowNoticeDialogAsync("範本下載成功", $"標準名片範本已成功儲存至：\n{file.Path}\n\n您可直接使用 Excel 開啟編輯填寫，再返回本視窗進行匯入。");
        }
        catch (Exception ex)
        {
            await ShowNoticeDialogAsync("下載範本失敗", $"發生錯誤：{ex.Message}");
        }
    }

    private async Task ShowNoticeDialogAsync(string title, string content)
    {
        var notice = new ContentDialog
        {
            XamlRoot = _xamlRoot,
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "確定",
            DefaultButton = ContentDialogButton.Close
        };
        await notice.ShowAsync();
    }

    private static IntPtr GetParentWindowHandle()
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
}
