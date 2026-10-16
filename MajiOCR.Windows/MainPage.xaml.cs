using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services;
using MajiOCR_Windows.Services.Update;
using MajiOCR_Windows.ViewModels;

namespace MajiOCR_Windows;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; } = new(new CustomFieldCatalogService(
        Program.GuiVerificationRoot is string root ? System.IO.Path.Combine(root, "custom-fields.json") : null));

    private readonly UpdateService _updateService = new();

    // Pan state
    private bool _isPanning = false;
    private Point _panStartPoint;
    private double _initialPanX;
    private double _initialPanY;

    // ROI drawing state
    private bool _isDrawingRoi = false;
    private Point _roiStartPoint;

    private bool _suppressComboSelection = false;

    public MainPage()
    {
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.DraftRows.CollectionChanged += (s, e) => UpdateDraftStatusUI();

        UpdateTargetHighlightUI();
        Loaded += async (s, e) =>
        {
            if (ViewModel.StorageService == null && !ViewModel.IsStorageInitializing)
            {
                StorageInitializationTask = InitializeStorageAsync();
                await StorageInitializationTask;
            }
        };
    }

    public Task StorageInitializationTask { get; private set; } = Task.CompletedTask;

    private async Task InitializeStorageAsync()
    {
        if (ViewModel.StorageService != null) return;

        ViewModel.IsStorageInitializing = true;
        MajiOCR_Windows.Services.Storage.StorageService? service = null;
        try
        {
            string storageRoot = Program.GuiVerificationRoot is string root ? System.IO.Path.Combine(root, "Storage") : MajiOCR_Windows.Services.Storage.StoragePathHelper.GetDefaultStorageRoot();
            string dbPath = System.IO.Path.Combine(storageRoot, MajiOCR_Windows.Services.Storage.StoragePathHelper.DatabaseFileName);

            var settings = ViewModel.SettingsService.LoadSettings();
            string? passphrase = null;

            if (settings.IsPasswordProtected || MajiOCR_Windows.Services.Storage.StorageService.IsDatabaseEncrypted(dbPath))
            {
                if (XamlRoot != null)
                {
                    passphrase = await SecurityDialogs.ShowUnlockDialogAsync(XamlRoot, dbPath, ViewModel.SettingsService);
                    if (passphrase == null)
                    {
                        MainWindow.CurrentWindow?.Close();
                        Application.Current.Exit();
                        return;
                    }
                }
            }

            service = await Task.Run(() => new MajiOCR_Windows.Services.Storage.StorageService(
                Program.GuiVerificationRoot is string r ? System.IO.Path.Combine(r, "Storage") : null,
                autoInit: true,
                passphrase: passphrase));

            if (service.IsAvailable)
                await service.RecoverPendingOperationsAsync();
            ViewModel.StorageService = service;
            if (!service.IsAvailable)
            {
                ViewModel.StatusMessage = $"本機資料庫不可用：{service.ErrorMessage}";
            }
            else
            {
                ViewModel.CatalogService.AttachStorage(service);
                _ = TriggerAutoUpdateCheckAsync();
            }
        }
        catch (Exception ex)
        {
            if (service != null) await Task.Run(() => service.Dispose());
            ViewModel.StorageService = null;
            ViewModel.StatusMessage = MajiOCR_Windows.Services.Storage.StorageService.FormatStorageError("DB_STARTUP", ex);
        }
        finally
        {
            ViewModel.IsStorageInitializing = false;
        }
    }

    private async Task TriggerAutoUpdateCheckAsync()
    {
        // 延遲 1.5 秒讓 UI 完全載入與渲染，不與初次啟動搶佔資源
        await Task.Delay(1500);

        try
        {
            var settings = _updateService.LoadSettings();
            var manifestUrl = !string.IsNullOrWhiteSpace(settings.ReleaseManifestUrl)
                ? settings.ReleaseManifestUrl
                : (!string.IsNullOrWhiteSpace(settings.CustomManifestUrl)
                    ? settings.CustomManifestUrl
                    : _updateService.DefaultReleaseManifestUrl);

            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                return;
            }

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            var checkResult = await _updateService.CheckAsync(manifestUrl, UpdateChannel.Release, cts.Token);

            if (checkResult.IsAvailable && XamlRoot != null && _currentActiveDialog == null)
            {
                var promptDialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "📢 發現軟體新版本！",
                    Content = new StackPanel
                    {
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = $"系統偵測到有新版本可供更新：v{checkResult.Manifest.Version}（目前版本：v{_updateService.CurrentVersion}）",
                                TextWrapping = TextWrapping.Wrap,
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                            },
                            new TextBlock
                            {
                                Text = !string.IsNullOrWhiteSpace(checkResult.Manifest.Description)
                                    ? checkResult.Manifest.Description
                                    : (checkResult.Reason ?? "建議立即更新以獲取最新功能與穩定性修正。"),
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = new SolidColorBrush(Microsoft.UI.Colors.DarkSlateGray)
                            }
                        }
                    },
                    PrimaryButtonText = "🚀 立即查看並更新",
                    CloseButtonText = "稍後提醒",
                    DefaultButton = ContentDialogButton.Primary
                };

                _currentActiveDialog = promptDialog;
                ContentDialogResult dialogResult;
                try
                {
                    dialogResult = await promptDialog.ShowAsync();
                }
                finally
                {
                    _currentActiveDialog = null;
                }

                if (dialogResult == ContentDialogResult.Primary && XamlRoot != null)
                {
                    await UpdateManagementDialog.ShowDialogAsync(this.XamlRoot, _updateService, onBeforeExitForUpdate: () =>
                    {
                    });
                }
            }
        }
        catch
        {
            // 自動背景檢查失敗（例如離線或超時）完全靜默，不干擾使用者正常離線作業
        }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        double height = e.NewSize.Height;

        // Dynamic CommandBar overflow label positioning
        if (TopCommandBar != null)
        {
            TopCommandBar.DefaultLabelPosition = width < 680
                ? CommandBarDefaultLabelPosition.Collapsed
                : CommandBarDefaultLabelPosition.Right;
        }

        if (RootPageGrid != null)
        {
            RootPageGrid.MaxWidth = width;
        }

        if (width < 820)
        {
            // Stack vertically in narrow windows with outer vertical scrolling
            LeftColumnDef.Width = new GridLength(1, GridUnitType.Star);
            LeftColumnDef.MinWidth = 0;
            RightColumnDef.Width = new GridLength(0, GridUnitType.Pixel);
            RightColumnDef.MinWidth = 0;

            TopRowDef.Height = GridLength.Auto;
            BottomRowDef.Height = GridLength.Auto;

            Grid.SetRow(LeftColumnScrollViewer, 0);
            Grid.SetColumn(LeftColumnScrollViewer, 0);

            Grid.SetRow(RightColumnPanel, 1);
            Grid.SetColumn(RightColumnPanel, 0);

            // In narrow mode, provide image viewport with fixed comfortable height
            ImageBorder.Height = 340;

            // Enable outer ScrollViewer so the entire page (image + right column) scrolls naturally
            if (MainContentScrollViewer != null)
            {
                MainContentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                MainContentScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
                MainContentScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                MainContentScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
            }

            // Disable inner scrollviewers to avoid nested scroll conflicts
            if (LeftColumnScrollViewer != null)
            {
                LeftColumnScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                LeftColumnScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
            }

            if (DraftScrollViewer != null)
            {
                DraftScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                DraftScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
            }
        }
        else
        {
            // Side-by-side in wide windows
            double rightColumnWidth = width >= 1100 ? 420 : 380;
            LeftColumnDef.Width = new GridLength(1, GridUnitType.Star);
            LeftColumnDef.MinWidth = 280;
            RightColumnDef.Width = new GridLength(rightColumnWidth, GridUnitType.Pixel);
            RightColumnDef.MinWidth = 280;

            TopRowDef.Height = new GridLength(1, GridUnitType.Star);
            BottomRowDef.Height = new GridLength(0, GridUnitType.Pixel);

            Grid.SetRow(LeftColumnScrollViewer, 0);
            Grid.SetColumn(LeftColumnScrollViewer, 0);

            Grid.SetRow(RightColumnPanel, 0);
            Grid.SetColumn(RightColumnPanel, 1);

            // Disable outer ScrollViewer in wide mode
            if (MainContentScrollViewer != null)
            {
                MainContentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                MainContentScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
                MainContentScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                MainContentScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
            }

            // Enable inner draft ScrollViewer for the right column
            if (DraftScrollViewer != null)
            {
                DraftScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                DraftScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
            }

            // Check if window height in DIP is short (height < 600 DIP, e.g. 1152x592 at 125% DPI)
            bool isShortHeight = height > 0 && height < 600;
            if (isShortHeight)
            {
                // In short-height wide windows, give image fixed comfortable height (280 DIP)
                // and enable LeftColumnScrollViewer so image, zoom bar, and coordinates are fully reachable
                ImageBorder.Height = 280;

                if (LeftColumnScrollViewer != null)
                {
                    LeftColumnScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    LeftColumnScrollViewer.VerticalScrollMode = ScrollMode.Enabled;
                }
            }
            else
            {
                // In tall wide windows, image viewport fills remaining vertical space
                ImageBorder.Height = double.NaN;

                if (LeftColumnScrollViewer != null)
                {
                    LeftColumnScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    LeftColumnScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
                }
            }
        }

    }

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WorkspaceCanvas.ActualWidth > 0 && WorkspaceCanvas.ActualHeight > 0)
        {
            WorkspaceCanvas.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, WorkspaceCanvas.ActualWidth, WorkspaceCanvas.ActualHeight)
            };

            if (NoImagePlaceholder != null)
            {
                double phWidth = NoImagePlaceholder.ActualWidth > 0 ? NoImagePlaceholder.ActualWidth : 320;
                double phHeight = NoImagePlaceholder.ActualHeight > 0 ? NoImagePlaceholder.ActualHeight : 100;
                double left = Math.Max(10, (WorkspaceCanvas.ActualWidth - phWidth) / 2);
                double top = Math.Max(10, (WorkspaceCanvas.ActualHeight - phHeight) / 2);
                Canvas.SetLeft(NoImagePlaceholder, left);
                Canvas.SetTop(NoImagePlaceholder, top);
            }
        }

        ViewModel.ViewportWidth = WorkspaceCanvas.ActualWidth;
        ViewModel.ViewportHeight = WorkspaceCanvas.ActualHeight;

        if (ViewModel.IsFitMode && ViewModel.HasImage)
        {
            ViewModel.SetFitToWindow();
        }
        UpdateImageDisplay();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModel.DisplayImage):
                    PreviewImage.Source = ViewModel.DisplayImage;
                    PreviewImage.Visibility = ViewModel.HasImage ? Visibility.Visible : Visibility.Collapsed;
                    NoImagePlaceholder.Visibility = ViewModel.HasImage ? Visibility.Collapsed : Visibility.Visible;
                    if (!ViewModel.HasImage)
                    {
                        RegionCoordsDisplayText.Text = "選區: 未框選 (在圖片上拖曳滑鼠)";
                    }
                    UpdateImageDisplay();
                    break;

                case nameof(ViewModel.IsBusy):
                    LoadingOverlay.Visibility = ViewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
                    CancelOcrButton.Visibility = ViewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
                    PickImageButton.IsEnabled = ViewModel.CanRunActions;
                    PasteImageButton.IsEnabled = ViewModel.CanRunActions;
                    FullOcrButton.IsEnabled = ViewModel.CanRunActions;
                    RegionOcrButton.IsEnabled = ViewModel.CanRunActions;
                    BlankDraftButton.IsEnabled = ViewModel.CanRunActions;
                    break;

                case nameof(ViewModel.BusyMessage):
                    LoadingMessageText.Text = ViewModel.BusyMessage;
                    break;

                case nameof(ViewModel.RegionCoordsText):
                    RegionCoordsDisplayText.Text = (ViewModel.RegionWidth > 0 && ViewModel.RegionHeight > 0)
                        ? ViewModel.RegionCoordsText
                        : "選區: 未框選 (在圖片上拖曳滑鼠)";
                    UpdateRoiRectangleFromViewModel();
                    break;

                case nameof(ViewModel.ZoomScale):
                case nameof(ViewModel.PanX):
                case nameof(ViewModel.PanY):
                case nameof(ViewModel.IsFitMode):
                    UpdateImageDisplay();
                    break;

                case nameof(ViewModel.SelectedTargetField):
                case nameof(ViewModel.IsTargetFieldSelected):
                    UpdateTargetHighlightUI();
                    break;

                case nameof(ViewModel.HasUnsavedChanges):
                case nameof(ViewModel.DraftRowsCountText):
                    UpdateDraftStatusUI();
                    break;

                case nameof(ViewModel.ClassificationSummary):
                    if (ClassificationSummaryText != null)
                    {
                        ClassificationSummaryText.Text = ViewModel.ClassificationSummary;
                    }
                    break;
            }
        });
    }

    private void UpdateDraftStatusUI()
    {
        DraftRowsCountLabel.Text = $"{ViewModel.DraftRows.Count} 筆草稿";

        if (ViewModel.HasUnsavedChanges)
        {
            DraftStatusBadge.Text = "已修改";
            DraftStatusBadge.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
            DraftStatusBadgeBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 0, 120, 212));
        }
        else
        {
            DraftStatusBadge.Text = "未修改";
            DraftStatusBadge.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
            DraftStatusBadgeBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 128, 128, 128));
        }
    }

    private void UpdateTargetHighlightUI()
    {
        var target = ViewModel.SelectedTargetField;

        UpdateBtnHighlight(NameRoiBtn, target != null && ReferenceEquals(target, ViewModel.NameRow), "姓名");
        UpdateBtnHighlight(CompanyRoiBtn, target != null && ReferenceEquals(target, ViewModel.CompanyRow), "公司");
        UpdateBtnHighlight(TitleRoiBtn, target != null && ReferenceEquals(target, ViewModel.TitleRow), "職稱");
        UpdateBtnHighlight(WebsiteRoiBtn, target != null && ReferenceEquals(target, ViewModel.WebsiteRow), "網站");
        UpdateBtnHighlight(AddressRoiBtn, target != null && ReferenceEquals(target, ViewModel.AddressRow), "地址");
        UpdateBtnHighlight(NoteRoiBtn, target != null && ReferenceEquals(target, ViewModel.NoteRow), "備註");
    }

    private void UpdateBtnHighlight(Button btn, bool isSelected, string fieldName)
    {
        if (btn == null) return;
        if (isSelected)
        {
            btn.Content = "框選中…";
            btn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }
        else
        {
            btn.Content = "局部辨識";
            btn.ClearValue(Button.StyleProperty);
        }
    }

    // --- Image Workspace Rendering & Coordinates ---

    private void UpdateImageDisplay()
    {
        if (ViewModel.CurrentSession == null || WorkspaceCanvas.ActualWidth <= 0 || WorkspaceCanvas.ActualHeight <= 0)
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            RoiRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        var (imgLeft, imgTop, imgW, imgH) = ImageTransformHelper.GetRenderedImageRect(
            WorkspaceCanvas.ActualWidth,
            WorkspaceCanvas.ActualHeight,
            ViewModel.CurrentSession.Width,
            ViewModel.CurrentSession.Height,
            ViewModel.ZoomScale,
            ViewModel.PanX,
            ViewModel.PanY);

        Canvas.SetLeft(PreviewImage, imgLeft);
        Canvas.SetTop(PreviewImage, imgTop);
        PreviewImage.Width = Math.Max(0, imgW);
        PreviewImage.Height = Math.Max(0, imgH);
        PreviewImage.Visibility = Visibility.Visible;

        UpdateRoiRectangleFromViewModel();
    }

    private void UpdateRoiRectangleFromViewModel()
    {
        if (ViewModel.CurrentSession == null || WorkspaceCanvas.ActualWidth <= 0 || WorkspaceCanvas.ActualHeight <= 0)
        {
            RoiRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        if (ViewModel.RegionWidth >= 10 && ViewModel.RegionHeight >= 10)
        {
            var (vx, vy, vw, vh) = ImageTransformHelper.ImagePixelRoiToVisualRoi(
                ViewModel.RegionX,
                ViewModel.RegionY,
                ViewModel.RegionWidth,
                ViewModel.RegionHeight,
                WorkspaceCanvas.ActualWidth,
                WorkspaceCanvas.ActualHeight,
                ViewModel.CurrentSession.Width,
                ViewModel.CurrentSession.Height,
                ViewModel.ZoomScale,
                ViewModel.PanX,
                ViewModel.PanY);

            Canvas.SetLeft(RoiRectangle, vx);
            Canvas.SetTop(RoiRectangle, vy);
            RoiRectangle.Width = Math.Max(0, vw);
            RoiRectangle.Height = Math.Max(0, vh);
            RoiRectangle.Visibility = Visibility.Visible;
        }
        else
        {
            RoiRectangle.Visibility = Visibility.Collapsed;
        }
    }

    // --- Image Workspace Pointer and Keyboard Interaction ---

    private void OnWorkspacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(WorkspaceCanvas);
        // Only primary/left mouse button may begin ROI or Ctrl+left pan (RS-ISSUE-0008 follow-up)
        if (!ImageTransformHelper.ShouldAcceptPointerPress(pt.Properties.IsLeftButtonPressed, ViewModel.HasImage))
        {
            return;
        }

        WorkspaceCanvas.Focus(FocusState.Programmatic);
        var modifiers = e.KeyModifiers;

        if (modifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            // Ctrl + Drag: Panning mode (RS-ISSUE-0008 OCR8-07)
            _isPanning = true;
            _panStartPoint = pt.Position;
            _initialPanX = ViewModel.PanX;
            _initialPanY = ViewModel.PanY;
            WorkspaceCanvas.CapturePointer(e.Pointer);
        }
        else
        {
            // Plain Drag: ROI selection mode
            _isDrawingRoi = true;
            _roiStartPoint = pt.Position;
            WorkspaceCanvas.CapturePointer(e.Pointer);

            RoiRectangle.Visibility = Visibility.Visible;
            Canvas.SetLeft(RoiRectangle, _roiStartPoint.X);
            Canvas.SetTop(RoiRectangle, _roiStartPoint.Y);
            RoiRectangle.Width = 0;
            RoiRectangle.Height = 0;
        }
    }

    private void OnWorkspacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.CurrentSession == null) return;
        var pt = e.GetCurrentPoint(WorkspaceCanvas);

        if (_isPanning)
        {
            double deltaX = pt.Position.X - _panStartPoint.X;
            double deltaY = pt.Position.Y - _panStartPoint.Y;
            ViewModel.SetPan(_initialPanX + deltaX, _initialPanY + deltaY);
            UpdateImageDisplay();
        }
        else if (_isDrawingRoi)
        {
            var cur = pt.Position;
            double x1 = Math.Min(_roiStartPoint.X, cur.X);
            double y1 = Math.Min(_roiStartPoint.Y, cur.Y);
            double w = Math.Abs(cur.X - _roiStartPoint.X);
            double h = Math.Abs(cur.Y - _roiStartPoint.Y);

            Canvas.SetLeft(RoiRectangle, x1);
            Canvas.SetTop(RoiRectangle, y1);
            RoiRectangle.Width = w;
            RoiRectangle.Height = h;
            RoiRectangle.Visibility = Visibility.Visible;
        }
    }

    private void OnWorkspacePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        ResetPointerInteraction(e.Pointer);
    }

    private void OnWorkspacePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ResetPointerInteraction(e.Pointer);
    }

    private void ResetPointerInteraction(Microsoft.UI.Xaml.Input.Pointer pointer)
    {
        bool wasDrawing = _isDrawingRoi;
        _isPanning = false;
        _isDrawingRoi = false;

        try
        {
            WorkspaceCanvas.ReleasePointerCapture(pointer);
        }
        catch
        {
            // Best effort release
        }

        // Interrupted gestures must never leave a stale selection box or trigger OCR
        if (wasDrawing)
        {
            UpdateRoiRectangleFromViewModel();
        }
    }

    private async void OnWorkspacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            try { WorkspaceCanvas.ReleasePointerCapture(e.Pointer); } catch { }
            return;
        }

        if (!_isDrawingRoi)
        {
            return;
        }

        _isDrawingRoi = false;
        try { WorkspaceCanvas.ReleasePointerCapture(e.Pointer); } catch { }

            if (ViewModel.CurrentSession == null) return;

            double vx = Canvas.GetLeft(RoiRectangle);
            double vy = Canvas.GetTop(RoiRectangle);
            double vw = RoiRectangle.Width;
            double vh = RoiRectangle.Height;

            bool validRoi = ImageTransformHelper.VisualRoiToImagePixelRoi(
                vx, vy, vw, vh,
                WorkspaceCanvas.ActualWidth,
                WorkspaceCanvas.ActualHeight,
                ViewModel.CurrentSession.Width,
                ViewModel.CurrentSession.Height,
                ViewModel.ZoomScale,
                ViewModel.PanX,
                ViewModel.PanY,
                out int origX, out int origY, out int origW, out int origH);

            if (validRoi && origW >= 10 && origH >= 10)
            {
                ViewModel.SetRegionCoordinates(origX, origY, origW, origH);
                UpdateRoiRectangleFromViewModel();

                // If user selected a target field first: automatically run targeted ROI OCR!
                if (ViewModel.SelectedTargetField != null)
                {
                    var target = ViewModel.SelectedTargetField;
                    var err = await ViewModel.RunTargetedRoiOcrAsync(target);
                    if (err != null)
                    {
                        await ShowErrorDialogAsync("局部辨識提示", err);
                    }
                    UpdateTargetHighlightUI();
                }
                // If no target field selected: plain drag only retains ROI box without starting OCR (OCR8-07)
            }
            else
            {
                // Invalid or out-of-bounds selection
                if (ViewModel.SelectedTargetField != null)
                {
                    ViewModel.StatusMessage = "選框過小或落在留白區域，未執行局部辨識。請重新框選名片文字。";
                }
                RoiRectangle.Visibility = Visibility.Collapsed;
            }
    }

    private void OnWorkspacePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // RS-ISSUE-0009 OCR9-01: Only when pointer is in workspace canvas, image exists, and Ctrl is held down
        if (!ViewModel.HasImage)
        {
            return;
        }

        var modifiers = e.KeyModifiers;
        if (!modifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            // Normal wheel scrolls page / parent container, do not intercept
            return;
        }

        var pt = e.GetCurrentPoint(WorkspaceCanvas);
        int delta = pt.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        // Mark handled so outer ScrollViewer does not scroll
        e.Handled = true;

        // OCR9-02: Visual anchor zoom at pointer location
        ViewModel.ZoomAtPoint(delta, pt.Position.X, pt.Position.Y);
        UpdateImageDisplay();
    }

    private void OnWorkspaceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Arrow keys pan only when image preview has focus; text inputs retain normal navigation
        const double panStep = 25.0;

        switch (e.Key)
        {
            case VirtualKey.Left:
                ViewModel.Pan(-panStep, 0);
                UpdateImageDisplay();
                e.Handled = true;
                break;

            case VirtualKey.Right:
                ViewModel.Pan(panStep, 0);
                UpdateImageDisplay();
                e.Handled = true;
                break;

            case VirtualKey.Up:
                ViewModel.Pan(0, -panStep);
                UpdateImageDisplay();
                e.Handled = true;
                break;

            case VirtualKey.Down:
                ViewModel.Pan(0, panStep);
                UpdateImageDisplay();
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                // Esc cancels target field selection and ROI box
                ViewModel.ClearTargetFieldSelection();
                RoiRectangle.Visibility = Visibility.Collapsed;
                UpdateTargetHighlightUI();
                ViewModel.StatusMessage = "已取消局部辨識目標欄位選取。";
                e.Handled = true;
                break;
        }
    }

    // --- Zoom Controls ---

    private void OnFitToWindowClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.SetFitToWindow();
        UpdateImageDisplay();
    }

    // --- Fixed Field ROI Target Selection ---

    private void OnNameRoiClicked(object sender, RoutedEventArgs e) => OnFieldRoiTargetClicked(ViewModel.NameRow);
    private void OnCompanyRoiClicked(object sender, RoutedEventArgs e) => OnFieldRoiTargetClicked(ViewModel.CompanyRow);
    private void OnTitleRoiClicked(object sender, RoutedEventArgs e) => OnFieldRoiTargetClicked(ViewModel.TitleRow);
    private void OnWebsiteRoiClicked(object sender, RoutedEventArgs e) => OnFieldRoiTargetClicked(ViewModel.WebsiteRow);
    private void OnAddressRoiClicked(object sender, RoutedEventArgs e) => OnFieldRoiTargetClicked(ViewModel.AddressRow);
    private void OnNoteRoiClicked(object sender, RoutedEventArgs e) => OnFieldRoiTargetClicked(ViewModel.NoteRow);

    private void OnRowRoiClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DraftRowItem row)
        {
            OnFieldRoiTargetClicked(row);
        }
    }

    private void OnFieldRoiTargetClicked(DraftRowItem row)
    {
        ViewModel.ToggleTargetField(row);
        UpdateTargetHighlightUI();
    }

    // --- Fixed Field Undo ---

    private void OnNameUndoClicked(object sender, RoutedEventArgs e) => ViewModel.UndoField(ViewModel.NameRow);
    private void OnCompanyUndoClicked(object sender, RoutedEventArgs e) => ViewModel.UndoField(ViewModel.CompanyRow);
    private void OnTitleUndoClicked(object sender, RoutedEventArgs e) => ViewModel.UndoField(ViewModel.TitleRow);
    private void OnWebsiteUndoClicked(object sender, RoutedEventArgs e) => ViewModel.UndoField(ViewModel.WebsiteRow);
    private void OnAddressUndoClicked(object sender, RoutedEventArgs e) => ViewModel.UndoField(ViewModel.AddressRow);
    private void OnNoteUndoClicked(object sender, RoutedEventArgs e) => ViewModel.UndoField(ViewModel.NoteRow);

    private void OnRowUndoClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DraftRowItem row)
        {
            ViewModel.UndoField(row);
        }
    }

    private async void OnConfirmAllFieldsClicked(object sender, RoutedEventArgs e)
    {
        if (_currentActiveDialog != null) return;
        ViewModel.ConfirmAllDraftRows(out string? error);
        if (error != null)
            await ShowErrorDialogAsync("全部欄位確認", error);
        UpdateDraftStatusUI();
    }

    // --- Fixed Field Confirm ---

    private async void OnNameConfirmClicked(object sender, RoutedEventArgs e) => await ConfirmRowAsync(ViewModel.NameRow);
    private async void OnCompanyConfirmClicked(object sender, RoutedEventArgs e) => await ConfirmRowAsync(ViewModel.CompanyRow);
    private async void OnTitleConfirmClicked(object sender, RoutedEventArgs e) => await ConfirmRowAsync(ViewModel.TitleRow);
    private async void OnWebsiteConfirmClicked(object sender, RoutedEventArgs e) => await ConfirmRowAsync(ViewModel.WebsiteRow);
    private async void OnAddressConfirmClicked(object sender, RoutedEventArgs e) => await ConfirmRowAsync(ViewModel.AddressRow);
    private async void OnNoteConfirmClicked(object sender, RoutedEventArgs e) => await ConfirmRowAsync(ViewModel.NoteRow);

    private async Task ConfirmRowAsync(DraftRowItem row)
    {
        var conflictRow = ViewModel.CheckSingleValueConflict(row, row.ValueKind);
        if (conflictRow != null)
        {
            string kindName = CanonicalValueKind.GetDisplayName(row.ValueKind, row.CustomFieldNameSnapshot);
            var resolution = await ShowConflictResolutionDialogAsync(kindName, conflictRow.Value, row.Value);
            if (resolution == "Cancel") return;
            if (resolution == "Replace")
            {
                conflictRow.Unconfirm();
                conflictRow.MarkModified();
            }
        }
        ViewModel.ConfirmDraftRow(row);
    }

    // --- Row ComboBox Handling (Custom & Unclassified) ---

    private void OnRowCategoryComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox cb && cb.Tag is DraftRowItem row)
        {
            _suppressComboSelection = true;
            cb.ItemsSource = row.AvailableOptions;
            cb.SelectedItem = row.SelectedOption;
            _suppressComboSelection = false;
        }
    }

    private async void OnRowCategoryComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboSelection) return;
        if (sender is not ComboBox cb || cb.Tag is not DraftRowItem row) return;
        if (cb.SelectedItem is not DraftFieldOption selectedOpt) return;

        if (selectedOpt.IsActionItem)
        {
            _suppressComboSelection = true;
            RestoreComboBoxSelection(cb, row);
            _suppressComboSelection = false;

            string? newName = await ShowTextInputDialogAsync("新增自訂名稱", "請輸入自訂欄位名稱 (例如：統編、部門、LINE ID):");
            if (string.IsNullOrWhiteSpace(newName)) return;

            if (!CustomFieldCatalogService.ValidateName(newName, out var sanitizedName, out var errMsg))
            {
                await ShowErrorDialogAsync("名稱驗證失敗", errMsg ?? "自訂名稱不符規則。");
                return;
            }

            try
            {
                var newDef = ViewModel.CatalogService.AddDefinition(sanitizedName);
                ViewModel.RefreshFieldOptions();

                row.ValueKind = CanonicalValueKind.Custom;
                row.CustomFieldDefinitionId = newDef.Id;
                row.CustomFieldNameSnapshot = newDef.Name;
                row.SyncOptions(ViewModel.CatalogService.GetActiveDefinitions(), DraftFieldOption.CreateAction("+ 新增自訂名稱…"));
                row.MarkModified();

                _suppressComboSelection = true;
                RestoreComboBoxSelection(cb, row);
                _suppressComboSelection = false;

                ViewModel.StatusMessage = $"已成功新增自訂名稱「{newDef.Name}」，並套用至目前草稿列。";
                ViewModel.SyncProjections();
            }
            catch (Exception ex)
            {
                await ShowErrorDialogAsync("新增自訂名稱失敗", ex.Message);
            }
            return;
        }

        string targetKind = selectedOpt.ValueKind;
        string? targetDefId = selectedOpt.CustomFieldDefinitionId;
        string? targetNameSnap = selectedOpt.CustomFieldName;

        var conflictRow = ViewModel.CheckSingleValueConflict(row, targetKind);
        if (conflictRow != null)
        {
            string kindName = CanonicalValueKind.GetDisplayName(targetKind, targetNameSnap);
            var resolution = await ShowConflictResolutionDialogAsync(kindName, conflictRow.Value, row.Value);
            if (resolution == "Cancel")
            {
                _suppressComboSelection = true;
                RestoreComboBoxSelection(cb, row);
                _suppressComboSelection = false;
                return;
            }
            else if (resolution == "Replace")
            {
                conflictRow.Unconfirm();
                conflictRow.MarkModified();
            }
        }

        row.ValueKind = targetKind;
        row.CustomFieldDefinitionId = targetDefId;
        row.CustomFieldNameSnapshot = targetNameSnap;
        row.SyncOptions(ViewModel.CatalogService.GetActiveDefinitions(), DraftFieldOption.CreateAction("+ 新增自訂名稱…"));
        row.MarkModified();

        _suppressComboSelection = true;
        RestoreComboBoxSelection(cb, row);
        _suppressComboSelection = false;

        ViewModel.SyncProjections();
    }

    private void RestoreComboBoxSelection(ComboBox cb, DraftRowItem row)
    {
        cb.ItemsSource = row.AvailableOptions;
        cb.SelectedItem = row.SelectedOption;
    }

    private void OnFixedFieldValueChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is DraftRowItem row)
        {
            if (!string.Equals(row.Value, tb.Text, StringComparison.Ordinal))
            {
                row.Value = tb.Text;
                row.MarkModified();
                ViewModel.EnsureRowInDraft(row);
            }
        }
    }

    private void OnDraftRowValueChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is DraftRowItem row)
        {
            if (!string.Equals(row.Value, tb.Text, StringComparison.Ordinal))
            {
                row.Value = tb.Text;
                row.MarkModified();
                ViewModel.EnsureRowInDraft(row);
            }
        }
    }

    private async void OnConfirmDraftRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DraftRowItem row)
        {
            await ConfirmRowAsync(row);
        }
    }

    private void OnDeleteDraftRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DraftRowItem row)
        {
            ViewModel.RemoveDraftRow(row);
        }
    }

    private void OnDeletePhoneRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DraftRowItem row)
        {
            ViewModel.RemoveDraftRow(row);
        }
    }

    private void OnDeleteEmailRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DraftRowItem row)
        {
            ViewModel.RemoveDraftRow(row);
        }
    }

    private void OnAddPhoneClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.AddPhoneRow();
    }

    private void OnAddEmailClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.AddEmailRow();
    }

    private void OnAddCustomFieldClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.AddCustomRow();
    }

    private void OnApplySuggestionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DraftRowItem row)
        {
            row.ApplySuggestion();
            ViewModel.EnsureRowInDraft(row);
        }
    }

    private async void OnManageCustomFieldsClicked(object sender, RoutedEventArgs e)
    {
        var controller = new CustomFieldsManagementController(ViewModel.CatalogService);
        if (!controller.TryLoadAllDefinitions(out var allDefs, out var loadError))
        {
            await ShowErrorDialogAsync("管理自訂名稱", loadError ?? "無法載入自訂名稱設定。");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "管理自訂名稱清單",
            PrimaryButtonText = "關閉",
            XamlRoot = this.XamlRoot
        };

        var panel = new StackPanel { Spacing = 12, Width = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "停用後將從下拉選單隱藏，既有草稿資料不受影響。改名時亦保留歷史快照。",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        if (allDefs.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "目前尚未建立任何自訂名稱。",
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                Margin = new Thickness(0, 10, 0, 10)
            });
        }
        else
        {
            var scroll = new ScrollViewer { MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var listStack = new StackPanel { Spacing = 8 };

            foreach (var def in allDefs)
            {
                var rowGrid = new Grid
                {
                    ColumnSpacing = 8,
                    Margin = new Thickness(0, 4, 0, 4)
                };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameText = new TextBlock
                {
                    Text = def.Name,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium
                };
                Grid.SetColumn(nameText, 0);
                rowGrid.Children.Add(nameText);

                bool isReverting = false;
                var activeBox = new CheckBox
                {
                    Content = "啟用",
                    IsChecked = def.Active,
                    VerticalAlignment = VerticalAlignment.Center
                };
                activeBox.Checked += (s, ev) =>
                {
                    if (isReverting) return;
                    if (controller.TrySetDefinitionActive(def.Id, true, out var err))
                    {
                        def.Active = true;
                        ViewModel.RefreshFieldOptions();
                    }
                    else
                    {
                        isReverting = true;
                        try { activeBox.IsChecked = false; } finally { isReverting = false; }
                        _ = ShowErrorDialogAsync("設定失敗", err ?? "無法更新啟用狀態。");
                    }
                };
                activeBox.Unchecked += (s, ev) =>
                {
                    if (isReverting) return;
                    if (controller.TrySetDefinitionActive(def.Id, false, out var err))
                    {
                        def.Active = false;
                        ViewModel.RefreshFieldOptions();
                    }
                    else
                    {
                        isReverting = true;
                        try { activeBox.IsChecked = true; } finally { isReverting = false; }
                        _ = ShowErrorDialogAsync("設定失敗", err ?? "無法更新啟用狀態。");
                    }
                };
                Grid.SetColumn(activeBox, 1);
                rowGrid.Children.Add(activeBox);

                var renameBtn = new Button
                {
                    Content = "重新命名",
                    FontSize = 11,
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                renameBtn.Click += async (s, ev) =>
                {
                    string? newName = await ShowTextInputDialogAsync("重新命名自訂欄位", $"請輸入「{def.Name}」的新名稱:");
                    if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName.Trim(), def.Name, StringComparison.Ordinal))
                    {
                        if (controller.TryRenameDefinition(def.Id, newName, out var err))
                        {
                            def.Name = newName.Trim().Normalize(System.Text.NormalizationForm.FormC);
                            nameText.Text = def.Name;
                            ViewModel.RefreshFieldOptions();
                            ViewModel.StatusMessage = $"已成功將自訂名稱重新命名為「{def.Name}」。";
                        }
                        else
                        {
                            await ShowErrorDialogAsync("重新命名失敗", err ?? "無法重新命名自訂欄位。");
                        }
                    }
                };
                Grid.SetColumn(renameBtn, 2);
                rowGrid.Children.Add(renameBtn);

                listStack.Children.Add(rowGrid);
            }

            scroll.Content = listStack;
            panel.Children.Add(scroll);
        }

        dialog.Content = panel;
        await ShowManagedDialogAsync(dialog);

        ViewModel.RefreshFieldOptions();
    }

    // --- Toolbar Actions ---

    private async void OnPickImageClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanRunActions || ViewModel.IsSaving || ViewModel.IsBusy) return;

        if (ViewModel.HasUnsavedChanges)
        {
            bool proceed = await ShowConfirmDialogAsync(
                "更換圖片確認",
                "目前名片草稿包含未保存或已編輯的內容。更換圖片將捨棄目前草稿，且無法復原。是否確定繼續？"
            );
            if (!proceed) return;
        }

        try
        {
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.CurrentWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                var err = await ViewModel.LoadFromFilePathAsync(file.Path);
                if (err != null)
                {
                    await ShowErrorDialogAsync("選取圖片失敗", err);
                }
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialogAsync("開啟檔案選擇器失敗", ex.Message);
        }
    }

    private async void OnPasteImageClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanRunActions || ViewModel.IsSaving || ViewModel.IsBusy) return;

        if (ViewModel.HasUnsavedChanges)
        {
            bool proceed = await ShowConfirmDialogAsync(
                "貼上圖片確認",
                "目前名片草稿包含未保存的內容。成功貼上新圖片後將捨棄目前草稿，且無法復原。是否確定繼續？"
            );
            if (!proceed) return;
        }

        var err = await ViewModel.PasteFromClipboardAsync();
        if (err != null)
        {
            await ShowErrorDialogAsync("貼上圖片提示", err);
        }
    }

    private async void OnFullOcrClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasImage)
        {
            await ShowErrorDialogAsync("無法辨識", "請先選取圖片或貼上剪貼簿圖片。");
            return;
        }

        var err = await ViewModel.RunFullOcrAsync();
        if (err != null)
        {
            await ShowErrorDialogAsync("整圖辨識結果", err);
        }
    }

    private async void OnRegionOcrClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasImage)
        {
            await ShowErrorDialogAsync("無法辨識", "請先選取圖片或貼上剪貼簿圖片。");
            return;
        }

        var err = await ViewModel.RunRegionOcrAsync();
        if (err != null)
        {
            await ShowErrorDialogAsync("局部辨識提示", err);
        }
        UpdateTargetHighlightUI();
    }

    private async void OnBlankDraftClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanRunActions || ViewModel.IsSaving || ViewModel.IsBusy) return;

        if (ViewModel.HasUnsavedChanges)
        {
            bool proceed = await ShowConfirmDialogAsync(
                "建立空白草稿",
                "目前草稿已被修改。建立新草稿將清除現有草稿列內容。是否確定清除？"
            );
            if (!proceed) return;
        }

        ViewModel.CreateBlankDraft();
        UpdateTargetHighlightUI();
    }

    private async void OnSaveDraftClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseStorage || _currentActiveDialog != null) return;

        var validation = MajiOCR_Windows.Services.Storage.DraftSnapshotFactory.ValidateDraft(ViewModel);
        if (!validation.IsValid)
        {
            await ShowSaveErrorAsync(SaveResult.Failed(validation.Code.ToString(), validation.Message));
            return;
        }

        string confirmationState = ViewModel.CaptureSaveConfirmationState();
        int fieldCount = MajiOCR_Windows.Services.Storage.DraftSnapshotFactory.CollectAllDistinctRows(ViewModel)
            .Count(row => !string.IsNullOrWhiteSpace(row.Value));
        var confirmation = new ContentDialog
        {
            Title = "確認儲存名片",
            Content = new TextBlock
            {
                Text = $"{(ViewModel.CurrentRevision == 0 ? "新增名片" : "更新目前名片")}\n" +
                       $"姓名：{ViewModel.NameRow.Value}\n公司：{ViewModel.CompanyRow.Value}\n" +
                       $"已確認欄位：{fieldCount} 個\n分類標籤：{ViewModel.ClassificationSummary}\n\n確認將這張名片儲存至本機？",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "確認儲存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        var decision = await ShowManagedDialogAsync(confirmation);
        var result = await ViewModel.SaveConfirmedDraftAsync(decision == ContentDialogResult.Primary, confirmationState);
        if (result.ErrorCode == "SAVE_CANCELLED") return;
        await ShowSaveErrorAsync(result);
    }

    private async Task ShowSaveErrorAsync(SaveResult result)
    {
        if (!result.Succeeded)
        {
            if (result.ErrorCode == nameof(DraftValidationCode.UnconfirmedFields))
            {
                await ShowErrorDialogAsync("尚有欄位待確認", result.ErrorMessage ?? "請先確認所有非空欄位。");
            }
            else if (result.ErrorCode == nameof(DraftValidationCode.NoConfirmedFields))
            {
                await ShowErrorDialogAsync("無法儲存名片", result.ErrorMessage ?? "請至少填寫並確認一個欄位。");
            }
            else if (result.ErrorCode == nameof(DraftValidationCode.MissingImage))
            {
                await ShowErrorDialogAsync("尚未載入圖片", result.ErrorMessage ?? "尚未載入名片圖片，無法保存名片。");
            }
            else
            {
                await ShowErrorDialogAsync("儲存尚未完成", result.ErrorMessage ?? "名片儲存失敗。");
            }
        }
    }

    private ContentDialog? _currentActiveDialog;
    public ContentDialog? CurrentActiveDialog => _currentActiveDialog;

    public async Task<ContentDialogResult?> ShowManagedDialogAsync(ContentDialog dialog)
    {
        if (_currentActiveDialog != null)
        {
            return null;
        }

        dialog.XamlRoot = this.XamlRoot;
        dialog.Resources["ContentDialogMaxWidth"] = 1400.0;
        dialog.Resources["ContentDialogMinWidth"] = 480.0;
        _currentActiveDialog = dialog;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _currentActiveDialog = null;
        }
    }

    private SavedCardsTablePanel? _savedCardsTablePanel;

    private async void OnSavedCardsClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseStorage || _currentActiveDialog != null) return;

        if (ViewModel.StorageService == null || !ViewModel.StorageService.IsAvailable)
        {
            await ShowErrorDialogAsync("資料庫不可用", ViewModel.StorageService?.ErrorMessage ?? "本機儲存資料庫尚未就緒。");
            return;
        }

        ShowSavedCardsTableView();
    }

    public void ShowSavedCardsTableView()
    {
        if (_savedCardsTablePanel == null)
        {
            _savedCardsTablePanel = new SavedCardsTablePanel(ViewModel.StorageService!);
            _savedCardsTablePanel.ActiveContactDeleted += (s, deletedId) =>
            {
                ViewModel.OnActiveContactDeletedExternally(deletedId);
            };
            _savedCardsTablePanel.RequestNavigateToScan += (s, e) =>
            {
                ShowScanView();
            };
            _savedCardsTablePanel.RequestReloadContact += async (s, detail) =>
            {
                if (ViewModel.HasUnsavedChanges)
                {
                    bool proceed = await ShowConfirmDialogAsync("未儲存變更提示", "目前名片草稿有未儲存的變更，載入此名片將會覆蓋現有草稿。是否繼續？");
                    if (!proceed) return;
                }

                await ViewModel.ReloadSavedContactAsync(detail);
                ShowScanView();
            };
            SavedCardsTableHostGrid.Children.Add(_savedCardsTablePanel);
        }
        else
        {
            _ = _savedCardsTablePanel.RefreshDataAsync();
        }

        TopCommandBar.Visibility = Visibility.Collapsed;
        if (OcrControlBarContainer != null) OcrControlBarContainer.Visibility = Visibility.Collapsed;
        OcrControlScrollViewer.Visibility = Visibility.Collapsed;
        MainContentScrollViewer.Visibility = Visibility.Collapsed;
        SavedCardsTableHostGrid.Visibility = Visibility.Visible;
    }

    public void ShowScanView()
    {
        SavedCardsTableHostGrid.Visibility = Visibility.Collapsed;
        TopCommandBar.Visibility = Visibility.Visible;
        if (OcrControlBarContainer != null) OcrControlBarContainer.Visibility = Visibility.Visible;
        OcrControlScrollViewer.Visibility = Visibility.Visible;
        MainContentScrollViewer.Visibility = Visibility.Visible;
        _ = ViewModel.RefreshClassificationAsync();
    }

    public SavedCardsTablePanel EnsureSavedCardsTablePanel()
    {
        if (_savedCardsTablePanel == null)
        {
            _savedCardsTablePanel = new SavedCardsTablePanel(ViewModel.StorageService!);
            _savedCardsTablePanel.ActiveContactDeleted += (s, deletedId) =>
            {
                ViewModel.OnActiveContactDeletedExternally(deletedId);
            };
            _savedCardsTablePanel.RequestNavigateToScan += (s, e) =>
            {
                ShowScanView();
            };
            _savedCardsTablePanel.RequestReloadContact += async (s, detail) =>
            {
                if (ViewModel.HasUnsavedChanges)
                {
                    bool proceed = await ShowConfirmDialogAsync("未儲存變更提示", "目前名片草稿有未儲存的變更，載入此名片將會覆蓋現有草稿。是否繼續？");
                    if (!proceed) return;
                }

                await ViewModel.ReloadSavedContactAsync(detail);
                ShowScanView();
            };
            SavedCardsTableHostGrid.Children.Add(_savedCardsTablePanel);
        }
        return _savedCardsTablePanel;
    }

    private async void OnBackupClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseStorage || _currentActiveDialog != null || ViewModel.StorageService == null) return;
        var panel = EnsureSavedCardsTablePanel();
        await panel.ShowBackupDialogAsync();
    }

    private async void OnRestoreClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseStorage || _currentActiveDialog != null || ViewModel.StorageService == null) return;
        var panel = EnsureSavedCardsTablePanel();
        await panel.ShowRestoreDialogAsync();
    }

    private async void OnExportOutlookClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseStorage || _currentActiveDialog != null || ViewModel.StorageService == null) return;
        var panel = EnsureSavedCardsTablePanel();
        await panel.ExportOutlookAsync();
    }

    private async void OnExportGoogleClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseStorage || _currentActiveDialog != null || ViewModel.StorageService == null) return;
        var panel = EnsureSavedCardsTablePanel();
        await panel.ExportGoogleAsync();
    }

    private async void OnExportVCardClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseStorage || _currentActiveDialog != null || ViewModel.StorageService == null) return;
        var panel = EnsureSavedCardsTablePanel();
        await panel.ExportVCardAsync();
    }

    private async void OnSyncGoogleContactsClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseStorage || _currentActiveDialog != null || ViewModel.StorageService == null) return;
        var panel = EnsureSavedCardsTablePanel();
        await panel.SyncGoogleContactsAsync();
    }

    private async void OnPickTagsClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseStorage || _currentActiveDialog != null ||
            ViewModel.StorageService is not MajiOCR_Windows.Services.Storage.ITagStorageService tags) return;
        var picker = new TagPickerPanel(tags, ViewModel.Classification);
        var dialog = new ContentDialog
        {
            Title = "名片分類標籤",
            Content = picker,
            PrimaryButtonText = "套用選擇",
            CloseButtonText = "取消",
            SecondaryButtonText = ViewModel.CurrentRevision > 0 ? "捨棄選擇並載入已存標籤" : "",
            MaxWidth = 780
        };
        var result = await ShowManagedDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.SetClassification(picker.Choices);
            if (ClassificationSummaryText != null)
            {
                ClassificationSummaryText.Text = ViewModel.ClassificationSummary;
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await ViewModel.ReloadClassificationAsync();
            if (ClassificationSummaryText != null)
            {
                ClassificationSummaryText.Text = ViewModel.ClassificationSummary;
            }
        }
    }

    private async void OnManageTagsClicked(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseStorage || _currentActiveDialog != null ||
            ViewModel.StorageService is not MajiOCR_Windows.Services.Storage.ITagStorageService tags) return;
        var panel = new TagCatalogPanel(tags);
        var dialog = new ContentDialog { Title = "管理分類標籤", Content = panel, CloseButtonText = "關閉", MaxWidth = 920 };
        dialog.Closing += (_, args) => { if (panel.Busy) args.Cancel = true; };
        await ShowManagedDialogAsync(dialog);
        await ViewModel.RefreshClassificationAsync();
        if (ClassificationSummaryText != null)
        {
            ClassificationSummaryText.Text = ViewModel.ClassificationSummary;
        }
    }

    private async void OnSecuritySettingsClicked(object sender, RoutedEventArgs e)
    {
        if (_currentActiveDialog != null || ViewModel.StorageService == null || XamlRoot == null) return;
        await SecurityDialogs.ShowSecuritySettingsDialogAsync(XamlRoot, ViewModel.StorageService, ViewModel.SettingsService);
    }

    private void OnCancelOcrClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelCurrentOcr();
    }

    private async void OnAboutClicked(object sender, RoutedEventArgs e)
    {
        if (_currentActiveDialog != null) return;

        string semVer = Program.GetAppVersion();
        string fileVer = _updateService.CurrentVersion;
        string changeLogText = _updateService.LoadLocalChangeLogText();

        var dialog = new ContentDialog
        {
            Title = "關於 MajiOCR.Windows",
            Content = new StackPanel
            {
                Spacing = 8,
                MinWidth = 480,
                MaxWidth = 560,
                Children =
                {
                    new TextBlock { Text = $"MajiOCR.Windows {semVer} (v{fileVer})", FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 16 },
                    new TextBlock { Text = $"桌面端版本: {semVer} (FileVersion: {fileVer}) | 內嵌 iOS/Web PWA 版本: 0.1.0-beta.1", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "架構: Windows 11 x64 WinUI 3 (Unpackaged, Self-Contained Single-File) + iOS 16+ PWA (Vercel 一鍵部署)" , TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "離線 OCR: Tesseract 5 (.NET 5.2.0) + 官方五語模型 (繁中/簡中/日/韓/英)" },
                    new TextBlock { Text = "本機與雲端儲存: SQLite (contacts.sqlite3) + SQLCipher-AES256 主密碼加密 + Google Drive 備份與 iPhone 雙管道同步", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "📋 版本更新資訊與變更紀錄 (Change Log)：", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                    new TextBox
                    {
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        Height = 180,
                        Text = changeLogText
                    }
                }
            },
            CloseButtonText = "確定",
            DefaultButton = ContentDialogButton.Close
        };
        await ShowManagedDialogAsync(dialog);
    }

    private async void OnCheckUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_currentActiveDialog != null) return;

        await UpdateManagementDialog.ShowDialogAsync(this.XamlRoot, _updateService, onBeforeExitForUpdate: () =>
        {
            // 更新前若有需保存的操作可在此執行
        });
    }

    private async void OnDeployVercelClicked(object sender, RoutedEventArgs e)
    {
        if (_currentActiveDialog != null || this.XamlRoot == null) return;

        await VercelDeployDialog.ShowAsync(this.XamlRoot, ViewModel.SettingsService);
    }

    private async void OnClearDraftClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsSaving || ViewModel.IsBusy) return;

        if (ViewModel.HasUnsavedChanges)
        {
            bool proceed = await ShowConfirmDialogAsync("清空草稿", "是否確定清空目前的名片草稿內容？");
            if (!proceed) return;
        }

        ViewModel.CreateBlankDraft();
        UpdateTargetHighlightUI();
    }

    // --- Dialog Helpers ---

    public async Task<string?> ShowTextInputDialogAsync(string title, string prompt)
    {
        if (_currentActiveDialog != null) return null;

        var input = new TextBox { Width = 300, PlaceholderText = "例如：統編" };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap },
                    input
                }
            },
            PrimaryButtonText = "確定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await ShowManagedDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            return input.Text;
        }
        return null;
    }

    public async Task<string> ShowConflictResolutionDialogAsync(string fieldName, string existingValue, string newValue)
    {
        if (_currentActiveDialog != null) return "Cancel";

        var dialog = new ContentDialog
        {
            Title = "單值欄位衝突提示",
            Content = new TextBlock
            {
                Text = $"單值欄位【{fieldName}】已有已確認內容：「{existingValue}」。\n新候選內容為：「{newValue}」。\n\n請選擇處理方式：\n- 替換：取消舊列確認狀態，改以此列為確認值。\n- 保留兩筆：同時保留兩筆為此欄位。\n- 取消：放棄變更此列欄位分類。",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "替換舊列",
            SecondaryButtonText = "保留兩筆",
            CloseButtonText = "取消操作",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await ShowManagedDialogAsync(dialog);
        return result switch
        {
            ContentDialogResult.Primary => "Replace",
            ContentDialogResult.Secondary => "KeepBoth",
            _ => "Cancel"
        };
    }

    public async Task<bool> ShowConfirmDialogAsync(string title, string content)
    {
        if (_currentActiveDialog != null) return false;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "確定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await ShowManagedDialogAsync(dialog);
        return result == ContentDialogResult.Primary;
    }

    public async Task ShowErrorDialogAsync(string title, string message)
    {
        if (_currentActiveDialog != null) return;

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "確定"
        };

        await ShowManagedDialogAsync(dialog);
    }
}
