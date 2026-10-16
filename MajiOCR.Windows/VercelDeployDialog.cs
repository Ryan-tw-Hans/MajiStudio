using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MajiOCR_Windows.Services;

namespace MajiOCR_Windows;

/// <summary>
/// Windows 端「📱 部署 iOS / Web 雲端版 (Vercel)」3 步驟一鍵部署精靈對話框。
/// 符合 RS-ISSUE-0044-v0.1, AR-ISSUE-0044-v0.1 與 DP-ISSUE-0044-v0.1。
/// </summary>
public static class VercelDeployDialog
{
    public static async Task ShowAsync(
        XamlRoot xamlRoot,
        IAppSettingsService settingsService,
        IVercelDeploymentService? customDeploymentService = null)
    {
        if (xamlRoot == null) return;

        var deployService = customDeploymentService ?? new VercelDeploymentService();
        var currentSettings = settingsService.LoadSettings();

        var rootStack = new StackPanel
        {
            Spacing = 14,
            Width = 560
        };

        // Intro Banner
        var introBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Background = new SolidColorBrush(Color.FromArgb(25, 15, 108, 189)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 15, 108, 189)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "💡 免 Mac 電腦、免 Apple 開發者年費！本精靈會將內建的「MajiOCR iOS 行動網頁版 (PWA)」自動部署至您個人的免費 Vercel 雲端帳號，並產生 QR Code 供您的 iPhone X 以上手機掃描加入主畫面（名片資料庫保存在您的 Google Drive 與 iPhone 本機，絕不上傳至 Vercel）。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5
            }
        };
        rootStack.Children.Add(introBorder);

        // --- Step 1: Register / Login Vercel & Get Access Token ---
        var step1Stack = new StackPanel { Spacing = 8 };
        step1Stack.Children.Add(new TextBlock
        {
            Text = "步驟 1：註冊／登入 Vercel 並取得 Access Token",
            FontWeight = FontWeights.Bold,
            FontSize = 14
        });
        step1Stack.Children.Add(new TextBlock
        {
            Text = "1. 若尚無帳號，請點擊下方按鈕免費註冊 Vercel（支援直接使用 GitHub / Google 帳號登入）。\n2. 登入後點擊「前往建立 Access Token」，點選 Create（Scope 選 Full Account），複製產生的 Token 貼入下方：",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.85
        });

        var browserBtnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };

        var signupBtn = new Button
        {
            Content = "🌐 1. 前往 Vercel 註冊 / 登入",
            Padding = new Thickness(12, 6, 12, 6)
        };
        signupBtn.Click += (_, _) => deployService.OpenVercelSignupPage();

        var tokenPageBtn = new Button
        {
            Content = "🔑 2. 前往建立 Access Token",
            Padding = new Thickness(12, 6, 12, 6)
        };
        tokenPageBtn.Click += (_, _) => deployService.OpenVercelTokensPage();

        browserBtnRow.Children.Add(signupBtn);
        browserBtnRow.Children.Add(tokenPageBtn);
        step1Stack.Children.Add(browserBtnRow);

        var tokenBox = new PasswordBox
        {
            Header = "Vercel Access Token (必填) *",
            PlaceholderText = "貼上從 https://vercel.com/account/tokens 複製的 Token...",
            Password = currentSettings.VercelAccessToken ?? string.Empty,
            IsPasswordRevealButtonEnabled = true
        };
        step1Stack.Children.Add(tokenBox);

        var step1Border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = step1Stack
        };
        rootStack.Children.Add(step1Border);

        // --- Step 2: Project Name & Optional Web Google OAuth Client ID ---
        var step2Stack = new StackPanel { Spacing = 8 };
        step2Stack.Children.Add(new TextBlock
        {
            Text = "步驟 2：設定專案名稱並一鍵導入部署至 Vercel",
            FontWeight = FontWeights.Bold,
            FontSize = 14
        });

        var projectNameBox = new TextBox
        {
            Header = "Vercel 專案名稱 *",
            PlaceholderText = "majiocr-ios-pwa",
            Text = string.IsNullOrWhiteSpace(currentSettings.VercelProjectName)
                ? VercelDeploymentService.DefaultProjectName
                : currentSettings.VercelProjectName
        };
        step2Stack.Children.Add(projectNameBox);

        var webClientIdBox = new TextBox
        {
            Header = "Web 端 Google OAuth Client ID（選填，供 iPhone 直接登入 Google Drive）",
            PlaceholderText = "例如：123456789-xxxx.apps.googleusercontent.com（留空仍可用 iPhone 檔案 App 匯入備份）",
            Text = currentSettings.VercelWebGoogleClientId ?? string.Empty
        };
        step2Stack.Children.Add(webClientIdBox);

        var deployBtn = new Button
        {
            Content = "🚀 一鍵導入專案並發布至 Vercel",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 10, 14, 10),
            FontWeight = FontWeights.SemiBold
        };
        if (Application.Current?.Resources?.ContainsKey("AccentButtonStyle") == true &&
            Application.Current.Resources["AccentButtonStyle"] is Style accentStyle)
        {
            deployBtn.Style = accentStyle;
        }
        step2Stack.Children.Add(deployBtn);

        var statusRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Visibility = Visibility.Collapsed
        };
        var progressRing = new ProgressRing
        {
            Width = 20,
            Height = 20,
            IsActive = false
        };
        var statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
            MaxWidth = 490
        };
        statusRow.Children.Add(progressRing);
        statusRow.Children.Add(statusText);
        step2Stack.Children.Add(statusRow);

        var step2Border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = step2Stack
        };
        rootStack.Children.Add(step2Border);

        // --- Step 3: Deployed URL & iPhone X Installation QR Code ---
        var step3Stack = new StackPanel { Spacing = 8 };
        step3Stack.Children.Add(new TextBlock
        {
            Text = "步驟 3：使用 iPhone 相機掃描 QR Code 安裝至主畫面",
            FontWeight = FontWeights.Bold,
            FontSize = 14
        });

        var urlBox = new TextBox
        {
            Header = "您的專屬 iOS / Web PWA 正式網址",
            IsReadOnly = true,
            Text = currentSettings.LastDeployedVercelUrl ?? string.Empty
        };
        step3Stack.Children.Add(urlBox);

        var urlActionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        var copyUrlBtn = new Button { Content = "📋 複製網址" };
        var openUrlBtn = new Button { Content = "🌐 在瀏覽器開啟預覽" };
        urlActionsRow.Children.Add(copyUrlBtn);
        urlActionsRow.Children.Add(openUrlBtn);
        step3Stack.Children.Add(urlActionsRow);

        var qrImage = new Image
        {
            Width = 210,
            Height = 210,
            HorizontalAlignment = HorizontalAlignment.Center,
            Stretch = Stretch.Uniform
        };
        var qrContainer = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = qrImage
        };
        step3Stack.Children.Add(qrContainer);

        step3Stack.Children.Add(new TextBlock
        {
            Text = "📱 iPhone X 安裝步驟：\n1. 打開 iPhone 相機對準上方 QR Code 掃描並於 Safari 開啟網址。\n2. 點擊 Safari 底部中央的「分享」圖示 ➔ 往下滑點選「加入主畫面 (Add to Home Screen)」。\n3. 從主畫面打開 MajiOCR 圖示，即可從 Google Drive 或手機檔案載入名片資料庫（支援輸入 SQLCipher 主密碼解密）！",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.9
        });

        var step3Border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Background = new SolidColorBrush(Color.FromArgb(20, 16, 185, 129)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 16, 185, 129)),
            BorderThickness = new Thickness(1),
            Visibility = string.IsNullOrWhiteSpace(currentSettings.LastDeployedVercelUrl)
                ? Visibility.Collapsed
                : Visibility.Visible,
            Child = step3Stack
        };
        rootStack.Children.Add(step3Border);

        async Task RenderQrCodeForUrlAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                byte[] bmpBytes = QrCodeBmpGenerator.GenerateBmpBytes(url, scale: 7, quietZoneModules: 4);
                using var ms = new InMemoryRandomAccessStream();
                await ms.WriteAsync(bmpBytes.AsBuffer());
                ms.Seek(0);
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ms);
                qrImage.Source = bmp;
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(currentSettings.LastDeployedVercelUrl))
        {
            await RenderQrCodeForUrlAsync(currentSettings.LastDeployedVercelUrl);
        }

        copyUrlBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(urlBox.Text)) return;
            var pkg = new DataPackage();
            pkg.SetText(urlBox.Text);
            Clipboard.SetContent(pkg);
            statusRow.Visibility = Visibility.Visible;
            statusText.Text = "✅ 已複製專屬網址至剪貼簿！";
        };

        openUrlBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(urlBox.Text)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = urlBox.Text,
                    UseShellExecute = true
                });
            }
            catch { }
        };

        CancellationTokenSource? cts = null;
        deployBtn.Click += async (_, _) =>
        {
            string token = tokenBox.Password.Trim();
            string proj = deployService.NormalizeProjectName(projectNameBox.Text);
            string webClientId = webClientIdBox.Text.Trim();
            projectNameBox.Text = proj;

            if (string.IsNullOrWhiteSpace(token))
            {
                statusRow.Visibility = Visibility.Visible;
                progressRing.IsActive = false;
                progressRing.Visibility = Visibility.Collapsed;
                statusText.Foreground = new SolidColorBrush(Colors.Crimson);
                statusText.Text = "❌ 請先於步驟 1 填入您的 Vercel Access Token。";
                return;
            }

            settingsService.UpdateVercelSettings(token, proj, webClientId);

            deployBtn.IsEnabled = false;
            statusRow.Visibility = Visibility.Visible;
            progressRing.Visibility = Visibility.Visible;
            progressRing.IsActive = true;
            statusText.Foreground = new SolidColorBrush(Colors.DodgerBlue);
            statusText.Text = "正在準備部署...";

            cts = new CancellationTokenSource();
            var progress = new Progress<(int Percentage, string StatusMessage)>(p =>
            {
                statusText.Text = $"[{p.Percentage}%] {p.StatusMessage}";
            });

            var result = await deployService.DeployPwaToVercelAsync(token, proj, webClientId, progress, cts.Token);

            progressRing.IsActive = false;
            progressRing.Visibility = Visibility.Collapsed;
            deployBtn.IsEnabled = true;

            if (result.Success && !string.IsNullOrWhiteSpace(result.ProductionUrl))
            {
                statusText.Foreground = new SolidColorBrush(Colors.SeaGreen);
                statusText.Text = result.Message;
                urlBox.Text = result.ProductionUrl;
                step3Border.Visibility = Visibility.Visible;
                settingsService.UpdateVercelSettings(token, proj, webClientId, result.ProductionUrl);
                await RenderQrCodeForUrlAsync(result.ProductionUrl);
            }
            else
            {
                statusText.Foreground = new SolidColorBrush(Colors.Crimson);
                statusText.Text = $"❌ {result.Message}";
            }
        };

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 620,
            Content = rootStack
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "📱 部署 iOS / Web 雲端版 (Vercel 一鍵精靈)",
            Content = scrollViewer,
            CloseButtonText = "儲存並關閉",
            DefaultButton = ContentDialogButton.None
        };

        dialog.Closing += (_, _) =>
        {
            try { cts?.Cancel(); } catch { }
            settingsService.UpdateVercelSettings(
                tokenBox.Password,
                deployService.NormalizeProjectName(projectNameBox.Text),
                webClientIdBox.Text,
                string.IsNullOrWhiteSpace(urlBox.Text) ? null : urlBox.Text);
        };

        await dialog.ShowAsync();
    }
}
