using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

public enum DatabaseSyncDirection
{
    Bidirectional,  // 雙向同步 (比對整併 + 雙向同步更新)
    PullToLocal,    // 僅自外部/雲端同步回本機
    PushToTarget    // 僅自本機同步推至外部/雲端
}

public enum DatabaseSyncMode
{
    LocalFile,      // 本機檔案/雙資料庫同步
    CloudDrive      // Google Drive 雲端同步
}

/// <summary>
/// 名片資料庫同步中心對話框 (WinUI 3)
/// 符合 RS-ISSUE-0053 規格：支援本機端雙資料庫/備份檔同步整併與 Google Drive 雲端同步
/// </summary>
public class DatabaseSyncDialog
{
    private readonly XamlRoot _xamlRoot;
    private readonly IGoogleAuthService _authService;
    private readonly IGoogleDriveBackupService _driveBackupService;
    private readonly IDatabaseMergeService _mergeService;
    private readonly IStorageService _storageService;
    private readonly IAppSettingsService _settingsService;
    private readonly Func<Task> _onDataRefreshed;

    public DatabaseSyncDialog(
        XamlRoot xamlRoot,
        IGoogleAuthService authService,
        IGoogleDriveBackupService driveBackupService,
        IDatabaseMergeService mergeService,
        IStorageService storageService,
        IAppSettingsService settingsService,
        Func<Task> onDataRefreshed)
    {
        _xamlRoot = xamlRoot;
        _authService = authService;
        _driveBackupService = driveBackupService;
        _mergeService = mergeService;
        _storageService = storageService;
        _settingsService = settingsService;
        _onDataRefreshed = onDataRefreshed;
    }

