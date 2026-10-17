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
/// Windows 端「📱 iOS / Web 行動版 (GitHub Pages / Vercel)」設定與部署對話框。
/// 支援：
///   1. 🌟 推薦：GitHub Pages 官方免設定公用版（即時 QR Code 掃描加入 iPhone 主畫面）
///   2. ⚡ 自訂：部署至個人 Vercel 免費雲端帳號
/// 符合 RS-ISSUE-0044、RS-ISSUE-0047、RS-ISSUE-0048、AR-ISSUE-0048 與 DP-ISSUE-0048。
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

        // 頂層容器：寬度自動延展填滿對話框，增加內部間距與舒適邊界
        var rootStack = new StackPanel
        {
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(2, 0, 6, 0)
        };

        // --- Top Mode Switcher (GitHub Pages vs. Vercel) - 等寬 Grid 平分，保證零截字 ---
        var modeGrid = new Grid
        {
            ColumnSpacing = 12,
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var modeGhpBtn = new Button
        {
            Content = "🌟 推薦：官方公用免設定版 (GitHub Pages)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 10, 14, 10),
            FontWeight = FontWeights.SemiBold,
            CornerRadius = new CornerRadius(6)
        };
        Grid.SetColumn(modeGhpBtn, 0);

        var modeVercelBtn = new Button
        {
            Content = "⚡ 自訂：部署至個人 Vercel",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 10, 14, 10),
            FontWeight = FontWeights.Normal,
            CornerRadius = new CornerRadius(6)
        };
        Grid.SetColumn(modeVercelBtn, 1);

        modeGrid.Children.Add(modeGhpBtn);
        modeGrid.Children.Add(modeVercelBtn);
        rootStack.Children.Add(modeGrid);

        void UpdateButtonStyles(bool isGhp)
        {
            if (isGhp)
            {
                modeGhpBtn.FontWeight = FontWeights.SemiBold;
                modeGhpBtn.BorderBrush = new SolidColorBrush(Colors.MediumSeaGreen);
                modeGhpBtn.BorderThickness = new Thickness(2);

                modeVercelBtn.FontWeight = FontWeights.Normal;
                modeVercelBtn.BorderThickness = new Thickness(1);
                modeVercelBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
            }
            else
            {
                modeGhpBtn.FontWeight = FontWeights.Normal;
                modeGhpBtn.BorderThickness = new Thickness(1);
                modeGhpBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));

                modeVercelBtn.FontWeight = FontWeights.SemiBold;
                modeVercelBtn.BorderBrush = new SolidColorBrush(Colors.DodgerBlue);
                modeVercelBtn.BorderThickness = new Thickness(2);
            }
        }
        UpdateButtonStyles(true);

        // Helper to render QR code bitmap
        async Task RenderQrCodeAsync(Image targetImg, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                byte[] bmpBytes = QrCodeBmpGenerator.GenerateBmpBytes(url, scale: 8, quietZoneModules: 4);
                using var ms = new InMemoryRandomAccessStream();
                await ms.WriteAsync(bmpBytes.AsBuffer());
                ms.Seek(0);
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ms);
                targetImg.Source = bmp;
            }
            catch { }
        }

        // =========================================================================
        // PANEL 1: GitHub Pages 官方免設定版 (預設可見)
        // =========================================================================
        var ghpPanel = new StackPanel { Spacing = 14 };

        var ghpIntroBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Background = new SolidColorBrush(Color.FromArgb(28, 16, 185, 129)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 16, 185, 129)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "💡 免註冊任何帳號、免申請 Token！直接拿起 iPhone 相機對準下方 QR Code 即可一鍵開啟 MajiOCR 行動版並加入主畫面。名片資料庫與解密金鑰 100% 保存在您各自的手機本機與個人 Google Drive，完全隔離且保護隱私。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                LineHeight = 20
            }
        };
        ghpPanel.Children.Add(ghpIntroBorder);

        // GitHub Pages QR Code Container
        var ghpQrImage = new Image
        {
            Width = 230,
            Height = 230,
            HorizontalAlignment = HorizontalAlignment.Center,
            Stretch = Stretch.Uniform
        };
        var ghpQrContainer = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = ghpQrImage
        };
        ghpPanel.Children.Add(ghpQrContainer);

        // GitHub Pages URL Box & Actions
        var ghpUrlBox = new TextBox
        {
            Header = "GitHub Pages 官方公用網址",
            Text = string.IsNullOrWhiteSpace(currentSettings.GitHubPagesUrl)
                ? "https://ryan-tw-hans.github.io/MajiStudio/"
                : currentSettings.GitHubPagesUrl
        };
        ghpPanel.Children.Add(ghpUrlBox);

        var ghpActionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        var ghpCopyBtn = new Button
        {
            Content = "📋 複製網址",
            Padding = new Thickness(12, 8, 12, 8)
        };
        var ghpOpenBtn = new Button
        {
            Content = "🌐 在瀏覽器開啟預覽",
            Padding = new Thickness(12, 8, 12, 8)
        };
        var ghpRefreshQrBtn = new Button
        {
            Content = "🔄 重新產生 QR Code",
            Padding = new Thickness(12, 8, 12, 8)
        };
        ghpActionsRow.Children.Add(ghpCopyBtn);
        ghpActionsRow.Children.Add(ghpOpenBtn);
        ghpActionsRow.Children.Add(ghpRefreshQrBtn);
        ghpPanel.Children.Add(ghpActionsRow);

        var ghpStatusText = new TextBlock
        {
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Colors.SeaGreen),
            Visibility = Visibility.Collapsed
        };
        ghpPanel.Children.Add(ghpStatusText);

        // Step-by-step instructions
        var ghpStepsBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Background = new SolidColorBrush(Color.FromArgb(15, 128, 128, 128)),
            Child = new TextBlock
            {
                Text = "📱 iPhone 安裝與使用步驟：\n" +
                       "1. 打開 iPhone 內建相機，對準上方 QR Code 掃描並於 Safari 開啟。\n" +
                       "2. 點擊 Safari 底部中央的「分享」圖示 ➔ 往下滑點選「加入主畫面 (Add to Home Screen)」。\n" +
                       "3. 手機桌面上即會建立 MajiOCR 圖示，點擊即可全螢幕秒速離線開啟。\n" +
                       "4. 點擊頂部「☁️ 載入/同步資料庫」➔ 可選擇直接登入 Google Drive 或從 iPhone「檔案」App 選取 Windows 匯出的備份檔（支援輸入 SQLCipher 主密碼解密）。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                Opacity = 0.9,
                LineHeight = 19
            }
        };
        ghpPanel.Children.Add(ghpStepsBorder);

        rootStack.Children.Add(ghpPanel);

        // =========================================================================
        // PANEL 2: Vercel 個人一鍵部署精靈 (初始隱藏)
        // =========================================================================
        var vercelPanel = new StackPanel
        {
            Spacing = 14,
            Visibility = Visibility.Collapsed
        };

        var vercelIntroBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Background = new SolidColorBrush(Color.FromArgb(28, 15, 108, 189)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 15, 108, 189)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "💡 本精靈會將內建的「MajiOCR iOS 行動網頁版 (PWA)」自動部署至您個人的免費 Vercel 雲端帳號，並產生專屬網址與 QR Code。名片資料庫保存在您的 Google Drive 與 iPhone 本機，絕不上傳至 Vercel。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                LineHeight = 20
            }
        };
        vercelPanel.Children.Add(vercelIntroBorder);

        // Step 1: Register / Login Vercel & Get Access Token
        var step1Stack = new StackPanel { Spacing = 10 };
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
            FontSize = 12.5,
            Opacity = 0.9,
            LineHeight = 18
        });

        var browserBtnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };

        var signupBtn = new Button
        {
            Content = "🌐 1. 前往 Vercel 註冊 / 登入",
            Padding = new Thickness(12, 7, 12, 7)
        };
        signupBtn.Click += (_, _) => deployService.OpenVercelSignupPage();

        var tokenPageBtn = new Button
        {
            Content = "🔑 2. 前往建立 Access Token",
            Padding = new Thickness(12, 7, 12, 7)
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
            Padding = new Thickness(14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = step1Stack
        };
        vercelPanel.Children.Add(step1Border);

        // Step 2: Project Name & Optional Web Google OAuth Client ID
        var step2Stack = new StackPanel { Spacing = 12 };
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

        // 拆分解構 Header 與說明文字，避免 WinUI 3 TextBox Header 換行導致的排版錯位疊加
        var webClientIdStack = new StackPanel { Spacing = 4 };
        webClientIdStack.Children.Add(new TextBlock
        {
            Text = "Web 端 Google OAuth Client ID（選填，供 iPhone 直接登入 Google Drive）",
            FontSize = 12.5,
            FontWeight = FontWeights.Medium
        });
        webClientIdStack.Children.Add(new TextBlock
        {
            Text = "若留空，iPhone 仍可透過「檔案」App 或 AirDrop 載入名片備份檔；若填入則可直接於網頁登入 Google 雲端硬碟同步。",
            FontSize = 11.5,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap
        });
        var webClientIdBox = new TextBox
        {
            PlaceholderText = "例如：123456789-xxxx.apps.googleusercontent.com",
            Text = currentSettings.VercelWebGoogleClientId ?? string.Empty
        };
        webClientIdStack.Children.Add(webClientIdBox);
        step2Stack.Children.Add(webClientIdStack);

        var deployBtn = new Button
        {
            Content = "🚀 一鍵導入專案並發布至 Vercel",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 11, 16, 11),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
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
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 0)
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
            MaxWidth = 600
        };
        statusRow.Children.Add(progressRing);
        statusRow.Children.Add(statusText);
        step2Stack.Children.Add(statusRow);

        var step2Border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = step2Stack
        };
        vercelPanel.Children.Add(step2Border);

        // Step 3: Deployed URL & iPhone Installation QR Code
        var step3Stack = new StackPanel { Spacing = 10 };
        step3Stack.Children.Add(new TextBlock
        {
            Text = "步驟 3：使用 iPhone 相機掃描 QR Code 安裝至主畫面",
            FontWeight = FontWeights.Bold,
            FontSize = 14
        });

        var vercelUrlBox = new TextBox
        {
            Header = "您的個人專屬 Vercel PWA 正式網址",
            IsReadOnly = true,
            Text = currentSettings.LastDeployedVercelUrl ?? string.Empty
        };
        step3Stack.Children.Add(vercelUrlBox);

        var vercelUrlActionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        var vercelCopyUrlBtn = new Button
        {
            Content = "📋 複製網址",
            Padding = new Thickness(12, 7, 12, 7)
        };
        var vercelOpenUrlBtn = new Button
        {
            Content = "🌐 在瀏覽器開啟預覽",
            Padding = new Thickness(12, 7, 12, 7)
        };
        vercelUrlActionsRow.Children.Add(vercelCopyUrlBtn);
        vercelUrlActionsRow.Children.Add(vercelOpenUrlBtn);
        step3Stack.Children.Add(vercelUrlActionsRow);

        var vercelQrImage = new Image
        {
            Width = 220,
            Height = 220,
            HorizontalAlignment = HorizontalAlignment.Center,
            Stretch = Stretch.Uniform
        };
        var vercelQrContainer = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = vercelQrImage
        };
        step3Stack.Children.Add(vercelQrContainer);

        var step3Border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Background = new SolidColorBrush(Color.FromArgb(20, 16, 185, 129)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 16, 185, 129)),
            BorderThickness = new Thickness(1),
            Visibility = string.IsNullOrWhiteSpace(currentSettings.LastDeployedVercelUrl)
                ? Visibility.Collapsed
                : Visibility.Visible,
            Child = step3Stack
        };
        vercelPanel.Children.Add(step3Border);

        rootStack.Children.Add(vercelPanel);

        // --- Mode Switch Logic ---
        void SetMode(bool isGhp)
        {
            if (isGhp)
            {
                ghpPanel.Visibility = Visibility.Visible;
                vercelPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                ghpPanel.Visibility = Visibility.Collapsed;
                vercelPanel.Visibility = Visibility.Visible;
            }
            UpdateButtonStyles(isGhp);
        }

        modeGhpBtn.Click += (_, _) => SetMode(true);
        modeVercelBtn.Click += (_, _) => SetMode(false);

        // --- Initialize GitHub Pages QR Code ---
        await RenderQrCodeAsync(ghpQrImage, ghpUrlBox.Text.Trim());

        ghpRefreshQrBtn.Click += async (_, _) =>
        {
            await RenderQrCodeAsync(ghpQrImage, ghpUrlBox.Text.Trim());
            ghpStatusText.Visibility = Visibility.Visible;
            ghpStatusText.Text = "✅ QR Code 已依最新網址重新產生！";
        };

        ghpCopyBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ghpUrlBox.Text)) return;
            var pkg = new DataPackage();
            pkg.SetText(ghpUrlBox.Text.Trim());
            Clipboard.SetContent(pkg);
            ghpStatusText.Visibility = Visibility.Visible;
            ghpStatusText.Text = "✅ 已複製 GitHub Pages 網址至剪貼簿！";
        };

        ghpOpenBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ghpUrlBox.Text)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ghpUrlBox.Text.Trim(),
                    UseShellExecute = true
                });
            }
            catch { }
        };

        // --- Initialize Vercel QR Code (if previously deployed) ---
        if (!string.IsNullOrWhiteSpace(currentSettings.LastDeployedVercelUrl))
        {
            await RenderQrCodeAsync(vercelQrImage, currentSettings.LastDeployedVercelUrl);
        }

        vercelCopyUrlBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(vercelUrlBox.Text)) return;
            var pkg = new DataPackage();
            pkg.SetText(vercelUrlBox.Text);
            Clipboard.SetContent(pkg);
            statusRow.Visibility = Visibility.Visible;
            statusText.Foreground = new SolidColorBrush(Colors.SeaGreen);
            statusText.Text = "✅ 已複製專屬網址至剪貼簿！";
        };

        vercelOpenUrlBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(vercelUrlBox.Text)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = vercelUrlBox.Text,
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
                vercelUrlBox.Text = result.ProductionUrl;
                step3Border.Visibility = Visibility.Visible;
                settingsService.UpdateVercelSettings(token, proj, webClientId, result.ProductionUrl);
                await RenderQrCodeAsync(vercelQrImage, result.ProductionUrl);
            }
            else
            {
                statusText.Foreground = new SolidColorBrush(Colors.Crimson);
                statusText.Text = $"❌ {result.Message}";
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "📱 iOS / Web 行動版設定與部署 (GitHub Pages / Vercel)",
            Content = rootStack,
            CloseButtonText = "儲存並關閉",
            DefaultButton = ContentDialogButton.None,
            MaxWidth = 720
        };

        dialog.Closing += (_, _) =>
        {
            try { cts?.Cancel(); } catch { }

            // Save GitHub Pages URL
            settingsService.UpdateGitHubPagesUrl(ghpUrlBox.Text.Trim());

            // Save Vercel Settings
            settingsService.UpdateVercelSettings(
                tokenBox.Password,
                deployService.NormalizeProjectName(projectNameBox.Text),
                webClientIdBox.Text,
                string.IsNullOrWhiteSpace(vercelUrlBox.Text) ? null : vercelUrlBox.Text);
        };

        await dialog.ShowAsync();
    }
}
