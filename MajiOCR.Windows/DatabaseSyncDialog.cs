using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    Bidirectional,  // 雙向同步 (雲端比對整併 + 回推雲端)
    PullToLocal,    // 僅自雲端同步回本機
    PushToCloud     // 僅自本機同步推至雲端
}

/// <summary>
/// 資料庫雲端同步中心對話框 (WinUI 3)
/// 符合 RS-ISSUE-0052-v0.3 規格：支援選擇同步方向（雲端至本機、本機至雲端、雙向同步）
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
            Title = "🔄 資料庫雲端同步中心 (Google Drive Sync)",
            PrimaryButtonText = "🚀 開始執行同步",
            CloseButtonText = "關閉",
            DefaultButton = ContentDialogButton.Primary
        };

        var rootStack = new StackPanel { Spacing = 14, Width = 580 };

        // 1. 說明文字
        var introText = new TextBlock
        {
            Text = "跨裝置名片資料庫同步中心：可自由選擇將本機修改同步至雲端、自雲端同步回本機，或執行完整雙向整併。",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        rootStack.Children.Add(introText);

        // 2. Google 登入狀態卡片
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
        rootStack.Children.Add(authBorder);

        // 3. 同步方向模式選取卡片
        var modeCard = new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Padding = new Thickness(14)
        };

        var modeStack = new StackPanel { Spacing = 10 };
        modeStack.Children.Add(new TextBlock
        {
            Text = "🧭 請選擇同步方向：",
            FontWeight = FontWeights.Bold,
            FontSize = 13
        });

        var rbBidirectional = new RadioButton
        {
            GroupName = "SyncDirectionGroup",
            Content = "🔄 雙向同步 (推薦：先自雲端比對整併，再將最新資料回推雲端)",
            IsChecked = true
        };
        var rbPull = new RadioButton
        {
            GroupName = "SyncDirectionGroup",
            Content = "⬇️ 從雲端同步至本機 (下載雲端資料並比對整併)"
        };
        var rbPush = new RadioButton
        {
            GroupName = "SyncDirectionGroup",
            Content = "⬆️ 從本機同步至雲端 (將本機目前名片庫同步推至雲端)"
        };

        modeStack.Children.Add(rbBidirectional);
        modeStack.Children.Add(rbPull);
        modeStack.Children.Add(rbPush);
        modeCard.Child = modeStack;
        rootStack.Children.Add(modeCard);

        // 4. 加密密碼卡片 (若本機或雲端有加密)
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

        // 5. 進度條與狀態文字
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Visibility = Visibility.Collapsed };
        var statusText = new TextBlock { Text = "", FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        rootStack.Children.Add(progressBar);
        rootStack.Children.Add(statusText);

        dialog.Content = rootStack;

        // 檢查是否需顯示密碼卡
        string dbPath = Path.Combine(StoragePathHelper.GetDefaultStorageRoot(), StoragePathHelper.DatabaseFileName);
        if (StorageService.IsDatabaseEncrypted(dbPath))
        {
            pwdCard.Visibility = Visibility.Visible;
        }

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
                var (savedId, savedSecret) = await _authService.LoadOAuthConfigAsync();
                if (string.IsNullOrWhiteSpace(savedId))
                {
                    statusText.Text = "⚠️ 尚未設定 Google OAuth Client ID，請先透過「匯出與同步 ➔ 從 Google Drive 還原」設定一次憑證。";
                    statusText.Visibility = Visibility.Visible;
                    statusText.Foreground = new SolidColorBrush(Colors.DarkOrange);
                    return;
                }

                var loginRes = await _authService.LoginWithBrowserAsync(savedId, savedSecret);
                if (loginRes.Success)
                {
                    authStatusText.Text = "🟢 已成功登入 Google 帳號！";
                    authBtn.Content = "登出帳號";
                    statusText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    statusText.Text = $"登入失敗：{loginRes.ErrorMessage}";
                    statusText.Visibility = Visibility.Visible;
                    statusText.Foreground = new SolidColorBrush(Colors.Red);
                }
            }
        };

        dialog.PrimaryButtonClick += async (s, args) =>
        {
            args.Cancel = true; // 保持對話框進行非同步處理

            if (!_authService.IsAuthenticated)
            {
                statusText.Text = "⚠️ 請先點擊上方按鈕登入 Google 帳號以執行雲端同步。";
                statusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
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

            var direction = rbBidirectional.IsChecked == true ? DatabaseSyncDirection.Bidirectional :
                            rbPull.IsChecked == true ? DatabaseSyncDirection.PullToLocal : DatabaseSyncDirection.PushToCloud;

            string storageRoot = StoragePathHelper.GetDefaultStorageRoot();
            string? enteredPass = string.IsNullOrEmpty(pwdBox.Password) ? _storageService.CurrentPassphrase : pwdBox.Password;

            try
            {
                await ExecuteSyncWorkflowAsync(dialog, direction, storageRoot, enteredPass, progress);
            }
            catch (Exception ex)
            {
                progressBar.Value = 100;
                statusText.Text = $"🔴 同步過程發生錯誤：{ex.Message}";
                statusText.Foreground = new SolidColorBrush(Colors.Red);
                dialog.IsPrimaryButtonEnabled = true;
            }
        };

        await dialog.ShowAsync();
    }

    private async Task ExecuteSyncWorkflowAsync(
        ContentDialog dialog,
        DatabaseSyncDirection direction,
        string storageRoot,
        string? passphrase,
        IProgress<BackupProgress> progress)
    {
        string token = _authService.CurrentAccessToken!;

        // ==========================================
        // 模式 A: 僅自本機同步推至雲端 (PushToCloud)
        // ==========================================
        if (direction == DatabaseSyncDirection.PushToCloud)
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

        // ==========================================
        // 模式 B & C: 需自雲端拉取 (PullToLocal 或 Bidirectional)
        // ==========================================
        progress.Report(new BackupProgress(15, "正在查詢 Google Drive 最新雲端資料庫..."));
        var versions = await _driveBackupService.ListDriveBackupsAsync(token);
        if (versions.Count == 0)
        {
            if (direction == DatabaseSyncDirection.Bidirectional)
            {
                // 雲端尚無版本，雙向同步直接轉為上傳初始版本
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

        // 選取雲端最新的一筆
        var latestVersion = versions.OrderByDescending(v => v.CreatedAtUtc).First();

        progress.Report(new BackupProgress(30, $"正在下載雲端最新資料庫 ({latestVersion.FileName})..."));
        string tempZip = Path.Combine(Path.GetTempPath(), $"sync_{Guid.NewGuid():N}.zip");
        string tempStagingDir = Path.Combine(Path.GetTempPath(), $"sync_staging_{Guid.NewGuid():N}");

        try
        {
            var dlRes = await _driveBackupService.DownloadBackupFromDriveAsync(latestVersion.FileId, tempZip, token, progress);
            if (!dlRes.Success) throw new InvalidOperationException(dlRes.Message);

            // 解壓至 staging 比對
            progress.Report(new BackupProgress(50, "正在解壓縮並比對名片差異..."));
            Directory.CreateDirectory(tempStagingDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempStagingDir);

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

            // 暫時隱藏同步中心，開啟比對裁決對話框
            dialog.Hide();

            var mergeDialog = new DatabaseMergeDialog(_xamlRoot, diffReport, $"Google Drive: {latestVersion.FileName}");
            bool confirmed = await mergeDialog.ShowAsync();
            if (!confirmed) return;

            // 執行本機整併
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

            // 若為雙向同步，整併後接續將最新本機狀態推回雲端！
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

    private async Task ShowNoticeDialogAsync(string title, string message)
    {
        var noticeDialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            CloseButtonText = "確定",
            XamlRoot = _xamlRoot
        };
        await noticeDialog.ShowAsync();
    }
}
