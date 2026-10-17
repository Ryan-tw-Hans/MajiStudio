using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows;

public sealed class CardImageViewerDialog : ContentDialog
{
    private readonly ContactDetailModel? _detail;
    private readonly byte[]? _imageBytes;
    private readonly string? _imageAbsolutePath;
    private readonly string _cardTitle;

    private ScrollViewer _imageScrollViewer = null!;
    private Image _previewImage = null!;
    private ScaleTransform _scaleTransform = null!;
    private RotateTransform _rotateTransform = null!;
    private TransformGroup _transformGroup = null!;
    private TextBlock _zoomPercentText = null!;
    private TextBlock _metaInfoText = null!;
    private TextBlock _errorNoticeText = null!;
    private Button _openSystemBtn = null!;

    private double _currentScale = 1.0;
    private double _currentRotation = 0.0;

    public CardImageViewerDialog(ContactDetailModel detail, string? imageAbsolutePath = null)
        : this(detail.VerifiedImageBytes, detail.DisplayTitle, imageAbsolutePath, detail)
    {
    }

    public CardImageViewerDialog(byte[]? imageBytes, string cardTitle, string? imageAbsolutePath = null, ContactDetailModel? detail = null)
    {
        _imageBytes = imageBytes;
        _cardTitle = string.IsNullOrWhiteSpace(cardTitle) ? "名片圖片" : cardTitle;
        _imageAbsolutePath = imageAbsolutePath;
        _detail = detail;

        Title = $"名片圖檔檢視：{_cardTitle}";
        CloseButtonText = "關閉 (Esc)";
        DefaultButton = ContentDialogButton.Close;
        Resources["ContentDialogMaxWidth"] = 1200.0;
        Resources["ContentDialogMaxHeight"] = 900.0;

        BuildVisualTree();
        Loaded += async (s, e) => await LoadImageAsync();
    }

