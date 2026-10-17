using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MajiOCR_Windows;

/// <summary>
/// 提供 Google Cloud Console 憑證申請與 Client ID 設定之 3 步驟圖文教學對話框與嵌入式控制項。
/// </summary>
public static class GoogleGuideDialog
{
    public record GuideStep(
        int StepNumber,
        string Title,
        string ResourceName,
        string FallbackFileName,
        string ShortDescription,
        string DetailedExplanation
    );

    public static readonly GuideStep[] Steps =
    [
        new(
            1,
            "步驟 1：在 Google Cloud 建立「OAuth 用戶端 ID」憑證",
            "MajiOCR.Guide.Step1",
            "step1_create_oauth.png",
            "進入 Google Cloud 憑證頁面，點擊「+ 建立憑證」➔ 選擇「OAuth 用戶端 ID」。",
            "1. 點擊下方「🔗 開啟 Google Cloud 憑證控制台」按鈕進入主控台。\n2. 點擊頂部工具列的「+ 建立憑證」下拉選單。\n3. 在下拉選單中選擇「OAuth 用戶端 ID」（要求使用者同意您的應用程式存取其資料）。\n💡 提示：若尚未啟用「Google People API」，系統會提示您先啟用 API，請點擊啟用即可。"
        ),
        new(
            2,
            "步驟 2：應用程式類型選擇「桌面版應用程式」",
            "MajiOCR.Guide.Step2",
            "step2_select_desktop.png",
            "應用程式類型務必選擇「桌面版應用程式 / 電腦用戶端」，填寫名稱後點擊「建立」。",
            "1. 在「應用程式類型」下拉選單中選擇「桌面版應用程式」（或「電腦用戶端」）。\n2. 「名稱」可自由填寫（例如：電腦用戶端 1、MajiOCR 等）。\n3. 點擊下方的藍色「建立」按鈕完成憑證產生。"
        ),
        new(
            3,
            "步驟 3：複製產生的「用戶端 ID」與「用戶端密碼」並貼回軟體",
            "MajiOCR.Guide.Step3",
            "step3_copy_client_id.png",
            "彈出完成視窗後，複製「用戶端 ID」與「用戶端密碼 (Client Secret)」，回到軟體貼上即可！",
            "1. 建立完成後將跳出「OAuth 用戶端已建立」視窗。\n2. 點擊「用戶端 ID」與「用戶端密碼」右側的 📋 複製圖示，即可將專屬憑證複製至剪貼簿。\n3. 回到本軟體視窗，分別貼入上方 Client ID 與 Client Secret 欄位。\n4. 點擊「🌐 透過瀏覽器登入 Google 帳號」，系統將永久記住您的設定！"
        )
    ];

    private static Style? GetResourceStyle(string key)
    {
        try
        {
            if (Application.Current?.Resources?.ContainsKey(key) == true)
            {
                return Application.Current.Resources[key] as Style;
            }
        }
        catch { }
        return null;
    }

    private static Brush GetResourceBrush(string key, Brush fallback)
    {
        try
        {
            if (Application.Current?.Resources?.ContainsKey(key) == true && Application.Current.Resources[key] is Brush b)
            {
                return b;
            }
        }
        catch { }
        return fallback;
    }

    /// <summary>
    /// 建立可嵌入於面板或 Expander 內的圖文教學檢視控制項。
    /// </summary>
    public static FrameworkElement CreateGuideView(Action? onOpenConsole = null, int imageHeight = 280)
    {
        int currentStepIndex = 0;

        var mainStack = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Step Tab / Indicator Buttons
        var stepIndicatorGrid = new Grid { ColumnSpacing = 6 };
        stepIndicatorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stepIndicatorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stepIndicatorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var stepButtons = new Button[3];
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var btn = new Button
            {
                Content = $"步驟 {i + 1}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 11,
                Padding = new Thickness(4, 4, 4, 4)
            };
            Grid.SetColumn(btn, i);
            stepButtons[i] = btn;
            stepIndicatorGrid.Children.Add(btn);
        }
        mainStack.Children.Add(stepIndicatorGrid);

