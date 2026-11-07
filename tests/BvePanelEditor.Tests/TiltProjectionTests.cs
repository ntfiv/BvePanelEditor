using BvePanelEditor.Core;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>Tilt（計器の面の傾き）をプレビューに描くための近似。</summary>
public sealed class TiltProjectionTests
{
    [Fact]
    public void NoTiltMeansNoProjection()
    {
        Assert.Null(TiltProjection.Create(100, 100, 0, 0, 3840, 1));
    }

    [Fact]
    public void CenterStaysPut()
    {
        var t = TiltProjection.Create(1353, 3499, 47, 0, 3840, 1.3)!;
        var (x, y) = t.Project(1353, 3499);
        Assert.Equal(1353, x, 6);
        Assert.Equal(3499, y, 6);
    }

    [Fact]
    public void TiltUpShrinksVerticallyAndTopMoreThanBottom()
    {
        var t = TiltProjection.Create(0, 0, 47, 0, 3840, 1)!;
        var top = t.Project(0, -180);
        var bottom = t.Project(0, 180);
        // 縦は cos47 ≒ 0.68 倍ほどに縮む
        Assert.InRange(-top.Y, 100, 140);
        Assert.InRange(bottom.Y, 100, 140);
        // 上側は奥なので、下側より小さく見える
        Assert.True(-top.Y < bottom.Y);
        // 上の左右も、下の左右より狭い
        var tl = t.Project(-100, -180); var tr = t.Project(100, -180);
        var bl = t.Project(-100, 180); var br = t.Project(100, 180);
        Assert.True(tr.X - tl.X < br.X - bl.X);
        // 横の線（v = 0）は変わらない
        var mid = t.Project(150, 0);
        Assert.Equal(150, mid.X, 6);
        Assert.Equal(0, mid.Y, 6);
    }

    [Fact]
    public void TiltLeftShrinksHorizontallyAndRightIsFarther()
    {
        var t = TiltProjection.Create(0, 0, 0, 40, 3840, 1)!;
        var left = t.Project(-180, 0);
        var right = t.Project(180, 0);
        Assert.True(right.X < 180 * Math.Cos(40 * Math.PI / 180) + 1e-6);
        Assert.True(right.X < -left.X);
        Assert.Equal(0, t.Project(0, 120).X, 6);
    }

    [Fact]
    public void LargerPerspectiveIsWeaker()
    {
        var strong = TiltProjection.Create(0, 0, 47, 0, 3840, 0.5)!;
        var weak = TiltProjection.Create(0, 0, 47, 0, 3840, 3)!;
        double Ratio(TiltProjection t) => -t.Project(0, -180).Y / t.Project(0, 180).Y;
        Assert.True(Ratio(strong) < Ratio(weak));
        Assert.True(Ratio(weak) < 1);
    }

    [Fact]
    public void MatchesBve6WithOriginAsVanishingPoint()
    {
        // E217（Resolution 3840・Perspective 1.3・Origin 2048, 2048）の速度計の調整用の目盛り（半径 184）。
        // この式（消失点 = Origin、距離 = Resolution × Perspective）が BVE6 の画面といちばん合った
        var t = TiltProjection.Create(1357, 3501, 47, 0, 3840, 1.3, 2048, 2048)!;
        var top = t.Project(1357, 3501 - 184);
        var bottom = t.Project(1357, 3501 + 184);
        // 消失点が上にあるので、奥（上側）の点は消失点のほうへ寄る（右上へ）。手前（下側）は離れる
        Assert.True(top.X > 1357);
        Assert.True(bottom.X < 1357);
        Assert.True(3501 - top.Y < bottom.Y - 3501);
    }

    [Fact]
    public void UnprojectReturnsToPlaneWithCamera()
    {
        var t = TiltProjection.Create(1353, 3499, 47, 10, 3840, 1.3, 2048, 2048)!;
        foreach (var (x, y) in new[] { (1353.0, 3499.0), (1400, 3350), (1200, 3600) })
        {
            var p = t.Project(x, y);
            var q = t.Unproject(p.X, p.Y);
            Assert.Equal(x, q.X, 6);
            Assert.Equal(y, q.Y, 6);
        }
    }

    [Fact]
    public void UnprojectReturnsToPlane()
    {
        var t = TiltProjection.Create(500, 400, 35, -20, 2048, 1.2)!;
        foreach (var (x, y) in new[] { (500.0, 400.0), (620, 300), (380, 520), (700, 410) })
        {
            var p = t.Project(x, y);
            var q = t.Unproject(p.X, p.Y);
            Assert.Equal(x, q.X, 6);
            Assert.Equal(y, q.Y, 6);
        }
    }
}
