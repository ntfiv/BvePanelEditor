using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BvePanelEditor;

public partial class MainWindow
{
    /// <summary>窓の中身を PNG に保存する（表示範囲の見本の保存などで使う）。</summary>
    public static void SaveWindowPng(Window win, string pngPath)
    {
        var root = (FrameworkElement)win.Content;
        var dpi = VisualTreeHelper.GetDpi(win);
        var rtb = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(pngPath);
        enc.Save(fs);
    }
}