    public async Task ShowAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _xamlRoot,
            Title = "🔄 名片資料庫同步中心 (Database Sync Center)",
            PrimaryButtonText = "🚀 開始執行同步",
            CloseButtonText = "關閉",
            DefaultButton = ContentDialogButton.Primary
        };

        // 解除 WinUI 3 ContentDialog 預設 548px 寬度上限，避免右側內容與按鈕被裁切跑版
        dialog.Resources["ContentDialogMaxWidth"] = 840.0;
        dialog.Resources["ContentDialogMinWidth"] = 680.0;
        dialog.MaxWidth = 840.0;

        var rootStack = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 740 };

        // 1. 說明文字
        var introText = new TextBlock
        {
            Text = "跨裝置名片資料庫同步中心：支援本機端雙資料庫/備份檔智慧整併，或透過 Google Drive 進行雲端同步，具備更新時間比對與雙向裁決保護。",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        rootStack.Children.Add(introText);

        // 2. 同步管道切換器 (Mode Selector)
        var modeCard = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12)
        };

        var modeStack = new StackPanel { Spacing = 8 };
        modeStack.Children.Add(new TextBlock
        {
            Text = "🌐 請選擇同步管道：",
            FontWeight = FontWeights.Bold,
            FontSize = 13
        });

        var rbModeLocal = CreateWrappingRadioButton(
            "SyncModeGroup",
            "📁 本地端檔案同步 (推薦)",
            "支援選取另一個 SQLite 資料庫檔 (*.sqlite3, *.db) 或 ZIP 備份檔進行整併",
            isChecked: true);

        var rbModeCloud = CreateWrappingRadioButton(
            "SyncModeGroup",
            "☁️ Google Drive 雲端同步",
            "透過 Google 帳號備份進行跨裝置資料庫整併與備份推播");

        modeStack.Children.Add(rbModeLocal);
        modeStack.Children.Add(rbModeCloud);
        modeCard.Child = modeStack;
        rootStack.Children.Add(modeCard);

        // ==========================================
        // 3. 本地端檔案同步區塊 (Local File Section)
        // ==========================================
        var localSection = new StackPanel { Spacing = 10 };

        // 3.1 目前本機資料庫資訊卡
        string storageRoot = StoragePathHelper.GetDefaultStorageRoot();
        string currentDbPath = Path.Combine(storageRoot, StoragePathHelper.DatabaseFileName);
        int currentCardCount = 0;
        try
        {
            var paged = await _storageService.GetContactsPagedAsync(null, 0, 1);
            currentCardCount = paged.TotalCount;
        }
        catch { }

        var currentDbCard = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(15, 0, 120, 215)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(50, 0, 120, 215)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10)
        };
        var currentDbStack = new StackPanel { Spacing = 4 };
        currentDbStack.Children.Add(new TextBlock
        {
            Text = $"🏠 基準本機庫：{currentDbPath}",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        currentDbStack.Children.Add(new TextBlock
        {
            Text = $"目前名片筆數：{currentCardCount} 筆",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        currentDbCard.Child = currentDbStack;
        localSection.Children.Add(currentDbCard);

        // 3.2 選取目標外部檔案
        var targetFileCard = new Border
        {
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12)
        };
        var targetFileStack = new StackPanel { Spacing = 8 };
        targetFileStack.Children.Add(new TextBlock
        {
            Text = "🎯 目標外部檔案 (*.sqlite3, *.db, *.zip)：",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });

        var filePickerGrid = new Grid();
        filePickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filePickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        filePickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var targetPathBox = new TextBox
        {
            PlaceholderText = "請點擊右側按鈕選取另一個資料庫或備份檔 (如隨身碟、網路資料夾或同步資料夾)",
            IsReadOnly = true,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(targetPathBox, 0);

        var pickFileBtn = new Button
        {
            Content = "📂 瀏覽選取檔案...",
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(pickFileBtn, 2);

        filePickerGrid.Children.Add(targetPathBox);
        filePickerGrid.Children.Add(pickFileBtn);
        targetFileStack.Children.Add(filePickerGrid);

        var fileInfoText = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = new SolidColorBrush(Colors.Green),
            Visibility = Visibility.Collapsed
        };
        targetFileStack.Children.Add(fileInfoText);
        targetFileCard.Child = targetFileStack;
        localSection.Children.Add(targetFileCard);

        // 3.3 本機同步方向
        var localDirCard = new Border
        {
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12)
        };
        var localDirStack = new StackPanel { Spacing = 8 };
        localDirStack.Children.Add(new TextBlock
        {
            Text = "🧭 本地同步方向模式：",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });

        var rbLocalBi = CreateWrappingRadioButton(
            "LocalSyncDirGroup",
            "🔄 雙向整併同步 (推薦)",
            "比對兩端差異並由您裁決，整併結果同時寫回本機目前庫與外部目標檔案",
            isChecked: true);

        var rbLocalPull = CreateWrappingRadioButton(
            "LocalSyncDirGroup",
            "⬇️ 從外部檔案整併至本機",
            "提取外部檔案之新增與較新資料整併回目前本機名片庫");

        var rbLocalPush = CreateWrappingRadioButton(
            "LocalSyncDirGroup",
            "⬆️ 從本機整併至外部檔案",
            "將目前本機最新名片庫整併覆蓋至外部目標檔案");
        localDirStack.Children.Add(rbLocalBi);
        localDirStack.Children.Add(rbLocalPull);
        localDirStack.Children.Add(rbLocalPush);
        localDirCard.Child = localDirStack;
        localSection.Children.Add(localDirCard);

        rootStack.Children.Add(localSection);

        // ==========================================
        // 4. 雲端同步區塊 (Cloud Drive Section)
        // ==========================================
        var cloudSection = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };

        // 4.1 登入狀態卡
        var authBorder = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(20, 0, 120, 215)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(60, 0, 120, 215)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12)
        };

        var authGrid = new Grid();
        authGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        authGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var authStatusText = new TextBlock
        {
            Text = _authService.IsAuthenticated ? "🟢 已連接至 Google 帳號" : "⚪ 尚未登入 Google 帳號",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(authStatusText, 0);

        var authBtn = new Button
        {
            Content = _authService.IsAuthenticated ? "切換/登出" : "登入 Google",
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(authBtn, 1);

        authGrid.Children.Add(authStatusText);
        authGrid.Children.Add(authBtn);
        authBorder.Child = authGrid;
        cloudSection.Children.Add(authBorder);

        // 4.2 就地 Google OAuth 憑證設定 Expander
        var (savedId, savedSecret) = await _authService.LoadOAuthConfigAsync();
        bool hasOAuth = !string.IsNullOrWhiteSpace(savedId);

        var oauthExpander = new Expander
        {
            Header = "⚙️ Google OAuth 憑證設定 (Client ID & Client Secret)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsExpanded = !hasOAuth && !_authService.IsAuthenticated
        };

        var oauthStack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 6) };
        oauthStack.Children.Add(new TextBlock
        {
            Text = "若尚未建立專屬 OAuth 憑證，請前往 Google Cloud Console 建立「桌面應用程式」憑證：",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        var clientIdBox = new TextBox
        {
            Text = savedId ?? string.Empty,
            PlaceholderText = "例如: xxxxxxxx.apps.googleusercontent.com",
            FontSize = 11
        };
        oauthStack.Children.Add(new TextBlock { Text = "Google OAuth 2.0 Client ID (必填)：", FontSize = 11, FontWeight = FontWeights.SemiBold });
        oauthStack.Children.Add(clientIdBox);

        var clientSecretBox = new PasswordBox
        {
            Password = savedSecret ?? string.Empty,
            PlaceholderText = "請輸入 Google OAuth 2.0 Client Secret",
            FontSize = 11
        };
        oauthStack.Children.Add(new TextBlock { Text = "Google OAuth 2.0 Client Secret (必填)：", FontSize = 11, FontWeight = FontWeights.SemiBold });
        oauthStack.Children.Add(clientSecretBox);

        var oauthActionGrid = new Grid();
        oauthActionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        oauthActionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var oauthNotice = new TextBlock { Text = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(oauthNotice, 0);

        var oauthBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Grid.SetColumn(oauthBtns, 1);

        var openConsoleBtn = new Button { Content = "🔗 開啟 Google Cloud 憑證控制台", FontSize = 11 };
        openConsoleBtn.Click += (s, e) => _authService.OpenGoogleCloudConsole();

        var saveOAuthBtn = new Button { Content = "💾 儲存憑證設定", FontSize = 11, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        saveOAuthBtn.Click += async (s, e) =>
        {
            string cid = clientIdBox.Text.Trim();
            string csec = clientSecretBox.Password.Trim();
            if (string.IsNullOrWhiteSpace(cid))
            {
                oauthNotice.Text = "⚠️ Client ID 不能為空！";
                oauthNotice.Foreground = new SolidColorBrush(Colors.Red);
                return;
            }
            await _authService.SaveOAuthConfigAsync(cid, csec);
            oauthNotice.Text = "✅ 憑證設定已成功保存！";
            oauthNotice.Foreground = new SolidColorBrush(Colors.Green);
        };

        oauthBtns.Children.Add(openConsoleBtn);
        oauthBtns.Children.Add(saveOAuthBtn);
        oauthActionGrid.Children.Add(oauthNotice);
        oauthActionGrid.Children.Add(oauthBtns);
        oauthStack.Children.Add(oauthActionGrid);

        oauthExpander.Content = oauthStack;
        cloudSection.Children.Add(oauthExpander);

        // 4.3 雲端同步方向
        var cloudDirCard = new Border
        {
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12)
        };
        var cloudDirStack = new StackPanel { Spacing = 8 };
        cloudDirStack.Children.Add(new TextBlock
        {
            Text = "🧭 雲端同步方向模式：",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });

        var rbCloudBi = CreateWrappingRadioButton(
            "CloudSyncDirGroup",
            "🔄 雙向同步 (推薦)",
            "先自雲端比對整併，再將最新資料回推雲端",
            isChecked: true);

        var rbCloudPull = CreateWrappingRadioButton(
            "CloudSyncDirGroup",
            "⬇️ 從雲端同步至本機",
            "下載雲端資料並比對整併回本機");

        var rbCloudPush = CreateWrappingRadioButton(
            "CloudSyncDirGroup",
            "⬆️ 從本機同步至雲端",
            "將本機目前名片庫同步推至雲端");

        cloudDirStack.Children.Add(rbCloudBi);
        cloudDirStack.Children.Add(rbCloudPull);
        cloudDirStack.Children.Add(rbCloudPush);
        cloudDirCard.Child = cloudDirStack;
        cloudSection.Children.Add(cloudDirCard);

        rootStack.Children.Add(cloudSection);

        // 5. 加密密碼卡片 (若本機或雲端有加密)
        var pwdCard = new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Padding = new Thickness(12),
            Visibility = Visibility.Collapsed
        };
        var pwdStack = new StackPanel { Spacing = 6 };
        pwdStack.Children.Add(new TextBlock { Text = "🔐 資料庫主密碼 (SQLCipher 加密保護)：", FontSize = 12, FontWeight = FontWeights.SemiBold });
        var pwdBox = new PasswordBox { PlaceholderText = "若資料庫受加密保護，請輸入主密碼", PasswordChar = "●" };
        if (!string.IsNullOrEmpty(_storageService.CurrentPassphrase))
        {
            pwdBox.Password = _storageService.CurrentPassphrase;
        }
        pwdStack.Children.Add(pwdBox);
        pwdCard.Child = pwdStack;
        rootStack.Children.Add(pwdCard);

        // 檢查是否需顯示密碼卡
        if (StorageService.IsDatabaseEncrypted(currentDbPath))
        {
            pwdCard.Visibility = Visibility.Visible;
        }

        // 6. 進度條與狀態文字
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Visibility = Visibility.Collapsed };
        var statusText = new TextBlock { Text = "", FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        rootStack.Children.Add(progressBar);
        rootStack.Children.Add(statusText);

        var scrollViewer = new ScrollViewer
        {
            Content = rootStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 620,
            Padding = new Thickness(0, 0, 10, 0)
        };

        dialog.Content = scrollViewer;

        // 模式切換事件
        rbModeLocal.Checked += (s, e) =>
        {
            localSection.Visibility = Visibility.Visible;
            cloudSection.Visibility = Visibility.Collapsed;
        };
        rbModeCloud.Checked += (s, e) =>
        {
            localSection.Visibility = Visibility.Collapsed;
            cloudSection.Visibility = Visibility.Visible;
        };

        // 檔案瀏覽按鈕事件
        string? selectedFilePath = null;
        pickFileBtn.Click += async (s, e) =>
        {
            try
            {
                var openPicker = new FileOpenPicker();
                openPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                openPicker.FileTypeFilter.Add(".sqlite3");
                openPicker.FileTypeFilter.Add(".db");
                openPicker.FileTypeFilter.Add(".zip");

                IntPtr hwnd = IntPtr.Zero;
                if (MainWindow.CurrentWindow != null)
                {
                    hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.CurrentWindow);
                }
                if (hwnd != IntPtr.Zero)
                {
                    WinRT.Interop.InitializeWithWindow.Initialize(openPicker, hwnd);
                }

                var file = await openPicker.PickSingleFileAsync();
                if (file != null)
                {
                    if (string.Equals(Path.GetFullPath(file.Path), Path.GetFullPath(currentDbPath), StringComparison.OrdinalIgnoreCase))
                    {
                        fileInfoText.Text = "⚠️ 所選檔案為目前正在使用的本機資料庫，請選取另一個不同的檔案！";
                        fileInfoText.Foreground = new SolidColorBrush(Colors.Red);
                        fileInfoText.Visibility = Visibility.Visible;
                        return;
                    }

                    selectedFilePath = file.Path;
                    targetPathBox.Text = file.Path;

                    var fi = new FileInfo(file.Path);
                    string ext = fi.Extension.ToLowerInvariant();
                    string typeDesc = ext == ".zip" ? "MajiOCR ZIP 備份封裝檔" : "SQLite 獨立資料庫檔";
                    fileInfoText.Text = $"✅ 已選取：{typeDesc} (大小：{fi.Length / 1024.0:F1} KB)";
                    fileInfoText.Foreground = new SolidColorBrush(Colors.Green);
                    fileInfoText.Visibility = Visibility.Visible;

                    // 若選取的外部 sqlite3 也是加密的，確保顯示密碼欄
                    if (ext != ".zip" && StorageService.IsDatabaseEncrypted(file.Path))
                    {
                        pwdCard.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (Exception ex)
            {
                fileInfoText.Text = $"選取檔案失敗：{ex.Message}";
                fileInfoText.Foreground = new SolidColorBrush(Colors.Red);
                fileInfoText.Visibility = Visibility.Visible;
            }
        };

        // 雲端登入按鈕事件
        authBtn.Click += async (s, e) =>
        {
            if (_authService.IsAuthenticated)
            {
                _authService.Logout();
                authStatusText.Text = "⚪ 尚未登入 Google 帳號";
                authBtn.Content = "登入 Google";
            }
            else
            {
                string cid = clientIdBox.Text.Trim();
                string csec = clientSecretBox.Password.Trim();
                if (string.IsNullOrWhiteSpace(cid))
                {
                    oauthExpander.IsExpanded = true;
                    statusText.Text = "⚠️ 尚未填寫 Google OAuth Client ID，請在上方憑證設定區塊填寫後再登入。";
                    statusText.Visibility = Visibility.Visible;
                    statusText.Foreground = new SolidColorBrush(Colors.DarkOrange);
                    return;
                }

                // 自動儲存憑證
                await _authService.SaveOAuthConfigAsync(cid, csec);

                var loginRes = await _authService.LoginWithBrowserAsync(cid, csec);
                if (loginRes.Success)
                {
                    authStatusText.Text = "🟢 已成功登入 Google 帳號！";
                    authBtn.Content = "登出帳號";
                    statusText.Visibility = Visibility.Collapsed;
                    oauthExpander.IsExpanded = false;
                }
                else
                {
                    statusText.Text = $"登入失敗：{loginRes.ErrorMessage}";
                    statusText.Visibility = Visibility.Visible;
                    statusText.Foreground = new SolidColorBrush(Colors.Red);
                }
            }
        };

        // 主按鈕執行事件
        dialog.PrimaryButtonClick += async (s, args) =>
        {
            args.Cancel = true; // 保持對話框進行非同步處理

            string? enteredPass = string.IsNullOrEmpty(pwdBox.Password) ? _storageService.CurrentPassphrase : pwdBox.Password;

            // ==========================================
            // 模式 1: 本地端檔案同步
            // ==========================================
            if (rbModeLocal.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(selectedFilePath) || !File.Exists(selectedFilePath))
                {
                    statusText.Text = "⚠️ 請先點擊【📂 瀏覽選取檔案...】選取要同步整併的目標檔案！";
                    statusText.Foreground = new SolidColorBrush(Colors.Red);
                    statusText.Visibility = Visibility.Visible;
                    return;
                }

                dialog.IsPrimaryButtonEnabled = false;
                progressBar.Visibility = Visibility.Visible;
                statusText.Visibility = Visibility.Visible;

                var progress = new Progress<BackupProgress>(p =>
                {
                    progressBar.Value = p.Percentage;
                    statusText.Text = p.StatusMessage;
                    statusText.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
                });

                var dir = rbLocalBi.IsChecked == true ? DatabaseSyncDirection.Bidirectional :
                          rbLocalPull.IsChecked == true ? DatabaseSyncDirection.PullToLocal : DatabaseSyncDirection.PushToTarget;

                try
                {
                    await ExecuteLocalFileWorkflowAsync(dialog, dir, storageRoot, selectedFilePath, enteredPass, progress);
                }
                catch (Exception ex)
                {
                    progressBar.Value = 100;
                    statusText.Text = $"🔴 本地同步發生錯誤：{ex.Message}";
                    statusText.Foreground = new SolidColorBrush(Colors.Red);
                    dialog.IsPrimaryButtonEnabled = true;
                }
                return;
            }

            // ==========================================
            // 模式 2: Google Drive 雲端同步
            // ==========================================
            if (!_authService.IsAuthenticated)
            {
                oauthExpander.IsExpanded = true;
                statusText.Text = "⚠️ 請先填寫上方憑證並登入 Google 帳號以執行雲端同步。";
                statusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
                statusText.Visibility = Visibility.Visible;
                return;
            }

            dialog.IsPrimaryButtonEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            statusText.Visibility = Visibility.Visible;

            var cloudProgress = new Progress<BackupProgress>(p =>
            {
                progressBar.Value = p.Percentage;
                statusText.Text = p.StatusMessage;
                statusText.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            });

            var cloudDir = rbCloudBi.IsChecked == true ? DatabaseSyncDirection.Bidirectional :
                           rbCloudPull.IsChecked == true ? DatabaseSyncDirection.PullToLocal : DatabaseSyncDirection.PushToTarget;

            try
            {
                await ExecuteCloudWorkflowAsync(dialog, cloudDir, storageRoot, enteredPass, cloudProgress);
            }
            catch (Exception ex)
            {
                progressBar.Value = 100;
                statusText.Text = $"🔴 雲端同步過程發生錯誤：{ex.Message}";
                statusText.Foreground = new SolidColorBrush(Colors.Red);
                dialog.IsPrimaryButtonEnabled = true;
            }
        };

        await dialog.ShowAsync();
    }

    #region 本地端檔案同步處理管線

    private async Task ExecuteLocalFileWorkflowAsync(
        ContentDialog dialog,
        DatabaseSyncDirection direction,
        string storageRoot,
        string targetFilePath,
        string? passphrase,
        IProgress<BackupProgress> progress)
    {
        progress.Report(new BackupProgress(15, "正在準備外部資料檔案..."));
        string tempStagingDir = Path.Combine(Path.GetTempPath(), $"sync_local_staging_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempStagingDir);

        try
        {
            string ext = Path.GetExtension(targetFilePath).ToLowerInvariant();
            string remoteDbPath = Path.Combine(tempStagingDir, StoragePathHelper.DatabaseFileName);

            if (ext == ".zip")
            {
                progress.Report(new BackupProgress(30, "正在解壓縮備份封裝並提取名片資料庫..."));
                ZipFile.ExtractToDirectory(targetFilePath, tempStagingDir);
            }
            else
            {
                // .sqlite3 或 .db 檔案
                progress.Report(new BackupProgress(30, "正在讀取目標 SQLite 資料庫與關聯圖檔..."));
                File.Copy(targetFilePath, remoteDbPath, true);

                // 若該檔案所在目錄有 card-images，一併拷貝供比對
                string targetDir = Path.GetDirectoryName(targetFilePath) ?? "";
                string targetImagesDir = Path.Combine(targetDir, "card-images");
                if (Directory.Exists(targetImagesDir))
                {
                    string stagingImagesDir = Path.Combine(tempStagingDir, "card-images");
                    Directory.CreateDirectory(stagingImagesDir);
                    foreach (var img in Directory.GetFiles(targetImagesDir))
                    {
                        File.Copy(img, Path.Combine(stagingImagesDir, Path.GetFileName(img)), true);
                    }
                }
            }

            if (!File.Exists(remoteDbPath))
            {
                throw new InvalidOperationException("目標檔案中未找到有效的名片資料庫 (contacts.sqlite3)。");
            }

            // 驗證密碼
            bool isRemoteEncrypted = StorageService.IsDatabaseEncrypted(remoteDbPath);
            string? remotePass = passphrase;
            if (isRemoteEncrypted && !string.IsNullOrEmpty(remotePass))
            {
                if (!StorageService.TryVerifyPassphrase(remoteDbPath, remotePass, out _))
                {
                    remotePass = null;
                }
            }

            // 比對差異
            progress.Report(new BackupProgress(50, "正在比對雙方名片差異與更新時間..."));
            var diffReport = await _mergeService.CompareDatabasesAsync(
                storageRoot,
                tempStagingDir,
                passphrase,
                remotePass);

            // 暫時隱藏同步中心，開啟比對裁決視窗
            dialog.Hide();

            var mergeDialog = new DatabaseMergeDialog(_xamlRoot, diffReport, $"外部檔案: {Path.GetFileName(targetFilePath)}");
            bool confirmed = await mergeDialog.ShowAsync();
            if (!confirmed) return;

            // 執行本機整併
            progress.Report(new BackupProgress(70, "正在執行名片整併與雙向資料安全寫入..."));
            var mergeResult = await _mergeService.ExecuteMergeAsync(
                storageRoot,
                tempStagingDir,
                diffReport,
                passphrase,
                remotePass);

            if (!mergeResult.Success)
            {
                await ShowNoticeDialogAsync("整併失敗", mergeResult.Message);
                return;
            }

            // 若為雙向同步或推送到外部，更新外部檔案！
            if (direction == DatabaseSyncDirection.Bidirectional || direction == DatabaseSyncDirection.PushToTarget)
            {
                progress.Report(new BackupProgress(90, "正在將整併成果同步回寫外部目標檔案..."));

                if (ext == ".zip")
                {
                    // 重新打包 ZIP 並取代原檔
                    var (newZip, newManifest) = await _driveBackupService.CreateLocalBackupPackageAsync(storageRoot, "windows", passphrase);
                    File.Copy(newZip, targetFilePath, true);
                    try { File.Delete(newZip); } catch { }
                }
                else
                {
                    // 複製更新後的 sqlite3 回外部目標
                    string localDb = Path.Combine(storageRoot, StoragePathHelper.DatabaseFileName);
                    File.Copy(localDb, targetFilePath, true);

                    // 複製新圖檔到外部目錄
                    string targetDir = Path.GetDirectoryName(targetFilePath) ?? "";
                    string targetImagesDir = Path.Combine(targetDir, "card-images");
                    string localImagesDir = Path.Combine(storageRoot, "card-images");
                    if (Directory.Exists(localImagesDir))
                    {
                        Directory.CreateDirectory(targetImagesDir);
                        foreach (var img in Directory.GetFiles(localImagesDir))
                        {
                            string destImg = Path.Combine(targetImagesDir, Path.GetFileName(img));
                            if (!File.Exists(destImg))
                            {
                                File.Copy(img, destImg, true);
                            }
                        }
                    }
                }
            }

            string successSummary = direction == DatabaseSyncDirection.Bidirectional ?
                $"🎉 本地雙資料庫雙向整併同步圓滿完成！\n\n" +
                $"• 外部檔案差異已完整整併至目前本機庫\n" +
                $"• 整併後的最新名片資料與圖檔已同步回寫至外部檔案：\n  {Path.GetFileName(targetFilePath)}\n" +
                $"• 兩側資料庫現已完全保持一致！" :
                direction == DatabaseSyncDirection.PullToLocal ?
                $"✅ 成功自外部檔案整併至本機資料庫！\n\n{mergeResult.Message}" :
                $"✅ 成功將本機最新名片資料整併推至外部目標檔案！";

            await ShowNoticeDialogAsync("同步成功", successSummary);
            await _onDataRefreshed();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempStagingDir)) Directory.Delete(tempStagingDir, true);
            }
            catch { }
        }
    }

    #endregion

    #region Google Drive 雲端同步處理管線

    private async Task ExecuteCloudWorkflowAsync(
        ContentDialog dialog,
        DatabaseSyncDirection direction,
        string storageRoot,
        string? passphrase,
        IProgress<BackupProgress> progress)
    {
        string token = _authService.CurrentAccessToken!;

        // 僅自本機同步推至雲端
        if (direction == DatabaseSyncDirection.PushToTarget)
        {
            progress.Report(new BackupProgress(20, "正在封裝本機資料庫與名片圖檔..."));
            var (zipPath, manifest) = await _driveBackupService.CreateLocalBackupPackageAsync(storageRoot, "windows", passphrase, progress);

            progress.Report(new BackupProgress(50, "正在同步上傳至 Google Drive..."));
            var uploadRes = await _driveBackupService.UploadBackupToDriveAsync(zipPath, manifest, token, progress);

            if (uploadRes.Success)
            {
                dialog.Hide();
                await ShowNoticeDialogAsync("同步成功", $"✅ 成功將本機資料庫同步至 Google Drive！\n已上傳 {manifest.ContactCount} 筆名片。");
            }
            else
            {
                throw new InvalidOperationException(uploadRes.Message);
            }
            return;
        }

        // 雙向或拉取模式
        progress.Report(new BackupProgress(15, "正在查詢 Google Drive 最新雲端資料庫..."));
        var versions = await _driveBackupService.ListDriveBackupsAsync(token);
        if (versions.Count == 0)
        {
            if (direction == DatabaseSyncDirection.Bidirectional)
            {
                progress.Report(new BackupProgress(30, "雲端尚無資料庫，自動將本機資料庫推播為雲端初始版本..."));
                var (zipPath, manifest) = await _driveBackupService.CreateLocalBackupPackageAsync(storageRoot, "windows", passphrase, progress);
                var uploadRes = await _driveBackupService.UploadBackupToDriveAsync(zipPath, manifest, token, progress);
                dialog.Hide();
                await ShowNoticeDialogAsync("同步成功", $"✅ 雲端尚無資料庫，已將本機 {manifest.ContactCount} 筆名片建立為雲端初始版本！");
                return;
            }
            else
            {
                throw new InvalidOperationException("Google Drive 上尚未找到任何名片資料庫備份。");
            }
        }

        var latestVersion = versions.OrderByDescending(v => v.CreatedAtUtc).First();

        progress.Report(new BackupProgress(30, $"正在下載雲端最新資料庫 ({latestVersion.FileName})..."));
        string tempZip = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}.zip");
        string tempStagingDir = Path.Combine(Path.GetTempPath(), $"sync_staging_{Guid.NewGuid():N}");

        try
        {
            var dlRes = await _driveBackupService.DownloadBackupFromDriveAsync(latestVersion.FileId, tempZip, token, progress);
            if (!dlRes.Success) throw new InvalidOperationException(dlRes.Message);

            progress.Report(new BackupProgress(50, "正在解壓縮並比對名片差異..."));
            Directory.CreateDirectory(tempStagingDir);
            ZipFile.ExtractToDirectory(tempZip, tempStagingDir);

            string remoteDbPath = Path.Combine(tempStagingDir, StoragePathHelper.DatabaseFileName);
            bool isRemoteEncrypted = StorageService.IsDatabaseEncrypted(remoteDbPath);
            string? remotePass = passphrase;

            if (isRemoteEncrypted && !string.IsNullOrEmpty(remotePass))
            {
                if (!StorageService.TryVerifyPassphrase(remoteDbPath, remotePass, out _))
                {
                    remotePass = null;
                }
            }

            var diffReport = await _mergeService.CompareDatabasesAsync(
                storageRoot,
                tempStagingDir,
                passphrase,
                remotePass);

            dialog.Hide();

            var mergeDialog = new DatabaseMergeDialog(_xamlRoot, diffReport, $"Google Drive: {latestVersion.FileName}");
            bool confirmed = await mergeDialog.ShowAsync();
            if (!confirmed) return;

            var mergeResult = await _mergeService.ExecuteMergeAsync(
                storageRoot,
                tempStagingDir,
                diffReport,
                passphrase,
                remotePass);

            if (!mergeResult.Success)
            {
                await ShowNoticeDialogAsync("整併失敗", mergeResult.Message);
                return;
            }

            if (direction == DatabaseSyncDirection.Bidirectional)
            {
                var (pushZip, pushManifest) = await _driveBackupService.CreateLocalBackupPackageAsync(storageRoot, "windows", passphrase);
                var pushRes = await _driveBackupService.UploadBackupToDriveAsync(pushZip, pushManifest, token);

                await ShowNoticeDialogAsync("雙向同步成功",
                    $"🎉 雙向同步圓滿完成！\n\n" +
                    $"• 雲端差異已整併至本機\n" +
                    $"• 最新名片庫 ({pushManifest.ContactCount} 筆) 已自動同步更新至 Google Drive\n" +
                    $"• 所有端點現已保持最新一致狀態！");
            }
            else
            {
                await ShowNoticeDialogAsync("同步完成", $"✅ 成功自雲端同步並整併至本機！\n\n{mergeResult.Message}");
            }

            await _onDataRefreshed();
        }
        finally
        {
            try
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
                if (Directory.Exists(tempStagingDir)) Directory.Delete(tempStagingDir, true);
            }
            catch { }
        }
    }

    #endregion

    private async Task ShowNoticeDialogAsync(string title, string message)
    {
        var noticeDialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
            CloseButtonText = "確定",
            XamlRoot = _xamlRoot
        };
        await noticeDialog.ShowAsync();
    }

    private static RadioButton CreateWrappingRadioButton(string groupName, string title, string? subtitle = null, bool isChecked = false)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }
        return new RadioButton
        {
            GroupName = groupName,
            Content = stack,
            IsChecked = isChecked,
            Margin = new Thickness(0, 2, 0, 4)
        };
    }
}
