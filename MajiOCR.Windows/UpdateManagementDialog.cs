using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MajiOCR_Windows.Services.Update;

namespace MajiOCR_Windows;

/// <summary>
/// WinUI 3 ContentDialog for Software Auto Update Management.
/// Fully streamlined UI: hidden cloud endpoints, automatic check and one-click update application.
/// </summary>
public static class UpdateManagementDialog
{
    public static async Task ShowDialogAsync(XamlRoot xamlRoot, UpdateService updateService, Action? onBeforeExitForUpdate = null)
    {
        var settings = updateService.LoadSettings();

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "🔄 軟體更新管理",
            CloseButtonText = "關閉",
            DefaultButton = ContentDialogButton.Close
        };

        var rootStack = new StackPanel { Spacing = 14, Width = 460 };

        // 1. 版本資訊區
        var versionBorder = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 0, 120, 215)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(80, 0, 120, 215)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12)
        };
        var versionStack = new StackPanel { Spacing = 6 };
        var currentVerText = new TextBlock
        {
            Text = $"目前軟體版本：{Program.GetAppVersion()} (v{updateService.CurrentVersion})",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14
        };
        var onlineVerText = new TextBlock
        {
            Text = "雲端版本狀態：正在檢查最新版本...",
            FontSize = 13,
            Foreground = new SolidColorBrush(Colors.DarkSlateGray)
        };
        versionStack.Children.Add(currentVerText);
        versionStack.Children.Add(onlineVerText);
        versionBorder.Child = versionStack;
        rootStack.Children.Add(versionBorder);

        // 2. Change Log 與說明區 (預設展開以直接呈現本版更新資訊)
        var logExpander = new Expander
        {
            Header = "📋 更新內容與變更紀錄 (Change Log)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = true
        };
        var logTextBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 160,
            Text = updateService.LoadLocalChangeLogText().TrimStart('\uFEFF')
        };
        logExpander.Content = logTextBox;
        rootStack.Children.Add(logExpander);

        // 3. 進度與狀態提示
        var statusText = new TextBlock
        {
            Text = "",
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap
        };
        var progressBar = new ProgressBar
        {
            IsIndeterminate = false,
            Visibility = Visibility.Collapsed,
            Height = 6
        };
        rootStack.Children.Add(statusText);
        rootStack.Children.Add(progressBar);

        // 4. 操作按鈕列
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var checkBtn = new Button
        {
            Content = "🔍 檢查更新",
            Style = Application.Current.Resources["DefaultButtonStyle"] as Style
        };

        var updateBtn = new Button
        {
            Content = "🚀 下載並套用更新",
            Style = Application.Current.Resources["AccentButtonStyle"] as Style,
            IsEnabled = false
        };

        var browserDownloadBtn = new Button
        {
            Content = "🌐 開啟瀏覽器下載更新",
            Visibility = Visibility.Collapsed
        };

        buttonPanel.Children.Add(checkBtn);
        buttonPanel.Children.Add(updateBtn);
        buttonPanel.Children.Add(browserDownloadBtn);
        rootStack.Children.Add(buttonPanel);

        dialog.Content = rootStack;

        UpdateCheckResult? lastCheckResult = null;

        async Task PerformCheckAsync()
        {
            checkBtn.IsEnabled = false;
            updateBtn.IsEnabled = false;
            progressBar.IsIndeterminate = true;
            progressBar.Visibility = Visibility.Visible;
            statusText.Text = "正在查詢雲端最新版本...";
            statusText.Foreground = new SolidColorBrush(Colors.DodgerBlue);

            try
            {
                var manifestUrl = !string.IsNullOrWhiteSpace(settings.ReleaseManifestUrl)
                    ? settings.ReleaseManifestUrl
                    : (!string.IsNullOrWhiteSpace(settings.CustomManifestUrl)
                        ? settings.CustomManifestUrl
                        : updateService.DefaultReleaseManifestUrl);

                if (string.IsNullOrWhiteSpace(manifestUrl))
                {
                    onlineVerText.Text = "雲端版本狀態：尚未發布或連結準備中";
                    statusText.Text = "目前已是最新版本。";
                    statusText.Foreground = new SolidColorBrush(Colors.DarkSlateGray);
                    return;
                }

                lastCheckResult = await updateService.CheckAsync(manifestUrl, UpdateChannel.Release);

                if (lastCheckResult.IsAvailable)
                {
                    onlineVerText.Text = $"雲端版本狀態：發現新版本 v{lastCheckResult.Manifest.Version} ({lastCheckResult.Manifest.ReleaseDate})";
                    statusText.Text = $"✨ {lastCheckResult.Reason ?? "有新版本可供更新！"}";
                    statusText.Foreground = new SolidColorBrush(Colors.ForestGreen);
                    updateBtn.IsEnabled = true;

                    if (!string.IsNullOrWhiteSpace(lastCheckResult.Manifest.ChangeLog))
                    {
                        logTextBox.Text = lastCheckResult.Manifest.ChangeLog.TrimStart('\uFEFF');
                        logExpander.IsExpanded = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(lastCheckResult.Manifest.Description))
                    {
                        logTextBox.Text = $"【版本說明】\n{lastCheckResult.Manifest.Description.TrimStart('\uFEFF')}";
                        logExpander.IsExpanded = true;
                    }
                }
                else
                {
                    onlineVerText.Text = $"雲端發布版本：v{lastCheckResult.Manifest.Version}（目前本機 v{updateService.CurrentVersion} 已是最新）";
                    statusText.Text = $"✅ {lastCheckResult.Reason ?? "目前已是最新版本，無需更新。"}";
                    statusText.Foreground = new SolidColorBrush(Colors.DarkSlateGray);
                    logTextBox.Text = updateService.LoadLocalChangeLogText().TrimStart('\uFEFF');
                }
            }
            catch (Exception ex)
            {
                onlineVerText.Text = "雲端版本狀態：暫時無法連線至更新伺服器";
                statusText.Text = $"連線提示：{ex.Message}";
                statusText.Foreground = new SolidColorBrush(Colors.DarkSlateGray);
            }
            finally
            {
                checkBtn.IsEnabled = true;
                progressBar.IsIndeterminate = false;
                progressBar.Visibility = Visibility.Collapsed;
            }
        }

        checkBtn.Click += async (_, _) => await PerformCheckAsync();

        updateBtn.Click += async (_, _) =>
        {
            if (lastCheckResult == null || !lastCheckResult.IsAvailable) return;

            checkBtn.IsEnabled = false;
            updateBtn.IsEnabled = false;
            progressBar.IsIndeterminate = false;
            progressBar.Value = 0;
            progressBar.Visibility = Visibility.Visible;
            statusText.Text = "正在下載更新包...";
            statusText.Foreground = new SolidColorBrush(Colors.DodgerBlue);

            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                if (p.Verifying)
                {
                    progressBar.IsIndeterminate = true;
                    statusText.Text = "下載完成，正在驗證安全雜湊...";
                }
                else if (p.ProgressPercentage.HasValue)
                {
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = p.ProgressPercentage.Value;
                    statusText.Text = $"正在下載更新包：{p.BytesReceived / 1024 / 1024:F1} MB / {p.TotalBytesToReceive / 1024 / 1024:F1} MB ({p.ProgressPercentage.Value:F0}%)";
                }
                else
                {
                    progressBar.IsIndeterminate = true;
                    statusText.Text = $"正在下載更新包：{p.BytesReceived / 1024 / 1024:F1} MB...";
                }
            });

            try
            {
                var downloadResult = await updateService.DownloadAndVerifyAsync(lastCheckResult.Manifest, progress);

                statusText.Text = "✅ 更新包下載並驗證成功！即將關閉主程式並套用更新...";
                statusText.Foreground = new SolidColorBrush(Colors.ForestGreen);

                await Task.Delay(1000);

                onBeforeExitForUpdate?.Invoke();
                dialog.Hide();

                // 啟動獨立更新程序
                updateService.StartSelfUpdate(lastCheckResult.Manifest, downloadResult);

                // 退出當前應用程式
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                statusText.Text = $"❌ 下載或驗證失敗：{ex.Message}\n💡 提示：若 Google 雲端限制直接串流，可點擊「開啟瀏覽器下載更新」直接下載。";
                statusText.Foreground = new SolidColorBrush(Colors.IndianRed);
                checkBtn.IsEnabled = true;
                updateBtn.IsEnabled = true;
                browserDownloadBtn.Visibility = Visibility.Visible;
                progressBar.Visibility = Visibility.Collapsed;
            }
        };

        browserDownloadBtn.Click += async (_, _) =>
        {
            var targetUrl = !string.IsNullOrWhiteSpace(lastCheckResult?.Manifest?.PackageUrl)
                ? lastCheckResult.Manifest.PackageUrl
                : (!string.IsNullOrWhiteSpace(settings.ReleaseManifestUrl) ? settings.ReleaseManifestUrl : updateService.DefaultReleaseManifestUrl);
            if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
            {
                await Windows.System.Launcher.LaunchUriAsync(uri);
            }
        };

        // 開啟對話框時自動執行一次檢查
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            dialog.DispatcherQueue.TryEnqueue(async () =>
            {
                await PerformCheckAsync();
            });
        });

        await dialog.ShowAsync();
    }
}
