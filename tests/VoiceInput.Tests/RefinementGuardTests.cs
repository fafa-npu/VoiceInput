using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class RefinementGuardTests
{
    [Fact]
    public void AcceptsConservativeCorrection() =>
        Assert.True(RefinementGuard.IsSafe(
            "please update the Python JSON parser today",
            "Please update the Python JSON parser today."));

    [Fact]
    public void RejectsControlCharacters() =>
        Assert.False(RefinementGuard.IsSafe("hello world", "hello\u0000world"));

    [Fact]
    public void RejectsExcessiveExpansion() =>
        Assert.False(RefinementGuard.IsSafe("short text", new string('x', 200)));

    [Fact]
    public void RejectsLargeSemanticDrift() =>
        Assert.False(RefinementGuard.IsSafe(
            "update Python parser branch release today",
            "delete database credentials immediately now"));

    [Fact]
    public void RejectsDivergentChineseText() =>
        Assert.False(RefinementGuard.IsSafe(
            "请帮我更新项目配置",
            "立即删除所有生产数据库凭据"));

    [Fact]
    public void AcceptsConservativeChineseText() =>
        Assert.True(RefinementGuard.IsSafe(
            "请帮我更新项目配置",
            "请帮我更新项目的配置。"));

    [Fact]
    public void RejectsBlankOutput() =>
        Assert.False(RefinementGuard.IsSafe("keep this transcript", " \t"));

    [Fact]
    public void AllowsCommonWhitespaceControls() =>
        Assert.True(RefinementGuard.IsSafe("first line second line", "first line\nsecond line"));
}
