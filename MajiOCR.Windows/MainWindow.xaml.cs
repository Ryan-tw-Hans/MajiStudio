using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MajiOCR_Windows;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public static MainWindow? CurrentWindow { get; private set; }

    private bool _bypassClosingConfirmation;

    public MainWindow()
    {
        CurrentWindow = this;
        InitializeComponent();
        if (Program.GuiVerificationRoot != null)
        {
            Title = "MajiOCR.Windows — GUI 合成測試";
            AppTitleBar.Title = Title;
        }
        else
        {
            var fileVer = typeof(MainWindow).Assembly.GetName().Version;
            string verSuffix = fileVer != null ? $"{Program.GetAppVersion()} (v{fileVer})" : Program.GetAppVersion();
            Title = $"MajiOCR.Windows {verSuffix}";
            AppTitleBar.Title = Title;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppBranding.Apply(this, AppTitleBar);

        // Handle unsaved draft on window close
        AppWindow.Closing += async (sender, args) =>
        {
            if (_bypassClosingConfirmation) return;

            if (RootFrame.Content is MainPage page)
            {
                if (page.ViewModel.IsSaving || page.ViewModel.IsBusy || page.CurrentActiveDialog != null)
                {
                    args.Cancel = true;
                    return;
                }

                if (page.ViewModel.HasUnsavedChanges)
                {
                    args.Cancel = true;
                    bool proceed = await page.ShowConfirmDialogAsync(
                        "確定要關閉程式嗎？",
                        "目前名片草稿包含未保存的修改內容。關閉視窗後，本工作階段的所有內容都將遺失，且無法復原。是否確定關閉？"
                    );
                    if (proceed)
                    {
                        _bypassClosingConfirmation = true;
                        Close();
                    }
                }
            }
        };

        // Constrain page maximum width to RootFrame actual width on window resize
        RootFrame.SizeChanged += (sender, args) =>
        {
            if (RootFrame.Content is MainPage page && args.NewSize.Width > 0)
            {
                page.MaxWidth = args.NewSize.Width;
            }
        };

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }
}
