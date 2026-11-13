using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BvePanelEditor.Core;

namespace BvePanelEditor;

/// <summary>編集操作の通し確認（--selftest 結果.txt）。開いたパネルのコピーに対して使う。</summary>
public partial class MainWindow
{
    private int _selfTestWait;

    public void RunSelfTest(string reportPath)
    {
        if (_doc == null && _selfTestWait++ < 40)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            t.Tick += (_, _) => { t.Stop(); RunSelfTest(reportPath); };
            t.Start();
            return;
        }
        var log = new List<string> { $"file={_path} doc={(_doc != null)} wait={_selfTestWait}" };
        int fail = 0;
        void Check(string name, bool ok)
        {
            log.Add($"{(ok ? "OK  " : "FAIL")} {name}");
            if (!ok) fail++;
        }
        try
        {
            var t0 = Editor.Text;
            int n0 = _doc!.Sections.Count;
            int needle = _doc.Sections.FindIndex(s => s.Kind == SectionKind.Needle);
            Check("指針がある", needle >= 0);

            SelectSection(needle);
            Check("選択がキャンバス・一覧に伝わる", Canvas.Selected == needle && ElementList.SelectedItem is ElementRow { Index: var li } && li == needle);
            Check("テキストの強調がセクションに合う", _highlighter.HeaderLine == _doc.LineMap()[needle].HeaderLine);

            OnPropertyCommit("Radius", "200");
            Check("プロパティ変更がテキストに入る", Editor.Text.Contains("Radius = 200"));
            Check("プロパティ変更が描画に入る", _scene!.ItemFor(needle)!.Radius == 200);
            Check("変更は 1 か所だけ", Editor.Text.Length - t0.Length == 0 || Math.Abs(Editor.Text.Length - t0.Length) < 5);

            OnUndo(this, null!);
            Check("元に戻すで元の本文", Editor.Text == t0);
            OnRedo(this, null!);
            Check("やり直しで再び変更", Editor.Text.Contains("Radius = 200"));
            OnUndo(this, null!);

            OnAddElement(new MenuItem { Tag = "DigitalGauge" }, new RoutedEventArgs());
            Check("円形ゲージを追加", _doc.Sections.Count == n0 + 1 && _doc.Sections[_selected].Kind == SectionKind.DigitalGauge);
            Check("追加したゲージに問題なし", !_scene.Problems.Any(p => p.Section == _selected));
            OnUndo(this, null!);
            Check("追加を元に戻す", Editor.Text == t0 && _doc.Sections.Count == n0);

            SelectSection(needle);
            OnDuplicate(null, null);
            Check("複製で 1 つ増える", _doc.Sections.Count == n0 + 1 && _selected == needle + 1);
            OnUndo(this, null!);
            Check("複製を元に戻す", Editor.Text == t0);

            SelectSection(needle);
            Move(1);
            Check("並べ替え", _selected == needle + 1 && _doc.Sections.Count == n0);
            OnUndo(this, null!);
            Check("並べ替えを元に戻す", Editor.Text == t0);

            SelectSection(needle);
            OnDelete(null, null);
            Check("削除", _doc.Sections.Count == n0 - 1);
            OnUndo(this, null!);
            Check("削除を元に戻す", Editor.Text == t0);

            // テキストを直接書き換える
            var map = _doc.LineMap();
            var stopPin0 = _doc.Sections[needle].Get("StopPin");
            var line = Editor.Document.GetLineByNumber(map[needle].HeaderLine);
            Editor.Document.Insert(line.EndOffset, _doc.NewLine + "StopPin = True");
            FlushReparse();
            Check("テキスト編集がモデルに入る", _doc.Sections[needle].Get("StopPin") == "True");
            Check("2.00 でも StopPin は問題にしない（BVE 5.8 以降は読む）", !_scene.Problems.Any(p => p.Section == needle && p.Message.Contains("StopPin")));
            OnUndo(this, null!);
            Check("テキスト編集を元に戻す", Editor.Text == t0 && _doc.Sections[needle].Get("StopPin") == stopPin0);

            // 名前（直前のコメント）
            SelectSection(needle);
            var oldTitle = _doc.Sections[needle].Title;
            OnTitleCommit("テスト用の名前");
            var hdr = _doc.LineMap()[needle].HeaderLine;
            Check("名前は見出しのすぐ上のコメント行になる",
                Editor.Document.GetText(Editor.Document.GetLineByNumber(hdr - 1)).EndsWith("テスト用の名前")
                || Editor.Document.GetText(Editor.Document.GetLineByNumber(hdr)).EndsWith("テスト用の名前"));
            Check("一覧の 1 行目が名前になる", Rows.First(r => r.Index == needle).Primary == "テスト用の名前");
            Check("名前を変えても選択はそのまま", _selected == needle && Canvas.Selected == needle);
            Check("描画データにも名前", _scene.ItemFor(needle)!.Title == "テスト用の名前");
            OnTitleCommit("");
            Check("名前を消せる", _doc.Sections[needle].Title == "" || _doc.Sections[needle].Title != "テスト用の名前");
            OnUndo(this, null!);
            OnUndo(this, null!);
            Check("名前の変更を元に戻す", Editor.Text == t0 && _doc.Sections[needle].Title == oldTitle);
            log.Add($"     （元の名前: 「{oldTitle}」）");
            SelectSection(needle);
            BeginRename();
            Check("一覧の名前入力欄が開く", Keyboard.FocusedElement is TextBox tb && tb.Text == oldTitle);
            if (Keyboard.FocusedElement is TextBox tb2) tb2.Text = "一覧から変更";
            ElementList.Focus(); // 欄からフォーカスが外れると確定する
            Check("一覧の入力欄で名前を確定", _doc.Sections[needle].Title == "一覧から変更");
            OnUndo(this, null!);
            Check("一覧からの変更を元に戻す", Editor.Text == t0);

            // キャンバスのドラッグ相当（位置を変えて確定）
            var item = _scene.ItemFor(needle)!;
            _doc.Sections[needle].Set("Location", PanelValues.Point(item.Location.X + 5, item.Location.Y));
            CommitModel();
            Check("位置の変更", _scene.ItemFor(needle)!.Location.X == item.Location.X + 5);
            OnUndo(this, null!);

            // 位置ロック
            Check("開いた直後は位置ロック", Canvas.PositionLocked && LockButton.IsChecked == true && LockMenu.IsChecked);
            SelectSection(needle);
            var loc0 = _scene.ItemFor(needle)!.Location;
            void PressRight()
            {
                var src = PresentationSource.FromVisual(Canvas)!;
                Canvas.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
            }
            PressRight();
            Check("ロック中は矢印キーで動かない", Editor.Text == t0 && _scene.ItemFor(needle)!.Location == loc0);
            Check("ロック中に動かすと案内が出る", MessageText.Text.Contains("ロック中"));
            LockButton.IsChecked = false; // ボタンのクリックと同じ（IsChecked が外れてから Click が来る）
            OnLockButton(this, new RoutedEventArgs());
            Check("ボタンで解除", !Canvas.PositionLocked && !LockMenu.IsChecked);
            PressRight();
            Check("解除中は矢印キーで 1px 動く", _scene.ItemFor(needle)!.Location.X == loc0.X + 1);
            OnUndo(this, null!);
            Check("その移動を元に戻す", Editor.Text == t0);
            LoadPanel(_path!, _vehicle);
            Check("開き直すとロックに戻る", Canvas.PositionLocked && LockButton.IsChecked == true);
            Check("ウィンドウ名がパネルくん", Title.Contains("パネルくん"));

            // 値のプレビュー（Ctrl＋上下ドラッグ相当）と動作プレビュー欄の色付け
            SelectSection(needle);
            var nkey = StateKeyOf(needle)!;
            Check("選んだ指針の値の行に色が付く", _states.HighlightedKey == nkey);
            Check("案内に選択中のオブジェクトが出る", SelectedStateText.Text.Contains("選択中"));
            var nr = _states.Range(nkey)!;
            Canvas.State[nkey] = nr.Min;
            OnValueDragStarted(needle);
            OnValueDragged(needle, 100, false); // 上へ 100px = 範囲の半分
            Check("ロック中でも Ctrl＋ドラッグで値が動く", Canvas.PositionLocked && Math.Abs(Canvas.State[nkey] - (nr.Min + (nr.Max - nr.Min) / 2)) <= (nr.IsInteger ? 0.5 : 1e-6));
            OnValueDragged(needle, 10000, false);
            Check("値は最大で止まる", Canvas.State[nkey] == nr.Max);
            OnValueDragged(needle, 10, true);
            Check("Shift で細かく動く", Canvas.State[nkey] - nr.Min <= (nr.Max - nr.Min) / 100 + 0.5);
            Check("値を動かしてもファイルは変わらない", Editor.Text == t0);

            int lamp = _doc.Sections.FindIndex(s => s.Kind == SectionKind.PilotLamp && StateKeyOf(_doc.Sections.IndexOf(s)) != null);
            if (lamp >= 0)
            {
                var lkey = StateKeyOf(lamp)!;
                SelectSection(lamp);
                Check("表示灯の値の行に色が付く", _states.HighlightedKey == lkey);
                AllLampsCheck.IsChecked = true;
                OnPreviewOption(this, new RoutedEventArgs());
                Canvas.State[lkey] = 0;
                OnValueDragStarted(lamp);
                Check("表示灯を動かすと全点灯を外す", AllLampsCheck.IsChecked == false && !Canvas.AllLampsOn);
                OnValueClicked(lamp);
                bool lampBool = _states.Range(lkey)!.IsBool;
                Check("表示灯は Ctrl＋クリックで切り替わる", !lampBool || Canvas.State[lkey] == 1);
                OnValueDragged(lamp, -50, false);
                Check("下へドラッグで消える", !lampBool || Canvas.State[lkey] == 0);
            }
            int always = _doc.Sections.FindIndex(s => s.Kind == SectionKind.PilotLamp && (s.Get("Subject") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase));
            if (always >= 0)
            {
                SelectSection(always);
                Check("常表示（true）のオブジェクトは色を付けない", _states.HighlightedKey == null && SelectedStateText.Text.Contains("動かせる値はありません"));
            }

            // ===== グループ =====
            var lampIdx = _doc.Sections.Select((s, i) => (s, i)).Where(t => t.s.Kind == SectionKind.PilotLamp).Select(t => t.i).ToList();
            if (lampIdx.Count >= 3)
            {
                var original = File.ReadAllBytes(_path!);
                _autoConfirm = _ => true;
                var pick = new List<int> { lampIdx[0], lampIdx[lampIdx.Count / 2], lampIdx[^1] }.Distinct().ToList();
                ApplySelection(pick[0], pick, null, SelectionSource.Other);
                Check("複数選択がキャンバスと一覧に伝わる", Canvas.SelectedSet.Count == pick.Count && ElementList.SelectedItems.Count == pick.Count);
                OnMakeGroup(null, null);
                var g = _selGroup;
                Check("グループにするとグループ全体が選ばれる", g != null && _selSet.Count == pick.Count && Canvas.SelectedGroup == g);
                var members = _doc.Members(g!);
                Check("グループのオブジェクトはファイル内で連続する", members[^1] - members[0] == members.Count - 1);
                Check("ファイルに開始行と終了行が入る", Editor.Text.Contains($";===== グループ: {g} =====") && Editor.Text.Contains($";===== グループ終わり: {g} ====="));
                Check("一覧にグループの見出し行が出る", Rows.Any(r => r.IsHeader && r.GroupName == g) && Rows.Count(r => !r.IsHeader && members.Contains(r.Index) && r.Indent.Left > 0) == members.Count);
                var hlRange = Enumerable.Range(_highlighter.FirstLine, _highlighter.HeaderLine - _highlighter.FirstLine + 1)
                    .Select(n => Editor.Document.GetText(Editor.Document.GetLineByNumber(n)));
                Check("テキストの色付けが開始行から", hlRange.Any(l => l.StartsWith(";===== グループ:")));
                Check("読み直してもグループが残る", PanelDocument.Parse(Editor.Text).Members(g!).Count == members.Count);

                // パネル画面のクリック: 1 回目はグループ全体、ダブルクリックで 1 つ
                int target = -1;
                Point at = default;
                foreach (var m in members)
                {
                    if (_scene!.ItemFor(m) is not { } mi) continue;
                    var b = Bounds(mi);
                    var c = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
                    if (Canvas.SelectAt(c, false, false) == m) { target = m; at = c; break; }
                }
                if (target >= 0)
                {
                    Canvas.SelectAt(at, false, false);
                    Check("クリックでグループ全体が選ばれる", Canvas.SelectedGroup == g && _selGroup == g && _selSet.Count == members.Count);
                    Canvas.SelectAt(at, true, false);
                    Check("ダブルクリックで 1 つだけ選ばれる", Canvas.SelectedSet.Count == 1 && Canvas.EnteredGroup == g && _selGroup == null && _selected == target);
                    Canvas.SelectAt(at, false, false);
                    Check("中に入ったままなら 1 つずつ", Canvas.SelectedSet.Count == 1 && Canvas.EnteredGroup == g);
                    Canvas.SelectAt(new Point(-5000, -5000), false, false);
                    Check("グループの外をクリックすると抜ける", Canvas.EnteredGroup == null);
                }
                else log.Add("     （グループのオブジェクトがほかのオブジェクトの下に隠れていて、クリックの確認は省略）");

                // 折りたたんだグループの中のオブジェクトを 1 つ選ぶと、そのグループが開く
                _folded.Add(g!);
                RebuildAll();
                Check("折りたたむと中のオブジェクトが一覧から消える", !Rows.Any(r => !r.IsHeader && members.Contains(r.Index)));
                ApplySelection(members[0], [members[0]], null, SelectionSource.Other);
                Check("中のオブジェクトを選ぶとグループが開いて一覧で選ばれる", !_folded.Contains(g!) && ElementList.SelectedItem is ElementRow { Index: var fi } && fi == members[0]);

                // まとめて動かす
                ApplySelection(members[0], members, g, SelectionSource.Other);
                var locs = members.Select(i => _scene!.ItemFor(i)!.Location).ToList();
                var t1 = Editor.Text;
                PressRight();
                Check("ロック中はグループも動かない", Editor.Text == t1);
                LockButton.IsChecked = false;
                OnLockButton(this, new RoutedEventArgs());
                PressRight();
                Check("矢印キーでグループ全部が 1px 動く", members.Select((i, k) => _scene!.ItemFor(i)!.Location.X == locs[k].X + 1).All(b => b));
                OnUndo(this, null!);
                Check("グループの移動を元に戻す", Editor.Text == t1);
                var tl = TopLeftOf(members)!.Value;
                OnGroupLocationCommit(tl.X + 5, tl.Y);
                Check("プロパティ欄の位置でグループ全部が動く", members.Select((i, k) => _scene!.ItemFor(i)!.Location.X == locs[k].X + 5).All(b => b));
                OnUndo(this, null!);
                LockButton.IsChecked = true;
                OnLockButton(this, new RoutedEventArgs());

                // 名前・複製・並べ替え・解除
                RenameGroup(g!, "テストグループ");
                Check("グループ名の変更", _selGroup == "テストグループ" && Editor.Text.Contains(";===== グループ: テストグループ ====="));
                var t2 = Editor.Text;
                OnDuplicate(null, null);
                Check("グループの複製", _doc.Members("テストグループ のコピー").Count == members.Count && _selGroup == "テストグループ のコピー");
                OnUndo(this, null!);
                Check("複製を元に戻す", Editor.Text == t2);
                var mg = _doc.Members("テストグループ");
                ApplySelection(mg[0], mg, "テストグループ", SelectionSource.Other);
                Move(-1);
                var mg2 = _doc.Members("テストグループ");
                Check("グループごと並べ替え", mg2[0] < mg[0] && mg2[^1] - mg2[0] == mg2.Count - 1);
                OnUndo(this, null!);
                Check("並べ替えを元に戻す", Editor.Text == t2);
                OnUngroup(null, null);
                Check("グループの解除", !Editor.Text.Contains("グループ: テストグループ") && _selGroup == null);
                OnUndo(this, null!);
                Check("解除を元に戻す", Editor.Text == t2);
                var mg3 = _doc.Members("テストグループ");
                ApplySelection(mg3[0], mg3, "テストグループ", SelectionSource.Other);
                int before = _doc.Sections.Count;
                OnDelete(null, null);
                Check("グループごと削除", _doc.Sections.Count == before - mg3.Count);
                OnUndo(this, null!);
                Check("削除を元に戻す", Editor.Text == t2);

                // 保存して開き直す
                Save(_path);
                LoadPanel(_path!, _vehicle);
                Check("保存して開き直してもグループが残る", _doc.Members("テストグループ").Count == members.Count && Rows.Any(r => r.IsHeader));
                File.WriteAllBytes(_path!, original); // 確認用のコピーを元に戻す
                LoadPanel(_path!, _vehicle);
                Check("元のファイルに戻した", Editor.Text == t0);
                _autoConfirm = null;
            }
            else log.Add("     （表示灯が 3 つ未満なのでグループの確認は省略）");

            // ===== 複数選択（すべて選択・囲み選択・まとめて編集・整列） =====
            {
                var instruments = Enumerable.Range(0, _doc.Sections.Count).Where(i => IsInstrument(_doc.Sections[i])).ToList();
                SelectAllElements(null, null);
                Check("すべて選択でパネル本体以外が全部選ばれる", _selSet.Count == instruments.Count && !_selSet.Contains(_scene!.ThisSection) && Canvas.SelectedSet.Count == instruments.Count);
                Check("すべて選択が一覧にも伝わる", ElementList.SelectedItems.Count >= instruments.Count);

                Canvas.SelectInRect(new Rect(-1e6, -1e6, 2e6, 2e6), add: false);
                Check("全体を囲むとオブジェクトが全部選ばれる", _selSet.Count == _scene.Items.Count);
                var lamps2 = _doc.Sections.Select((x, i) => (x, i)).Where(t => t.x.Kind == SectionKind.PilotLamp && _scene.ItemFor(t.i) != null).Select(t => t.i).ToList();
                if (lamps2.Count >= 3)
                {
                    var r = BoundsOf(_scene.ItemFor(lamps2[0])!);
                    Canvas.SelectInRect(new Rect(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), add: false);
                    Check("1 つだけ囲むとそれが選ばれる", _selSet.Contains(lamps2[0]));
                    Canvas.SelectInRect(new Rect(-1e7, -1e7, 1, 1), add: false);
                    Check("何もない所を囲むとパネル本体", _selSet.Count == 1 && _selected == _scene.ThisSection);

                    // まとめて編集
                    var pick = lamps2.Take(3).ToList();
                    ApplySelection(pick[0], pick, null, SelectionSource.Other);
                    var t3 = Editor.Text;
                    OnMultiCommit("Layer", "7");
                    Check("まとめて編集で全部の Layer が変わる", pick.All(i => _doc.Sections[i].Get("Layer") == "7"));
                    OnUndo(this, null!);
                    Check("まとめて編集を元に戻す", Editor.Text == t3);

                    // 整列（ロック中でも使える）
                    ApplySelection(pick[0], pick, null, SelectionSource.Other);
                    Align(AlignKind.Left);
                    var lefts = pick.Select(i => Math.Round(BoundsOf(_scene!.ItemFor(i)!).Left)).Distinct().Count();
                    Check("左揃え", lefts == 1 || pick.All(i => _doc.Sections[i].Group != null));
                    OnUndo(this, null!);
                    Check("左揃えを元に戻す", Editor.Text == t3);
                    ApplySelection(pick[0], pick, null, SelectionSource.Other);
                    Align(AlignKind.DistributeY);
                    var bs = pick.Select(i => BoundsOf(_scene!.ItemFor(i)!)).OrderBy(b => b.Top).ToList();
                    double g1 = bs[1].Top - bs[0].Bottom, g2 = bs[2].Top - bs[1].Bottom;
                    Check("縦に等間隔", Math.Abs(g1 - g2) <= 1.01 || pick.Any(i => _doc.Sections[i].Group != null));
                    OnUndo(this, null!);
                    Check("等間隔を元に戻す", Editor.Text == t3);
                }
                else log.Add("     （表示灯が 3 つ未満なので、囲み選択の一部・まとめて編集・整列の確認は省略）");
            }

            // 保存（元と同じ中身で保存してバイト一致、.bak ができる）
            var orig = File.ReadAllBytes(_path!);
            Check("保存できる", Save(_path));
            Check("保存しても元とバイト一致", File.ReadAllBytes(_path!).AsSpan().SequenceEqual(orig));
            Check(".bak を作る", File.Exists(_path + ".bak"));
            Check("保存後は未保存マークなし", !IsDirty);
        }
        catch (Exception ex)
        {
            log.Add("EXCEPTION " + ex);
            fail++;
        }
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(reportPath, log);
        _savedText = Editor.Text;
        Close();
    }
}