    private void BuildVisualTree()
    {
        var rootGrid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto }, // Toolbar
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, // Image Area
                new RowDefinition { Height = GridLength.Auto }  // Bottom info
            },
            MinWidth = 680,
            MinHeight = 480,
            MaxWidth = 1100,
            MaxHeight = 780
        };

        // --- 1. Top Toolbar ---
        var toolbarGrid = new Grid { Margin = new Thickness(0, 0, 0, 10), ColumnSpacing = 8 };
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        var zoomOutBtn = new Button { Content = "➖ 縮小", Padding = new Thickness(8, 4, 8, 4), FontSize = 12 };
        zoomOutBtn.Click += (s, e) => Zoom(-0.2);
        leftTools.Children.Add(zoomOutBtn);

        _zoomPercentText = new TextBlock
        {
            Text = "100%",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 46,
            TextAlignment = TextAlignment.Center
        };
        leftTools.Children.Add(_zoomPercentText);

        var zoomInBtn = new Button { Content = "➕ 放大", Padding = new Thickness(8, 4, 8, 4), FontSize = 12 };
        zoomInBtn.Click += (s, e) => Zoom(0.2);
        leftTools.Children.Add(zoomInBtn);

        var fitBtn = new Button { Content = "適合視窗", Padding = new Thickness(8, 4, 8, 4), FontSize = 12 };
        fitBtn.Click += (s, e) => ResetFit();
        leftTools.Children.Add(fitBtn);

        var rotateBtn = new Button { Content = "🔄 旋轉 90°", Padding = new Thickness(8, 4, 8, 4), FontSize = 12 };
        rotateBtn.Click += (s, e) => RotateClockwise();
        leftTools.Children.Add(rotateBtn);

        Grid.SetColumn(leftTools, 0);
        toolbarGrid.Children.Add(leftTools);

        var rightTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _openSystemBtn = new Button
        {
            Content = "💻 使用系統看圖軟體開啟",
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 12,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        _openSystemBtn.Click += OnOpenWithSystemAppClicked;
        _openSystemBtn.IsEnabled = !string.IsNullOrEmpty(_imageAbsolutePath) && File.Exists(_imageAbsolutePath);
        rightTools.Children.Add(_openSystemBtn);

        Grid.SetColumn(rightTools, 1);
        toolbarGrid.Children.Add(rightTools);

        Grid.SetRow(toolbarGrid, 0);
        rootGrid.Children.Add(toolbarGrid);

        // --- 2. Image Display Area ---
        var imageBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            MinHeight = 380,
            MaxHeight = 600,
            Padding = new Thickness(4)
        };

        _imageScrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Disabled, // We use RenderTransform for precision
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _imageScrollViewer.PointerWheelChanged += OnImagePointerWheelChanged;

        var imageContainer = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _scaleTransform = new ScaleTransform { ScaleX = 1.0, ScaleY = 1.0, CenterX = 0.5, CenterY = 0.5 };
        _rotateTransform = new RotateTransform { Angle = 0.0, CenterX = 0.5, CenterY = 0.5 };
        _transformGroup = new TransformGroup();
        _transformGroup.Children.Add(_scaleTransform);
        _transformGroup.Children.Add(_rotateTransform);

        _previewImage = new Image
        {
            Stretch = Stretch.Uniform,
            RenderTransform = _transformGroup,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        imageContainer.Children.Add(_previewImage);

        _errorNoticeText = new TextBlock
        {
            Text = "無法載入名片圖片或尚未儲存圖檔。",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 230, 80, 0)),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Visibility = Visibility.Collapsed
        };
        imageContainer.Children.Add(_errorNoticeText);

        _imageScrollViewer.Content = imageContainer;
        imageBorder.Child = _imageScrollViewer;

        Grid.SetRow(imageBorder, 1);
        rootGrid.Children.Add(imageBorder);

        // --- 3. Bottom Meta Info Bar ---
        var bottomGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        _metaInfoText = new TextBlock
        {
            Text = "提示：支援滑鼠滾輪縮放、旋轉與適合視窗比例檢視。",
            FontSize = 11,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        };
        bottomGrid.Children.Add(_metaInfoText);

        Grid.SetRow(bottomGrid, 2);
        rootGrid.Children.Add(bottomGrid);

        Content = rootGrid;
    }

    private async Task LoadImageAsync()
    {
        byte[]? bytes = _imageBytes;

        if (bytes == null && !string.IsNullOrEmpty(_imageAbsolutePath) && File.Exists(_imageAbsolutePath))
        {
            try
            {
                bytes = await File.ReadAllBytesAsync(_imageAbsolutePath);
            }
            catch
            {
                bytes = null;
            }
        }

        if (bytes != null && bytes.Length > 0)
        {
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                }
                stream.Seek(0);

                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(stream);

                _previewImage.Source = bmp;
                _previewImage.Visibility = Visibility.Visible;
                _errorNoticeText.Visibility = Visibility.Collapsed;

                int width = bmp.PixelWidth;
                int height = bmp.PixelHeight;
                double sizeKb = bytes.Length / 1024.0;
                _metaInfoText.Text = $"名片圖片資訊：{width} × {height} 像素 ({sizeKb:F1} KB)  |  支援滑鼠滾輪縮放與旋轉檢視";
            }
            catch (Exception ex)
            {
                _previewImage.Visibility = Visibility.Collapsed;
                _errorNoticeText.Text = $"圖片解碼失敗: {ex.Message}";
                _errorNoticeText.Visibility = Visibility.Visible;
            }
        }
        else
        {
            _previewImage.Visibility = Visibility.Collapsed;
            _errorNoticeText.Text = "此名片尚無關聯的圖檔或檔案無法讀取。";
            _errorNoticeText.Visibility = Visibility.Visible;
        }

        _openSystemBtn.IsEnabled = !string.IsNullOrEmpty(_imageAbsolutePath) && File.Exists(_imageAbsolutePath);
    }

    private void Zoom(double delta)
    {
        _currentScale = Math.Clamp(_currentScale + delta, 0.2, 5.0);
        _scaleTransform.ScaleX = _currentScale;
        _scaleTransform.ScaleY = _currentScale;
        _zoomPercentText.Text = $"{Math.Round(_currentScale * 100)}%";
    }

    private void ResetFit()
    {
        _currentScale = 1.0;
        _currentRotation = 0.0;
        _scaleTransform.ScaleX = 1.0;
        _scaleTransform.ScaleY = 1.0;
        _rotateTransform.Angle = 0.0;
        _zoomPercentText.Text = "100%";
    }

    private void RotateClockwise()
    {
        _currentRotation = (_currentRotation + 90.0) % 360.0;
        _rotateTransform.Angle = _currentRotation;
    }

    private void OnImagePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(_imageScrollViewer).Properties;
        int delta = props.MouseWheelDelta;
        if (delta != 0)
        {
            Zoom(delta > 0 ? 0.15 : -0.15);
            e.Handled = true;
        }
    }

    private void OnOpenWithSystemAppClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_imageAbsolutePath) || !File.Exists(_imageAbsolutePath))
        {
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _imageAbsolutePath,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch
        {
            // Fallback ignore if external app launcher fails
        }
    }
}
