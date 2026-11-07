using BvePanelEditor.Core;
using Xunit;

namespace BvePanelEditor.Tests;

/// <summary>ats256 以上は BveEX の状態量（BVE 本体の ats は 0〜255）。</summary>
public sealed class BveExSubjectTests
{
    [Theory]
    [InlineData("ats256", 256)]
    [InlineData("Ats308", 308)]
    [InlineData(" ATS1000 ", 1000)]
    public void Ats256AndAboveAreBveEx(string subject, int n)
    {
        Assert.True(Subjects.IsBveExAts(subject, out var i));
        Assert.Equal(n, i);
        Assert.False(Subjects.IsKnown(subject));
        Assert.Contains("BveEX", Subjects.Info(subject.Trim().ToLowerInvariant()).Label);
    }

    [Theory]
    [InlineData("ats0")]
    [InlineData("ats255")]
    [InlineData("kmph")]
    [InlineData("atsx")]
    public void OthersAreNotBveEx(string subject)
    {
        Assert.False(Subjects.IsBveExAts(subject, out _));
    }
}
