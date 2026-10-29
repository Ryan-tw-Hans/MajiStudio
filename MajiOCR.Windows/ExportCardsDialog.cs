using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MajiOCR_Windows;

public enum ExportCardsFormat
{
    Excel,
    Csv,
    EncryptedPackage
}

public enum ExportCardsScope
{
    SelectedOnly,
    AllFiltered
}

public class ExportCardsOptions
{
    public bool IsConfirmed { get; set; }
    public ExportCardsScope Scope { get; set; } = ExportCardsScope.SelectedOnly;
    public ExportCardsFormat Format { get; set; } = ExportCardsFormat.Excel;
    public bool IncludeTags { get; set; } = true;
    public bool IncludeNotes { get; set; } = true;
    public string Password { get; set; } = string.Empty;
}

public class ExportCardsDialog
{
    private readonly XamlRoot _xamlRoot;
    private readonly int _selectedCount;
    private readonly int _totalFilteredCount;
    private readonly ExportCardsFormat _initialFormat;

    public ExportCardsDialog(
        XamlRoot xamlRoot,
        int selectedCount,
        int totalFilteredCount,
        ExportCardsFormat initialFormat = ExportCardsFormat.Excel)
    {
        _xamlRoot = xamlRoot;
        _selectedCount = selectedCount;
        _totalFilteredCount = totalFilteredCount;
        _initialFormat = initialFormat;
    }

    public async Task<ExportCardsOptions?> ShowAsync()
    {
        var options = new ExportCardsOptions();

        var dialog = new ContentDialog
        {
            XamlRoot = _xamlRoot,
            Title = "📤 匯出名片資料設定",
            PrimaryButtonText = "開始匯出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var rootStack = new StackPanel { Spacing = 14, MaxWidth = 460 };

        // 1. 匯出範圍
        rootStack.Children.Add(new TextBlock
        {
            Text = "1. 選擇匯出範圍",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13
        });

        var scopeStack = new StackPanel { Spacing = 6, Margin = new Thickness(12, 0, 0, 0) };
        var rbSelected = new RadioButton
        {
            Content = $"僅匯出勾選的名片 ({_selectedCount} 筆)",
            IsChecked = _selectedCount > 0,
            IsEnabled = _selectedCount > 0
        };
        var rbAll = new RadioButton
        {
            Content = $"匯出目前篩選清單全部 ({_totalFilteredCount} 筆)",
            IsChecked = _selectedCount == 0
        };
        scopeStack.Children.Add(rbSelected);
        scopeStack.Children.Add(rbAll);
        rootStack.Children.Add(scopeStack);

        // 2. 欄位包含設定
        rootStack.Children.Add(new TextBlock
        {
            Text = "2. 包含欄位選項",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13
        });

        var fieldsStack = new StackPanel { Spacing = 6, Margin = new Thickness(12, 0, 0, 0) };
        var chkTags = new CheckBox
        {
            Content = "包含分類標籤 (若取消勾選則排除標籤)",
            IsChecked = true
        };
        var chkNotes = new CheckBox
        {
            Content = "包含名片備註 (若取消勾選則排除備註)",
            IsChecked = true
        };
        fieldsStack.Children.Add(chkTags);
        fieldsStack.Children.Add(chkNotes);
        rootStack.Children.Add(fieldsStack);

        // 3. 匯出格式
        rootStack.Children.Add(new TextBlock
        {
            Text = "3. 選擇匯出格式",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13
        });

        var formatStack = new StackPanel { Spacing = 6, Margin = new Thickness(12, 0, 0, 0) };
        var rbExcel = new RadioButton
        {
            Content = "📊 Excel 工作表 (*.xlsx)",
            IsChecked = _initialFormat == ExportCardsFormat.Excel
        };
        var rbCsv = new RadioButton
        {
            Content = "📁 CSV 檔案 (*.csv)",
            IsChecked = _initialFormat == ExportCardsFormat.Csv
        };
        var rbEncrypted = new RadioButton
        {
            Content = "🔐 MajiOCR 專用加密名片分享包 (*.majicard)",
            IsChecked = _initialFormat == ExportCardsFormat.EncryptedPackage
        };
        formatStack.Children.Add(rbExcel);
        formatStack.Children.Add(rbCsv);
        formatStack.Children.Add(rbEncrypted);
        rootStack.Children.Add(formatStack);

        // 4. 密碼保護區塊 (僅在加密包時顯示)
        var cryptoPanel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(12, 4, 0, 0),
            Visibility = _initialFormat == ExportCardsFormat.EncryptedPackage ? Visibility.Visible : Visibility.Collapsed
        };

        var cryptoInfo = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = "AES-256 加密保護",
            Message = "此分享包將包含名片各欄位與圖片。接收者需輸入相同密碼才能開啟並勾選匯入。"
        };
        cryptoPanel.Children.Add(cryptoInfo);

        var pwdBox = new PasswordBox
        {
            Header = "設定分享保護密碼 (必填)",
            PlaceholderText = "請輸入保護密碼"
        };
        cryptoPanel.Children.Add(pwdBox);

        var pwdConfirmBox = new PasswordBox
        {
            Header = "再次確認密碼",
            PlaceholderText = "請再次輸入相同密碼"
        };
        cryptoPanel.Children.Add(pwdConfirmBox);

        var pwdErrorText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
            Visibility = Visibility.Collapsed
        };
        cryptoPanel.Children.Add(pwdErrorText);

        rootStack.Children.Add(cryptoPanel);

        // Event: Format changed toggles crypto panel
        rbExcel.Checked += (s, e) => cryptoPanel.Visibility = Visibility.Collapsed;
        rbCsv.Checked += (s, e) => cryptoPanel.Visibility = Visibility.Collapsed;
        rbEncrypted.Checked += (s, e) => cryptoPanel.Visibility = Visibility.Visible;

        dialog.Content = new ScrollViewer { Content = rootStack, MaxHeight = 480 };

        // Validation on PrimaryButtonClick
        dialog.PrimaryButtonClick += (s, e) =>
        {
            if (rbEncrypted.IsChecked == true)
            {
                string p1 = pwdBox.Password;
                string p2 = pwdConfirmBox.Password;

                if (string.IsNullOrWhiteSpace(p1))
                {
                    pwdErrorText.Text = "請輸入分享保護密碼。";
                    pwdErrorText.Visibility = Visibility.Visible;
                    e.Cancel = true;
                    return;
                }

                if (p1 != p2)
                {
                    pwdErrorText.Text = "兩次輸入的密碼不一致，請重新確認。";
                    pwdErrorText.Visibility = Visibility.Visible;
                    e.Cancel = true;
                    return;
                }

                options.Password = p1;
            }

            options.IsConfirmed = true;
            options.Scope = (rbSelected.IsChecked == true) ? ExportCardsScope.SelectedOnly : ExportCardsScope.AllFiltered;
            if (rbExcel.IsChecked == true) options.Format = ExportCardsFormat.Excel;
            else if (rbCsv.IsChecked == true) options.Format = ExportCardsFormat.Csv;
            else options.Format = ExportCardsFormat.EncryptedPackage;

            options.IncludeTags = chkTags.IsChecked == true;
            options.IncludeNotes = chkNotes.IsChecked == true;
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && options.IsConfirmed)
        {
            return options;
        }

        return null;
    }
}
