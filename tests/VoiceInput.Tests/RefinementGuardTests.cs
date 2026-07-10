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
}
