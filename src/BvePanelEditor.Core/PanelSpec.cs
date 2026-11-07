namespace BvePanelEditor.Core;

public enum KeyType { Text, Int, Number, Point, Color, Image, Subject, Bool, Angle }

/// <summary>セクションに書けるキーの定義（プロパティ欄の並びと説明）。</summary>
public sealed record KeySpec(string Name, string Label, KeyType Type, string Help, bool V201Only = false);

public static class PanelSpec
{
    public static readonly KeySpec[] This =
    [
        new("Resolution", "解像度", KeyType.Int, "ズーム 1 倍のとき画面の横幅に入るパネル画像のピクセル数"),
        new("Left", "表示範囲 左", KeyType.Int, "視点移動で表示できる範囲の左端（パネル画像の座標）"),
        new("Top", "表示範囲 上", KeyType.Int, "視点移動で表示できる範囲の上端"),
        new("Right", "表示範囲 右", KeyType.Int, "視点移動で表示できる範囲の右端"),
        new("Bottom", "表示範囲 下", KeyType.Int, "視点移動で表示できる範囲の下端"),
        new("DaytimeImage", "昼画像", KeyType.Image, "昼間用のパネル画像（このファイルからの相対パス）"),
        new("NighttimeImage", "夜画像", KeyType.Image, "夜間用のパネル画像"),
        new("TransparentColor", "透過色", KeyType.Color, "透明にする色（6 桁の 16 進数）"),
        new("Center", "中心", KeyType.Point, "既定の視点で画面の中央になる座標"),
        new("Origin", "消失点", KeyType.Point, "消失点の座標"),
        new("Perspective", "パース", KeyType.Number, "Tilt を付けた計器の遠近感の強さ。大きいほど弱い（既定 1）。プレビューの遠近は近似です", true),
    ];

    public static readonly KeySpec[] PilotLamp =
    [
        new("Subject", "状態量", KeyType.Subject, "表示する状態量。0 以外のとき点灯"),
        new("Location", "位置（左上）", KeyType.Point, "パネル画像上で表示灯の左上の座標"),
        new("DaytimeImage", "昼画像", KeyType.Image, "昼間用の画像"),
        new("NighttimeImage", "夜画像", KeyType.Image, "夜間用の画像"),
        new("TransparentColor", "透過色", KeyType.Color, "透明にする色"),
        new("Layer", "レイヤー", KeyType.Int, "描画順。大きいほど手前（ベース画像は 0）"),
    ];

    public static readonly KeySpec[] Needle =
    [
        new("Subject", "状態量", KeyType.Subject, "表示する状態量"),
        new("Location", "位置（回転中心）", KeyType.Point, "パネル画像上の回転中心"),
        new("DaytimeImage", "昼画像", KeyType.Image, "昼間用の針画像（上向きに描く）"),
        new("NighttimeImage", "夜画像", KeyType.Image, "夜間用の針画像"),
        new("Color", "色", KeyType.Color, "面の色（画像に掛け合わせる）"),
        new("TransparentColor", "透過色", KeyType.Color, "透明にする色"),
        new("Origin", "画像の回転中心", KeyType.Point, "針画像上の回転中心（省略時は画像の中央として表示）"),
        new("Layer", "レイヤー", KeyType.Int, "描画順。大きいほど手前"),
        new("Radius", "半径", KeyType.Number, "針の半径（画像の回転中心から上端までがこの長さになる）"),
        new("InitialAngle", "最小時の角度", KeyType.Angle, "最小値を指すときの角度（12 時が 0、時計回りが正）"),
        new("LastAngle", "最大時の角度", KeyType.Angle, "最大値を指すときの角度。1 周以上回る針は InitialAngle + 360"),
        new("Minimum", "最小値", KeyType.Number, "最小値"),
        new("Maximum", "最大値", KeyType.Number, "最大値"),
        new("NaturalFreq", "固有角振動数", KeyType.Number, "針の固有角振動数（揺れの速さ）"),
        new("DampingRatio", "減衰比", KeyType.Number, "針の減衰比（1 で行き過ぎなし）"),
        new("StopPin", "ストップピン", KeyType.Bool, "針の動きを最小〜最大に制限する（True/False、既定 False）", true),
        new("Tilt", "傾き", KeyType.Point, "回転面の傾き angleX, angleY（deg）。angleX は上向き、angleY は左向きが正。プレビューは近似です", true),
    ];

    public static readonly KeySpec[] DigitalGauge =
    [
        new("Subject", "状態量", KeyType.Subject, "表示する状態量"),
        new("Location", "位置（中心）", KeyType.Point, "パネル画像上の回転中心"),
        new("Color", "消灯の色", KeyType.Color, "LED が消えている部分の色。パネルの絵の上で、今の値から最大までをこの色で覆います（点いている部分はパネルの絵が見えます）"),
        new("Layer", "レイヤー", KeyType.Int, "描画順。大きいほど手前"),
        new("Radius", "半径", KeyType.Number, "ゲージの半径"),
        new("InitialAngle", "最小時の角度", KeyType.Angle, "最小のときの角度（12 時が 0、時計回りが正）"),
        new("LastAngle", "最大時の角度", KeyType.Angle, "最大のときの角度"),
        new("Minimum", "最小値", KeyType.Number, "最小値"),
        new("Maximum", "最大値", KeyType.Number, "最大値"),
        new("Step", "分解能", KeyType.Number, "表示の刻み"),
        new("Tilt", "傾き", KeyType.Point, "表示面の傾き angleX, angleY（deg）。angleX は上向き、angleY は左向きが正。プレビューは近似です", true),
    ];

    public static readonly KeySpec[] DigitalNumber =
    [
        new("Subject", "状態量", KeyType.Subject, "表示する状態量。値の番号のコマを表示"),
        new("Location", "位置（左上）", KeyType.Point, "パネル画像上で数字の左上の座標"),
        new("DaytimeImage", "昼画像", KeyType.Image, "昼間用の数字画像（コマを縦に並べたもの）"),
        new("NighttimeImage", "夜画像", KeyType.Image, "夜間用の数字画像"),
        new("TransparentColor", "透過色", KeyType.Color, "透明にする色"),
        new("Layer", "レイヤー", KeyType.Int, "描画順。大きいほど手前"),
        new("Interval", "1 コマの高さ", KeyType.Int, "数字画像 1 コマの高さ（ピクセル）"),
    ];

    public static KeySpec[] For(SectionKind kind) => kind switch
    {
        SectionKind.This => This,
        SectionKind.PilotLamp => PilotLamp,
        SectionKind.Needle => Needle,
        SectionKind.DigitalGauge => DigitalGauge,
        SectionKind.DigitalNumber => DigitalNumber,
        _ => [],
    };

    public static string KindLabel(SectionKind kind) => kind switch
    {
        SectionKind.This => "パネル本体",
        SectionKind.PilotLamp => "表示灯",
        SectionKind.Needle => "指針",
        SectionKind.DigitalGauge => "円形ゲージ",
        SectionKind.DigitalNumber => "デジタル数字",
        _ => "不明",
    };
}
