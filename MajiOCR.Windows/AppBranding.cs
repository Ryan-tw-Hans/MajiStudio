using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MajiOCR_Windows;

/// <summary>Loads branding from the application itself for single-file launches.</summary>
internal static class AppBranding
{
    public static void Apply(Window window, TitleBar titleBar)
    {
        using var logo = typeof(AppBranding).Assembly.GetManifestResourceStream("MajiOCR.Branding.Logo")
            ?? throw new InvalidOperationException("The embedded application logo is missing.");
        using var randomAccessLogo = logo.AsRandomAccessStream();
        var bitmap = new BitmapImage();
        bitmap.SetSource(randomAccessLogo);
        titleBar.IconSource = new ImageIconSource { ImageSource = bitmap };

        // The EXE carries the same multi-resolution ICO used by Explorer.
        // Keep native handles alive until the window closes.
        if (Environment.ProcessPath is not string executable) return;
        uint count = ExtractIconEx(executable, 0, out nint largeIcon, out nint smallIcon, 1);
        if (count == 0 || largeIcon == 0)
        {
            if (smallIcon != 0) DestroyIcon(smallIcon);
            return;
        }

        try
        {
            window.AppWindow.SetIcon(Win32Interop.GetIconIdFromIcon(largeIcon));
        }
        catch
        {
            DestroyIcon(largeIcon);
            if (smallIcon != 0) DestroyIcon(smallIcon);
            throw;
        }

        window.Closed += (_, _) =>
        {
            DestroyIcon(largeIcon);
            if (smallIcon != 0) DestroyIcon(smallIcon);
        };
    }

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file, int index, out nint largeIcon, out nint smallIcon, uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