        // Card Container for Step Content
        var stepCard = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            BorderBrush = GetResourceBrush("CardStrokeColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.LightGray)),
            Background = GetResourceBrush("CardBackgroundFillColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.Transparent)),
            Padding = new Thickness(10)
        };

        var cardContentStack = new StackPanel { Spacing = 8 };

        var stepHeaderGrid = new Grid();
        stepHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stepHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stepTitleText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetResourceBrush("AccentTextFillColorPrimaryBrush", new SolidColorBrush(Microsoft.UI.Colors.DarkBlue)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(stepTitleText, 0);
        stepHeaderGrid.Children.Add(stepTitleText);

        var openExternalImgBtn = new Button
        {
            Content = "🔍 系統看圖開啟",
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        openExternalImgBtn.Click += (s, e) =>
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "GoogleGuide", Steps[currentStepIndex].FallbackFileName);
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
            catch { }
        };
        Grid.SetColumn(openExternalImgBtn, 1);
        stepHeaderGrid.Children.Add(openExternalImgBtn);

        cardContentStack.Children.Add(stepHeaderGrid);

        // Image Container
        var imgBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            BorderBrush = GetResourceBrush("CardStrokeColorDefaultBrush", new SolidColorBrush(Microsoft.UI.Colors.LightGray)),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(20, 128, 128, 128)),
            Height = imageHeight,
            Padding = new Thickness(4)
        };

        var guideImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        imgBorder.Child = guideImage;
        cardContentStack.Children.Add(imgBorder);

        // Step Detailed Explanation
        var stepDescText = new TextBlock
        {
            FontSize = 11,
            LineHeight = 16,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.95
        };
        cardContentStack.Children.Add(stepDescText);

        stepCard.Child = cardContentStack;
        mainStack.Children.Add(stepCard);

        // Navigation & Console Actions
        var navGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var openConsoleBtn = new Button
        {
            Content = "🔗 開啟 Google Cloud 憑證控制台",
            Style = GetResourceStyle("AccentButtonStyle"),
            FontSize = 11,
            Padding = new Thickness(8, 4, 8, 4)
        };
        openConsoleBtn.Click += (s, e) =>
        {
            if (onOpenConsole != null) onOpenConsole();
            else
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://console.cloud.google.com/apis/credentials") { UseShellExecute = true });
                }
                catch { }
            }
        };
        Grid.SetColumn(openConsoleBtn, 0);
        navGrid.Children.Add(openConsoleBtn);

        var stepNavStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        var prevBtn = new Button { Content = "⬅️ 上一步", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
        var nextBtn = new Button { Content = "➡️ 下一步", FontSize = 11, Padding = new Thickness(8, 4, 8, 4) };
        stepNavStack.Children.Add(prevBtn);
        stepNavStack.Children.Add(nextBtn);
        Grid.SetColumn(stepNavStack, 2);
        navGrid.Children.Add(stepNavStack);

        mainStack.Children.Add(navGrid);

        void UpdateStepUI()
        {
            var step = Steps[currentStepIndex];
            stepTitleText.Text = step.Title;
            stepDescText.Text = step.DetailedExplanation;

            for (int i = 0; i < 3; i++)
            {
                if (i == currentStepIndex)
                {
                    stepButtons[i].Style = GetResourceStyle("AccentButtonStyle");
                    stepButtons[i].Content = $"📌 步驟 {i + 1}";
                }
                else
                {
                    stepButtons[i].Style = GetResourceStyle("DefaultButtonStyle");
                    stepButtons[i].Content = $"步驟 {i + 1}";
                }
            }

            prevBtn.IsEnabled = currentStepIndex > 0;
            nextBtn.IsEnabled = currentStepIndex < Steps.Length - 1;

            try
            {
                string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "GoogleGuide", step.FallbackFileName);
                if (File.Exists(localPath))
                {
                    guideImage.Source = new BitmapImage(new Uri(localPath));
                }
                else
                {
                    var asm = typeof(GoogleGuideDialog).Assembly;
                    using var stream = asm.GetManifestResourceStream(step.ResourceName);
                    if (stream != null)
                    {
                        var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        ms.Position = 0;
                        var ras = ms.AsRandomAccessStream();
                        var bmp = new BitmapImage();
                        bmp.SetSource(ras);
                        guideImage.Source = bmp;
                    }
                }
            }
            catch { }
        }

        for (int i = 0; i < 3; i++)
        {
            int targetIdx = i;
            stepButtons[i].Click += (s, e) =>
            {
                currentStepIndex = targetIdx;
                UpdateStepUI();
            };
        }

        prevBtn.Click += (s, e) =>
        {
            if (currentStepIndex > 0)
            {
                currentStepIndex--;
                UpdateStepUI();
            }
        };

        nextBtn.Click += (s, e) =>
        {
            if (currentStepIndex < Steps.Length - 1)
            {
                currentStepIndex++;
                UpdateStepUI();
            }
        };

        UpdateStepUI();
        return mainStack;
    }

    /// <summary>
    /// 彈出獨立教學視窗。
    /// </summary>
    public static async Task ShowAsync(XamlRoot xamlRoot, Action? onOpenConsole = null)
    {
        var mainStack = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 720,
            MinWidth = 580
        };

        var headerStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        headerStack.Children.Add(new FontIcon { Glyph = "\uE896", FontSize = 20, Foreground = GetResourceBrush("AccentTextFillColorPrimaryBrush", new SolidColorBrush(Microsoft.UI.Colors.DarkBlue)) });
        headerStack.Children.Add(new TextBlock
        {
            Text = "Google 帳號授權 Client ID 申請圖文指引",
            FontSize = 16,
            FontWeight = FontWeights.Bold
        });
        mainStack.Children.Add(headerStack);

        var guideView = CreateGuideView(onOpenConsole, imageHeight: 340);
        mainStack.Children.Add(guideView);

        var scrollViewer = new ScrollViewer
        {
            Content = mainStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 700
        };

        var dialog = new ContentDialog
        {
            Content = scrollViewer,
            CloseButtonText = "我知道了，關閉教學",
            XamlRoot = xamlRoot,
            MaxWidth = 760
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1400.0;
        dialog.Resources["ContentDialogMinWidth"] = 600.0;
        dialog.Resources["ContentDialogMaxHeight"] = 1000.0;

        await dialog.ShowAsync();
    }
}
