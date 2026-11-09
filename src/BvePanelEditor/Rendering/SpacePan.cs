using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BvePanelEditor.Rendering;

/// <summary>
/// Photoshop と同じ「スペースキーを押しながら左ドラッグで表示を動かす」。
/// マウスが画面（canvas）の上にあり、文字を入れる欄にフォーカスがないときにスペースを押すと、押している間だけ有効になる
/// （スペースはボタンやテキストに渡さない）。文字を入れる欄にフォーカスがあるときは、ふつうにスペースを入力する。
/// </summary>
public sealed class SpacePan
{
    private readonly FrameworkElement _canvas;
    private Window? _window;

    /// <summary>スペースを押している（左ドラッグで表示を動かす）。</summary>
    public bool Held { get; private set; }

    /// <summary>表示を動かしているときのマウスの形。</summary>
    public static Cursor Cursor => Cursors.ScrollAll;

    /// <summary>押した・離したとき（マウスの形を直すため）。</summary>
    public event Action? Changed;

    public SpacePan(FrameworkElement canvas)
    {
        _canvas = canvas;
        canvas.Loaded += (_, _) => Hook();
        canvas.Unloaded += (_, _) => Unhook();
    }

    private void Hook()
    {
        var w = Window.GetWindow(_canvas);
        if (w == _window) return;
        Unhook();
        _window = w;
        if (w == null) return;
        w.PreviewKeyDown += OnKeyDown;
        w.PreviewKeyUp += OnKeyUp;
        w.Deactivated += OnDeactivated;
    }

    private void Unhook()
    {
        if (_window == null) return;
        _window.PreviewKeyDown -= OnKeyDown;
        _window.PreviewKeyUp -= OnKeyUp;
        _window.Deactivated -= OnDeactivated;
        _window = null;
        Release();
    }

    /// <summary>文字を入れる欄（テキスト欄・数字の欄・パネルファイルのテキスト）にフォーカスがある。</summary>
    private static bool IsTextInput(object? focused) =>
        focused is TextBoxBase or PasswordBox or ICSharpCode.AvalonEdit.Editing.TextArea;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        if (Held) { e.Handled = true; return; } // 押しっぱなしのくり返し
        if (!_canvas.IsVisible || !_canvas.IsMouseOver || IsTextInput(Keyboard.FocusedElement)) return;
        e.Handled = true;
        Held = true;
        _canvas.Cursor = Cursor;
        Changed?.Invoke();
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !Held) return;
        e.Handled = true;
        Release();
    }

    private void OnDeactivated(object? sender, EventArgs e) => Release();

    private void Release()
    {
        if (!Held) return;
        Held = false;
        if (!Panning) _canvas.Cursor = null; // ドラッグ中なら、離すまで移動の形のまま
        Changed?.Invoke();
    }

    /// <summary>表示を動かしている（どのボタンでも）。</summary>
    public bool Panning { get; private set; }

    /// <summary>表示を動かし始めた（中・右ボタン、スペース＋左ボタン）: マウスの形を変える。</summary>
    public void BeginPan()
    {
        Panning = true;
        _canvas.Cursor = Cursor;
    }

    /// <summary>表示を動かし終えた: スペースを押したままなら移動の形のまま、そうでなければ戻す。</summary>
    public void EndPan()
    {
        if (!Panning) return;
        Panning = false;
        _canvas.Cursor = Held ? Cursor : null;
        Changed?.Invoke();
    }

    /// <summary>この押し方で表示を動かすか（中・右ボタン、またはスペースを押しながら左ボタン）。</summary>
    public bool IsPanButton(MouseButton b) => b is MouseButton.Middle or MouseButton.Right || (b == MouseButton.Left && Held);

    /// <summary>確認用: スペースを押した・離したことにする。</summary>
    public void SetHeldForTest(bool held)
    {
        if (held == Held) return;
        Held = held;
        if (!Panning) _canvas.Cursor = held ? Cursor : null;
        Changed?.Invoke();
    }
}
