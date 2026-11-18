using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BvePanelEditor.Core;
using BvePanelEditor.Rendering;

namespace BvePanelEditor;

/// <summary>オブジェクトの一覧の下: 選んだオブジェクトの昼・夜の画像を出す（畳めば次に起動したときも畳んだまま）。</summary>
public partial class MainWindow
{
    private bool _imagePreviewFolded;
    private static readonly Brush PreviewChecker = PanelCanvas.MakeChecker();

    private void OnImagePreviewHeader(object sender, MouseButtonEventArgs e) => SetImagePreviewFolded(!_imagePreviewFolded, save: true);

    public void SetImagePreviewFolded(bool folded, bool save)
    {
        _imagePreviewFolded = folded;
        ImagePreviewBody.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        ImagePreviewFold.Text = folded ? "▶" : "▼";
        if (save) SaveViewSettings();
        if (!folded) UpdateImagePreview();
    }

    /// <summary>主に選んでいるオブジェクトの画像を出す（パネル画面と同じ、透過色・色の掛け合わせ済みの絵）。</summary>
    private void UpdateImagePreview()
    {
        PreviewDayBox.Background = PreviewNightBox.Background = PreviewChecker;
        if (_imagePreviewFolded) return;
        BitmapSource? day = null, night = null;
        string? dayPath = null, nightPath = null;
        if (_doc != null && _scene != null && _selected >= 0 && _selected < _doc.Sections.Count)
        {
            var s = _doc.Sections[_selected];
            if (s.Kind == SectionKind.This) { day = _scene.BaseDay; night = _scene.BaseNight; }
            else if (_scene.Items.FirstOrDefault(i => i.Section == _selected) is { } it) { day = it.Day; night = it.Night; }
            dayPath = s.Get("DaytimeImage");
            nightPath = s.Get("NighttimeImage");
        }
        ShowPreview(PreviewDayImage, PreviewDayText, "昼", day, dayPath);
        ShowPreview(PreviewNightImage, PreviewNightText, "夜", night, nightPath);
    }

    private static void ShowPreview(Image image, TextBlock text, string label, BitmapSource? img, string? path)
    {
        image.Source = img;
        // 小さい画像は拡大してもぼかさない
        RenderOptions.SetBitmapScalingMode(image, img != null && img.PixelHeight < 110 && img.PixelWidth < 160 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        var rel = path?.Trim().Trim('"');
        if (string.IsNullOrEmpty(rel)) { text.Text = $"{label}: なし"; text.ToolTip = null; return; }
        var name = Path.GetFileName(rel);
        text.Text = img != null ? $"{label}: {name}（{img.PixelWidth}×{img.PixelHeight}）" : $"{label}: {name}（読めません）";
        text.ToolTip = rel;
    }
}
