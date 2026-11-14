using System.IO;
using BvePanelEditor.Core.Psd;
using BvePanelEditor.Views;

namespace BvePanelEditor;

/// <summary>
/// 確認用: PSD 取り込み画面のレイヤー行の一括変更（--psdnodetest 結果.txt）。
/// フォルダの役割を一括で変えたとき、Changed が葉ごとに飛ばず 1 回だけになることを確かめる。
/// </summary>
public partial class MainWindow
{
    public void RunPsdNodeTest(string report)
    {
        var log = new List<string>();
        int fail = 0;
        void Check(string name, bool ok) { log.Add($"{(ok ? "OK  " : "FAIL")} {name}"); if (!ok) fail++; }
        try
        {
            var folder = new PsdLayerNode { Name = "フォルダ", Path = "フォルダ", IsFolder = true };
            int changed = 0;
            folder.Changed = () => changed++;
            var leaves = new List<PsdLayerNode>();
            for (int i = 0; i < 3; i++)
            {
                var leaf = new PsdLayerNode { Name = $"葉{i}", Path = $"フォルダ/葉{i}", ParentNode = folder };
                leaf.Changed = () => changed++;
                folder.Children.Add(leaf);
                leaves.Add(leaf);
            }
            folder.RoleValue = PsdRole.Lamp;
            Check("フォルダの一括変更で Changed は 1 回だけ", changed == 1);
            Check("葉の役割が全部そろう", leaves.All(l => l.Role == PsdRole.Lamp));

            changed = 0;
            leaves[0].Role = PsdRole.Base;
            Check("葉 1 つの変更では Changed が 1 回", changed == 1);

            changed = 0;
            folder.RoleValue = PsdRole.Number;
            Check("数字への一括変更（NumberSet の設定込み）でも Changed は 1 回だけ", changed == 1);
            Check("葉の NumberSet にフォルダ名が入る", leaves.All(l => l.NumberSet == "フォルダ"));
        }
        catch (Exception ex)
        {
            fail++;
            log.Add("例外: " + ex);
        }
        log.Add(fail == 0 ? "全部 OK" : $"失敗 {fail} 件");
        File.WriteAllLines(report, log);
        Close();
    }
}
