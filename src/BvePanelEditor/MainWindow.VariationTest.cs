using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BvePanelEditor.Core;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// 確認用: バリエーション展開（--variationtest 作業フォルダ 画面.png 結果.txt）。作業フォルダにマスターと見本のパネルを作って使う。
/// 一覧を作る → 表示の切り替え → 目のマーク・右の欄のチェックで入れる・外す → 追加したときの扱い → 書き出し → 保存して開き直す → 既存のパネルから作る。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 確認用: 実物のパネルからマスターを作ってみる（--variationimport マスター 画面.png 結果.txt 名前=パネル ...）。
    /// 取り込んだ結果（共通・一部だけのオブジェクト、知らせ）と、各バリエーションを書き出し直したときに元のファイルと同じかを書く。
    /// </summary>
    public async void RunVariationImport(string master, string png, string report, string[] pairs)
    {
        var log = new List<string>();
        try
        {
            var files = pairs.Select(p => p.Split('=', 2)).Select(a => (a[0], Path.GetFullPath(a[1]))).ToList();
            _autoConfirm = _ => true;
            var r = ImportVariations(files, master);
            await Task.Delay(300);
            if (r == null) { log.Add("取り込めませんでした"); }
            else
            {
                log.Add($"共通 {r.Common} 個・一部だけ {r.Partial} 個・ずれ {r.Mismatches.Count} 件");
                foreach (var n in r.Notes) log.Add("知らせ: " + n);
                foreach (var m in r.Mismatches) log.Add("書き出し直すと違う: " + m);
                log.Add("--- オブジェクト（共通でないもの）");
                for (int i = 0; i < _doc!.Sections.Count; i++)
                    if (_doc.Sections[i].VariationCond is { } c) log.Add($"#{i} {(_doc.Sections[i].Title.Length > 0 ? _doc.Sections[i].Title : _doc.Sections[i].HeaderRaw)} {{{c}}}");
                log.Add("--- 書き出し直したものと元のファイル");
                var masterDir = Path.GetDirectoryName(Path.GetFullPath(master))!;
                foreach (var (name, path) in files)
                {
                    var (orig, _) = BveText.Read(path);
                    var back = _doc.ExportVariation(name, masterDir, Path.GetDirectoryName(path));
                    File.WriteAllText(Path.ChangeExtension(report, $".{name}.back.txt"), back);
                    File.WriteAllText(Path.ChangeExtension(report, $".{name}.orig.txt"), orig);
                    int diff = orig.Split('\n').Zip(back.Split('\n')).Count(t => t.First != t.Second) + Math.Abs(orig.Split('\n').Length - back.Split('\n').Length);
                    log.Add($"{name}: {(orig == back ? "1 字も違わない" : $"違う行 {diff} 行（オブジェクト・値・並びは {(r.Mismatches.Contains(name) ? "違う" : "同じ")}）")}");
                }
                SetViewVariation(files[^1].Item1);
                RightTabs.SelectedIndex = RightTabs.Items.Count - 1;
                Canvas.FitAll();
                UpdateLayout();
                await Task.Delay(300);
                SaveWindowPng(this, png);
                // 1 行表示・問題の欄を畳んだ画面も
                SetCompactList(true, save: false);
                SetProblemsFolded(true, save: false);
                UpdateLayout();
                await Task.Delay(300);
                SaveWindowPng(this, Path.ChangeExtension(png, ".compact.png"));
                SetCompactList(false, save: false);
                SetProblemsFolded(false, save: false);
            }
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); }
        _autoConfirm = null;
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }

    public async void RunVariationTest(string workDir, string png, string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            var masterDir = Path.Combine(workDir, "master");
            Directory.CreateDirectory(masterDir);
            Directory.CreateDirectory(Path.Combine(workDir, "img"));
            var px = new byte[40 * 40 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 200; px[i + 3] = 255; }
            foreach (var n in new[] { "a", "b", "c" }) File.WriteAllBytes(Path.Combine(workDir, "img", n + ".png"), Core.Psd.PngWriter.Encode(40, 40, px));
            string Lamp(string name, string subj, string img, int x) => $"\r\n;{name}\r\n[PilotLamp]\r\nSubject = {subj}\r\nLocation = {x}, 100\r\nDaytimeImage = ..\\img\\{img}.png\r\n";
            var text = "BveTs Instrument Panel 2.00\r\n\r\n[This]\r\nResolution = 400\r\n" + Lamp("共通", "true", "a", 50) + Lamp("ワンマン灯", "ats200", "b", 150) + Lamp("雨", "ats150", "c", 250);
            var master = Path.Combine(masterDir, "_panel_master.txt");
            File.WriteAllText(master, text);
            LoadPanel(master, null);
            await Task.Delay(300);
            _autoConfirm = _ => true;
            string Conds() => string.Join(",", _doc!.Sections.Skip(1).Select(s => s.Title + (s.VariationCond != null ? "{" + s.VariationCond + "}" : "")));

            // 0. オブジェクトの一覧の 1 行表示・問題の欄を畳む
            SetCompactList(true, save: false);
            Check("1 行表示では 2 行目を出さない", Rows.Where(r => !r.IsHeader && r.HasTitle).All(r => r.SecondaryVisibility == Visibility.Collapsed));
            SetCompactList(false, save: false);
            Check("1 行表示を外すと 2 行目を出す", Rows.Where(r => !r.IsHeader && r.HasTitle).All(r => r.SecondaryVisibility == Visibility.Visible));
            SetProblemsFolded(true, save: false);
            Check("問題の欄を畳める", ProblemList.Visibility == Visibility.Collapsed && ProblemFold.Text == "▶");
            SetProblemsFolded(false, save: false);
            Check("問題の欄を開ける", ProblemList.Visibility == Visibility.Visible && ProblemRowDef.Height.Value == 160);

            // バリエーションのタブを開くと「バリエーション編集モード」（位置ロック固定・左上のボタンを隠す）
            SetLocked(false);
            RightTabs.SelectedIndex = RightTabs.Items.Count - 1;
            Check("バリエーションのタブではバリエーション編集モード（位置ロック・ボタンを隠す）", Canvas.PositionLocked && LockButton.Visibility == Visibility.Collapsed
                && AllLampsCheck.Visibility == Visibility.Collapsed && VariationModeBadge.Visibility == Visibility.Visible);
            SetLocked(false);
            Check("バリエーション編集モードでは位置ロックを外せない", Canvas.PositionLocked);
            Check("マスター表示のときは「バリエーション編集モード」を赤で出す", VariationModeBadge.Background == BadgeMaster && VariationModeText.Text.Contains("マスター"));
            RightTabs.SelectedIndex = 0;
            Check("ほかのタブに戻ると元の位置ロック（外していた）とボタンに戻る", !Canvas.PositionLocked && LockButton.Visibility == Visibility.Visible && VariationModeBadge.Visibility == Visibility.Collapsed);
            // モード中にファイルを開いた（ロックに戻す）なら、抜けてもロックのまま
            RightTabs.SelectedIndex = RightTabs.Items.Count - 1;
            SetLocked(true); // ファイルを開いたときと同じ
            RightTabs.SelectedIndex = 0;
            Check("モード中にファイルを開いたら、抜けたあとも位置ロックのまま", Canvas.PositionLocked && !Canvas.ForceLocked);
            RightTabs.SelectedIndex = RightTabs.Items.Count - 1;

            // 1. 一覧を作る（追加して名前を変える）
            OnAddVariation();
            _varName.Text = "ツーマン"; CommitVariationName();
            OnAddVariation();
            _varName.Text = "ワンマン"; CommitVariationName();
            _varName.Text = "あ い"; CommitVariationName();
            Check("空白のある名前は使えない", _doc!.VariationNames.SequenceEqual(["ツーマン", "ワンマン"]));
            Check("一覧の行がファイルに入る", Editor.Text.Contains(";@バリエーション: ツーマン = panel_ツーマン.txt") && Editor.Text.Contains(";@バリエーション: ワンマン = panel_ワンマン.txt"));
            Check("バリエーションがあると表示の切り替えが出る", VariationViewBox.Visibility == Visibility.Visible && VariationViewBox.Items.Count == 3);

            // 2. ワンマンを表示して、目のマークで「ワンマン灯」を入れる・外す
            SetViewVariation("ツーマン");
            var row = Rows.First(r => !r.IsHeader && _doc.Sections[r.Index].Title == "ワンマン灯");
            Check("バリエーションの表示中は目のマークが「入れる・外す」になる", row.VariationMode && row.InVariation && row.EyeTip.Contains("バリエーション"));
            ToggleMembership([row.Index], "ツーマン");
            log.Add("     ツーマンから外したあと: " + Conds());
            Check("目のマークでツーマンから外す（ワンマンだけに入る）", Conds() == "共通,ワンマン灯{ワンマン},雨");
            Check("ツーマンの表示では描かない（一覧では薄く・目を閉じる）", _scene!.ItemFor(2)!.Hidden && Rows.First(r => r.Index == 2) is { InVariation: false, RowOpacity: < 1 });
            Check("ファイルには「; @group ワンマン」〜「; @end」で書く", Editor.Text.Contains("; @group ワンマン\r\n;ワンマン灯") && Editor.Text.Contains("; @end\r\n;雨"));
            OnUndo(this, null!);
            Check("Ctrl+Z で戻せる", Conds() == "共通,ワンマン灯,雨");
            OnRedo(this, null!);

            // 3. 右の欄のチェック: 「雨」をワンマンだけに
            ApplySelection(3, [3], null, SelectionSource.Other);
            var cb = _varChecks.Children.OfType<CheckBox>().First(c => (string)c.Content == "ツーマン");
            Check("右の欄に、選んだオブジェクトが入るバリエーションのチェックが出る", cb.IsChecked == true && _varChecks.Children.Count == 2);
            cb.IsChecked = false;
            cb.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check("チェックを外すとそのバリエーションから外れる", Conds() == "共通,ワンマン灯{ワンマン},雨{ワンマン}");

            // 3'. グループの見出し: 中のオブジェクトが全部入っているバリエーションを出す
            _doc.MakeGroup([2, 3], "ワンマン機器");
            CommitModel();
            var gh = Rows.First(r => r.IsHeader && r.GroupName == "ワンマン機器");
            Check("グループの中のオブジェクトが全部ワンマンに入っていれば、見出しに「ワンマン」と出す", gh.VariationText == "ワンマン" && gh.VariationTextVisibility == Visibility.Visible);
            UpdateLayout();
            SaveWindowPng(this, Path.ChangeExtension(png, ".group.png"));
            _doc.SetMembership([3], "ツーマン", true);
            CommitModel();
            Check("一部だけなら、全部が入っているバリエーションだけを出す", Rows.First(r => r.IsHeader && r.GroupName == "ワンマン機器").VariationText == "ワンマン");
            _doc.SetMembership([2], "ツーマン", true);
            CommitModel();
            Check("全部のバリエーションに入っていれば（共通）出さない", Rows.First(r => r.IsHeader && r.GroupName == "ワンマン機器").VariationText == "");
            OnUndo(this, null!); OnUndo(this, null!); OnUndo(this, null!);
            Check("Ctrl+Z で戻せる", Conds() == "共通,ワンマン灯{ワンマン},雨{ワンマン}" && _doc.GroupNames().Count == 0);

            // 4. バリエーションの表示中に複製したオブジェクトは、そのバリエーションだけに入る
            SetViewVariation("ツーマン");
            ApplySelection(1, [1], null, SelectionSource.Other);
            OnDuplicate(null, null);
            Check("表示中のバリエーションで複製すると、そのバリエーションだけに入る", _doc.Sections[2].VariationCond == "ツーマン" && MessageText.Text.Contains("ツーマン"));
            ApplySelection(2, [2], null, SelectionSource.Other);
            _doc.Sections[2].SetTitle("ツーマン灯");
            CommitModel();
            SetViewVariation(null);
            UpdateLayout();
            SaveWindowPng(this, Path.ChangeExtension(png, ".master.png"));
            SetViewVariation("ワンマン");
            UpdateLayout();
            SaveWindowPng(this, png);

            // 5. 書き出す
            var written = ExportVariations(_doc.Variations.ToList());
            Check("すべて書き出す（2 個）", written.Count == 2 && written.All(File.Exists));
            var two = File.ReadAllText(Path.Combine(masterDir, "panel_ツーマン.txt"));
            var one = File.ReadAllText(Path.Combine(masterDir, "panel_ワンマン.txt"));
            string Titles(string t) => string.Join(",", PanelDocument.Parse(t).Sections.Skip(1).Select(s => s.Title));
            log.Add($"     ツーマン: {Titles(two)}　ワンマン: {Titles(one)}");
            Check("ツーマンには入るオブジェクトだけ", Titles(two) == "共通,ツーマン灯");
            Check("ワンマンには入るオブジェクトだけ", Titles(one) == "共通,ワンマン灯,雨");
            Check("書き出したパネルには印も一覧の行もない", !two.Contains("@group") && !two.Contains("@end") && !one.Contains("@バリエーション"));

            // 6. 出力先を別のフォルダにすると画像の場所を直す
            SelectVariationRow(1);
            _varFile.Text = "..\\out\\panel_1man.txt"; CommitVariationFile();
            ExportVariations([_doc.Variations[1]]);
            var moved = PanelDocument.Parse(File.ReadAllText(Path.Combine(workDir, "out", "panel_1man.txt")));
            Check("別のフォルダへ書き出すと画像の場所を直す（同じ画像を指す）", moved.Sections.First(s => s.Title == "雨").Get("DaytimeImage") == "..\\img\\c.png");

            // 7. 保存して開き直しても同じ
            Check("保存できる", Save(_path));
            var saved = Editor.Text;
            LoadPanel(master, null);
            Check("開き直しても一覧と条件が同じ", Editor.Text == saved && _doc.VariationNames.SequenceEqual(["ツーマン", "ワンマン"]) && _doc.GroupWarnings.Count == 0);

            // 8'. 今の表示状態をバリエーションとして追加: ワンマンを表示して「雨」を目のマーク（マスター表示の非表示）で隠す → 雨なしのバリエーション
            SetViewVariation("ワンマン");
            var varRows = (List<string>)_varList.ItemsSource;
            Check("一覧のいちばん上はマスター", varRows[0].Contains("マスター（すべて）") && varRows[0].Contains("_panel_master.txt"));
            Check("バリエーション表示のときは「バリエーション編集モード」を青で出す", VariationModeBadge.Background == BadgeVariation && VariationModeText.Text.Contains("ワンマン"));
            Check("表示中のバリエーションには一覧の左に ▶ を付ける", varRows[2].StartsWith("▶") && !varRows[1].StartsWith("▶") && !varRows[0].StartsWith("▶"));
            _varList.SelectedIndex = 0;
            Check("マスターの行では名前・出力ファイル・削除は使えない（複製はできる）", !_varEdit.IsEnabled && _varDelete?.IsEnabled == false && _varDuplicate?.IsEnabled == true && SelectedVariation == null);
            OnDuplicateVariation();
            var allName = _doc.Variations[^1].Name;
            Check("マスターを複製すると、すべてのオブジェクトが入るバリエーションになる", allName == "すべて" && _doc.Sections.All(s => s.InVariation(allName)));
            OnUndo(this, null!);
            Check("Ctrl+Z で戻せる", !_doc.VariationNames.Contains("すべて"));
            int rainIdx = _doc.Sections.FindIndex(s => s.Title == "雨");
            _doc.Sections[rainIdx].ViewHidden = true;
            OnAddVariation();
            var added = _doc.Variations[^1].Name;
            log.Add($"     表示状態から足した「{added}」: " + Conds());
            Check("今の表示状態（ワンマンのオブジェクトから、隠した雨を除く）がバリエーションになる",
                _doc.Sections[rainIdx].InVariation("ワンマン") && !_doc.Sections[rainIdx].InVariation(added)
                && _doc.Sections.First(s => s.Title == "ワンマン灯").InVariation(added) && !_doc.Sections.First(s => s.Title == "ツーマン灯").InVariation(added));
            OnUndo(this, null!);
            _doc.Sections[rainIdx].ViewHidden = false;
            SetViewVariation(null);

            // 8. 複製・名前の変更・削除
            SelectVariationRow(1);
            OnDuplicateVariation();
            Check("複製すると同じオブジェクトが入る", _doc.VariationNames.SequenceEqual(["ツーマン", "ワンマン", "ワンマン_2"]) && Conds().Contains("雨{ワンマン, ワンマン_2}"));
            _varName.Text = "ワンマン雨"; CommitVariationName();
            Check("名前を変えると条件も変わる", Conds().Contains("雨{ワンマン, ワンマン雨}"));
            SelectVariationRow(0);
            ChoiceDialog.TestAnswer = (_, _) => 1; // そのバリエーションだけのオブジェクトは共通にする
            OnDeleteVariation();
            ChoiceDialog.TestAnswer = null;
            log.Add("     ツーマンを消したあと: " + Conds());
            Check("消すと、そのバリエーションだけのオブジェクトを共通にできる", !_doc.VariationNames.Contains("ツーマン") && Conds().StartsWith("共通,ツーマン灯,"));
            OnUndo(this, null!);
            Check("Ctrl+Z で戻せる", _doc.VariationNames.Contains("ツーマン"));

            // 8''. 精査で直したところ
            // 出力ファイルが同じバリエーションは書き出さない
            var fileBefore = _doc.Variations[1].File;
            _doc.Variations[1].File = _doc.Variations[0].File;
            CommitModel();
            var stamp = File.GetLastWriteTimeUtc(Path.Combine(masterDir, _doc.Variations[0].File));
            Check("出力ファイルが同じバリエーションがあると書き出さない", ExportVariations(_doc.Variations.ToList()).Count == 0
                && File.GetLastWriteTimeUtc(Path.Combine(masterDir, _doc.Variations[0].File)) == stamp && MessageText.Text.Contains("同じ"));
            _doc.Variations[1].File = fileBefore;
            CommitModel();
            // 表示中のバリエーションの名前を変えると、左上の表示も変わる
            SetViewVariation(_doc.Variations[1].Name);
            SelectVariationRow(1);
            var oldName = _doc.Variations[1].Name;
            _varName.Text = oldName + "X"; CommitVariationName();
            Check("表示中のバリエーションの名前を変えると、左上の表示も変わる", VariationModeText.Text.Contains(oldName + "X"));
            OnUndo(this, null!);
            Check("Ctrl+Z で名前が戻ると、左上の表示も戻る", !VariationModeText.Text.Contains(oldName + "X"));
            // バリエーション表示中の Ctrl+H（右クリックの非表示）は、目のマークと同じく入れる・外す
            SetViewVariation(_doc.Variations[1].Name);
            int lampIdx = _doc.Sections.FindIndex(s => s.Title == "ワンマン灯");
            ApplySelection(lampIdx, [lampIdx], null, SelectionSource.Other);
            ToggleHiddenOrMembership();
            Check("バリエーション表示中の Ctrl+H は、そのバリエーションから外す（画面だけの非表示にしない）",
                !_doc.Sections[lampIdx].InVariation(_doc.Variations[1].Name) && !_doc.Sections[lampIdx].ViewHidden);
            OnUndo(this, null!);
            SetViewVariation(null);
            // バリエーション編集モードでは、追加したばかりのオブジェクトも動かせない
            Check("バリエーション編集モードでは、追加したばかりのオブジェクトも位置ロックのまま", Canvas.ForceLocked && Canvas.PositionLocked);

            // 9. 一覧にない名前は問題の欄に出る
            _doc.Sections[1].VariationCond = "雨天";
            CommitModel();
            Check("一覧にないバリエーション名は問題に出る", _scene!.Problems.Any(p => p.Message.Contains("「雨天」は一覧にありません")));
            OnUndo(this, null!);

            // 9'. 既存のパネルから作る画面は、パネルファイルだけを受け付ける
            var vehicle = Path.Combine(workDir, "Vehicle.txt");
            File.WriteAllText(vehicle, "BveTs Vehicle 2.00\r\nPanel = master\\_panel_master.txt\r\n");
            var notes = Path.Combine(workDir, "memo.txt");
            File.WriteAllText(notes, "メモ\r\n");
            var iw = new VariationImportWindow(this);
            iw.AddFiles([Path.Combine(masterDir, "panel_ツーマン.txt"), vehicle, notes, Path.Combine(workDir, "img", "a.png")]);
            log.Add("     取り込む画面の知らせ: " + iw.NoteForTest);
            Check("取り込む画面は、パネルファイルでないもの（車両ファイル・ほかのテキスト・画像）を外して知らせる",
                iw.FilesForTest.Count == 1 && iw.NoteForTest.Contains("Vehicle.txt（車両ファイル）") && iw.NoteForTest.Contains("memo.txt") && iw.NoteForTest.Contains("a.png"));
            iw.Close();
            var before = _path;
            Check("パネルファイルでないものは取り込まない", ImportVariations([("A", Path.Combine(masterDir, "panel_ツーマン.txt")), ("B", vehicle)], Path.Combine(workDir, "_x_master.txt")) == null
                && !File.Exists(Path.Combine(workDir, "_x_master.txt")) && _path == before);

            // 10. 既存のパネルからマスターを作る（別々のフォルダの 3 枚）
            var src3 = Path.Combine(workDir, "rain", "panel.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(src3)!);
            File.WriteAllText(src3, one.Replace("..\\img", "..\\img").Replace(";雨", ";雨天用の灯\r\n[PilotLamp]\r\nSubject = ats151\r\nLocation = 300, 100\r\nDaytimeImage = ..\\img\\c.png\r\n\r\n;雨"));
            var srcs = new List<(string, string)> { ("ツーマン", Path.Combine(masterDir, "panel_ツーマン.txt")), ("ワンマン", Path.Combine(workDir, "out", "panel_1man.txt")), ("雨", src3) };
            var master2 = Path.Combine(workDir, "_imported_master.txt");
            var result = ImportVariations(srcs, master2);
            log.Add("     取り込み: " + Conds() + $"　知らせ: {string.Join(" ／ ", result?.Notes ?? [])}");
            Check("既存のパネルからマスターを作って開く", result != null && _path == Path.GetFullPath(master2) && _doc.VariationNames.SequenceEqual(["ツーマン", "ワンマン", "雨"]));
            Check("書き出し直すと元と同じ（確かめた結果）", result != null && result.Mismatches.Count == 0);
            Check("共通・一部だけのオブジェクトに分かれる", Conds() == "共通,ツーマン灯{ツーマン},ワンマン灯{ワンマン, 雨},雨天用の灯{雨},雨{ワンマン, 雨}");
            Check("出力ファイルは元のパネル（マスターからの場所）", _doc.Variations[2].File == "rain\\panel.txt" && _doc.Variations[0].File == "master\\panel_ツーマン.txt");
            Check("画像の場所はマスターからのものに直す", _doc.Sections.First(s => s.Title == "雨天用の灯").Get("DaytimeImage") == "img\\c.png");
            var rain = PanelDocument.Parse(_doc.ExportVariation("雨", Path.GetDirectoryName(master2), Path.GetDirectoryName(src3)));
            Check("雨を書き出すと元のパネルと同じ中身", rain.Sections.Select(VariationMerge.Key).SequenceEqual(PanelDocument.Parse(File.ReadAllText(src3)).Sections.Select(VariationMerge.Key)));
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fail++; }
        _autoConfirm = null;
        ChoiceDialog.TestAnswer = null;
        log.Add(fail == 0 ? "ALL PASSED" : $"{fail} FAILED");
        File.WriteAllLines(report, log);
        _savedText = Editor.Text;
        Close();
    }
}
