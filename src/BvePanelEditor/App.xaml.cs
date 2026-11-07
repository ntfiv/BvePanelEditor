using System.IO;
using System.Windows;
using BvePanelEditor.Core;

namespace BvePanelEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        BveText.EnsureInitialized();
#if DEBUG
        // 確認用の起動オプション（--selftest など）はデバッグ版だけ。リリース版では使えない
        bool testRun = e.Args.Any(a => a.StartsWith("--", StringComparison.Ordinal));
#else
        const bool testRun = false;
#endif
        BvePanelEditor.MainWindow.IsTestRun = testRun;
        // 確認用の起動では、針の一覧を user の設定ではなく一時フォルダに保存する（毎回まっさら）
        if (testRun)
        {
            NeedleCatalog.StoreDir = Path.Combine(Path.GetTempPath(), "panelkun_test_needles_" + Environment.ProcessId);
            try { if (Directory.Exists(NeedleCatalog.StoreDir)) Directory.Delete(NeedleCatalog.StoreDir, true); } catch (IOException) { }
        }
        MainWindow? main = null;
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            if (testRun)
            {
                // 確認用の起動では、メッセージで止まらずにエラーを書いて終わる
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "panelkun_error.txt"), ex.Exception.ToString());
                Shutdown(1);
                return;
            }
            // 詳しい内容はログに書き、未保存の内容は控えを取ってから、短く知らせる
            CrashReport.Show(ex.Exception, main);
        };
        // 画面の外（別のスレッド）で起きたエラーは、ログにだけ書く
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { if (ex.ExceptionObject is Exception x) CrashReport.WriteLog(x); };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) => { CrashReport.WriteLog(ex.Exception); ex.SetObserved(); };
        var w = new MainWindow();
        main = w;
        w.Show();
#if DEBUG
        // 使い方: BvePanelEditor.exe [ファイル] [--select 番号] [--snapshot 出力.png]（--snapshot は確認用。画面を保存して終了）
        string? file = null, snapshot = null, selftest = null;
        (string Day, string Night, string Dir, string Report)? psdtest = null;
        (string Day, string Night, string Png)? psdwindow = null;
        (string Image, string Dir, string Report)? croptest = null;
        (string Psd, string Dir, string Report)? croppsdtest = null;
        (string Png, string Report)? viewlimit = null;
        (string Png, string Report)? pointtest = null;
        (string Dir, string Report)? digittest = null;
        string? intervaltest = null;
        (string Png, string Report)? layertest = null;
        (string Png, string Report)? renametest = null;
        (string Image, string Dir, string Report)? cropoverlaytest = null;
        (string Png, string Report)? needletest = null;
        (string Png, string Report)? covertest = null;
        (string Png, string Report)? frametest = null;
        string[]? mainpaneltest = null;
        (string Png, string Report)? hiddentest = null;
        string[]? tilttest = null;
        string[]? trashtest = null;
        string[]? digitimporttest = null;
        string? crashtest = null;
        string[]? listordertest = null;
        string[]? disabledtest = null;
        string[]? tiltshot = null;
        string[]? assigntest = null;
        string[]? droptest = null;
        string? pathnamestest = null;
        (string With, string Without, string Report)? opentest = null;
        int select = -1;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--snapshot" && i + 1 < e.Args.Length) snapshot = e.Args[++i];
            else if (e.Args[i] == "--select" && i + 1 < e.Args.Length && int.TryParse(e.Args[++i], out var n)) select = n;
            else if (e.Args[i] == "--view" && i + 1 < e.Args.Length) w.StartupView = e.Args[++i];
            else if (e.Args[i] == "--state" && i + 1 < e.Args.Length) w.StartupState = e.Args[++i];
            else if (e.Args[i] == "--selftest" && i + 1 < e.Args.Length) selftest = e.Args[++i];
            else if (e.Args[i] == "--psdtest" && i + 4 < e.Args.Length) psdtest = (e.Args[++i], e.Args[++i], e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--psdwindow" && i + 3 < e.Args.Length) psdwindow = (e.Args[++i], e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--croptest" && i + 3 < e.Args.Length) croptest = (e.Args[++i], e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--opentest" && i + 3 < e.Args.Length) opentest = (e.Args[++i], e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--pathnamestest" && i + 1 < e.Args.Length) pathnamestest = e.Args[++i];
            else if (e.Args[i] == "--droptest" && i + 5 < e.Args.Length) { droptest = e.Args[(i + 1)..(i + 6)]; i += 5; }
            else if (e.Args[i] == "--needletest" && i + 2 < e.Args.Length) needletest = (e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--covertest" && i + 2 < e.Args.Length) covertest = (e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--frametest" && i + 2 < e.Args.Length) frametest = (e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--tiltshot" && i + 2 < e.Args.Length) { tiltshot = e.Args[(i + 1)..(i + 3)]; i += 2; }
            else if (e.Args[i] == "--disabledtest" && i + 3 < e.Args.Length) { disabledtest = e.Args[(i + 1)..(i + 4)]; i += 3; }
            else if (e.Args[i] == "--listordertest" && i + 3 < e.Args.Length) { listordertest = e.Args[(i + 1)..(i + 4)]; i += 3; }
            else if (e.Args[i] == "--crashtest" && i + 1 < e.Args.Length) crashtest = e.Args[++i];
            else if (e.Args[i] == "--digitimporttest" && i + 3 < e.Args.Length) { digitimporttest = e.Args[(i + 1)..(i + 4)]; i += 3; }
            else if (e.Args[i] == "--trashtest" && i + 2 < e.Args.Length) { trashtest = e.Args[(i + 1)..(i + 3)]; i += 2; }
            else if (e.Args[i] == "--tilttest" && i + 3 < e.Args.Length) { tilttest = e.Args[(i + 1)..(i + 4)]; i += 3; }
            else if (e.Args[i] == "--hiddentest" && i + 2 < e.Args.Length) hiddentest = (e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--assigntest" && i + 3 < e.Args.Length) { assigntest = e.Args[(i + 1)..(i + 4)]; i += 3; }
            else if (e.Args[i] == "--mainpaneltest" && i + 3 < e.Args.Length) { mainpaneltest = e.Args[(i + 1)..(i + 4)]; i += 3; }
            else if (e.Args[i] == "--cropoverlaytest" && i + 3 < e.Args.Length) cropoverlaytest = (e.Args[++i], e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--renametest" && i + 2 < e.Args.Length) renametest = (e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--layertest" && i + 2 < e.Args.Length) layertest = (e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--intervaltest" && i + 1 < e.Args.Length) intervaltest = e.Args[++i];
            else if (e.Args[i] == "--digittest" && i + 2 < e.Args.Length) digittest = (e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--pointtest" && i + 2 < e.Args.Length) pointtest = (e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--viewlimit" && i + 2 < e.Args.Length) viewlimit = (e.Args[++i], e.Args[++i]);
            else if (e.Args[i] == "--croppsdtest" && i + 3 < e.Args.Length) croppsdtest = (e.Args[++i], e.Args[++i], e.Args[++i]);
            else file ??= e.Args[i];
        }
        if (file != null) w.OpenPath(file, BvePanelEditor.MainWindow.OpenSource.Startup);
        w.Dispatcher.BeginInvoke(() =>
        {
            if (select >= 0) w.SelectForStartup(select);
            w.ApplyStartupOptionsPublic();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (snapshot != null) w.SnapshotAndClose(snapshot);
        // 夜の PSD を使わないときは "-"
        if (psdtest is { } pt)
            w.Dispatcher.BeginInvoke(() => w.RunPsdTest(pt.Day, pt.Night == "-" ? null : pt.Night, pt.Dir, pt.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (psdwindow is { } pw)
            w.Dispatcher.BeginInvoke(() => w.ShowPsdWindowSnapshot(pw.Day, pw.Night == "-" ? null : pw.Night, pw.Png), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (croptest is { } ct)
            w.Dispatcher.BeginInvoke(() => w.RunCropTest(ct.Image, ct.Dir, ct.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (opentest is { } ot)
            w.Dispatcher.BeginInvoke(() => w.RunOpenTest(ot.With, ot.Without, ot.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (pathnamestest is { } pnt)
            w.Dispatcher.BeginInvoke(() => w.RunPathNamesTest(pnt), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (droptest is { } dt2)
            w.Dispatcher.BeginInvoke(() => w.RunDropTest(dt2[0], dt2[1], dt2[2], dt2[3], dt2[4]), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (assigntest is { } at)
            w.Dispatcher.BeginInvoke(() => w.RunAssignTest(at[0], at[1], at[2]), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (tiltshot is { } ts)
            w.Dispatcher.BeginInvoke(() => w.RunTiltShot(ts[0], ts[1]), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (disabledtest is { } dst)
            w.Dispatcher.BeginInvoke(() => w.RunDisabledTest(dst[0], dst[1], dst[2]), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (listordertest is { } lot)
            w.Dispatcher.BeginInvoke(() => w.RunListOrderTest(lot[0], lot[1], lot[2]), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (crashtest != null)
            w.Dispatcher.BeginInvoke(() => w.RunCrashTest(crashtest), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (digitimporttest is { } dit)
            w.Dispatcher.BeginInvoke(() => w.RunDigitImportTest(dit[0], dit[1], dit[2]), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (trashtest is { } trt)
            w.Dispatcher.BeginInvoke(() => w.RunTrashTest(trt[0], trt[1]), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (tilttest is { } tt)
            w.Dispatcher.BeginInvoke(() => w.RunTiltTest(tt[0], tt[1], tt[2]), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (hiddentest is { } ht)
            w.Dispatcher.BeginInvoke(() => w.RunHiddenTest(ht.Png, ht.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (mainpaneltest is { } mpt)
            w.Dispatcher.BeginInvoke(() => w.RunMainPanelTest(mpt[0], mpt[1], mpt[2]), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (frametest is { } ft)
            w.Dispatcher.BeginInvoke(() => w.RunFrameTest(ft.Png, ft.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (covertest is { } cvt)
            w.Dispatcher.BeginInvoke(() => w.RunCoverTest(cvt.Png, cvt.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (needletest is { } nt)
            w.Dispatcher.BeginInvoke(() => w.RunNeedleTest(nt.Png, nt.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (cropoverlaytest is { } cot)
            w.Dispatcher.BeginInvoke(() => w.RunCropOverlayTest(cot.Image, cot.Dir, cot.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (renametest is { } rt)
            w.Dispatcher.BeginInvoke(() => w.RunRenameTest(rt.Png, rt.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (layertest is { } lt)
            w.Dispatcher.BeginInvoke(() => w.RunLayerTest(lt.Png, lt.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (intervaltest is { } itp)
            w.Dispatcher.BeginInvoke(() => w.RunIntervalTest(itp), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (digittest is { } dt)
            w.Dispatcher.BeginInvoke(() => w.RunDigitTest(dt.Dir, dt.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (pointtest is { } pt2)
            w.Dispatcher.BeginInvoke(() => w.RunPointTest(pt2.Png, pt2.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (viewlimit is { } vl)
            w.Dispatcher.BeginInvoke(() => w.RunViewLimitTest(vl.Png, vl.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (croppsdtest is { } cpt)
            w.Dispatcher.BeginInvoke(() => w.RunCropPsdTest(cpt.Psd, cpt.Dir, cpt.Report), System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (selftest != null)
            w.Dispatcher.BeginInvoke(() => w.RunSelfTest(selftest), System.Windows.Threading.DispatcherPriority.ContextIdle);
#else
        // リリース版: 引数はパネルファイル（などの）パスだけ
        var path = e.Args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (path != null) w.OpenPath(path, BvePanelEditor.MainWindow.OpenSource.Startup);
#endif
    }
}
