using XtremeIdiots.Portal.Sync.App.Validators;

namespace XtremeIdiots.Portal.Sync.App.Tests.Helpers;

public class GuidValidatorTests
{
    private readonly GuidValidator _sut = new();

    [Theory]
    [InlineData("CallOfDuty2", "abcd", true)]
    [InlineData("CallOfDuty2", "abcd1234", true)]
    [InlineData("CallOfDuty2", "abcdef1234567890abcdef1234567890", true)]
    [InlineData("CallOfDuty2", "abc", false)] // too short (min 4)
    [InlineData("CallOfDuty2", "", false)]
    [InlineData("CallOfDuty2", "ABCD", false)] // uppercase not allowed
    [InlineData("CallOfDuty2", "abcd!@#$", false)] // special chars
    [InlineData("CallOfDuty2", "abcdef1234567890abcdef1234567890a", false)] // 33 chars, too long
    public void IsValid_CallOfDuty2_ReturnsExpected(string gameType, string guid, bool expected)
    {
        var result = _sut.IsValid(gameType, guid);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("CallOfDuty4", "abcdef1234567890abcdef1234567890", true)] // exactly 32 hex chars
    [InlineData("CallOfDuty4", "00000000000000000000000000000000", true)]
    [InlineData("CallOfDuty4", "abcd", false)] // too short
    [InlineData("CallOfDuty4", "abcdef1234567890abcdef123456789", false)] // 31 chars
    [InlineData("CallOfDuty4", "abcdef1234567890abcdef1234567890a", false)] // 33 chars
    [InlineData("CallOfDuty4", "", false)]
    [InlineData("CallOfDuty4", "ABCDEF1234567890ABCDEF1234567890", false)] // uppercase
    public void IsValid_CallOfDuty4_ReturnsExpected(string gameType, string guid, bool expected)
    {
        var result = _sut.IsValid(gameType, guid);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("CallOfDuty5", "abcd", true)]
    [InlineData("CallOfDuty5", "abcd1234", true)]
    [InlineData("CallOfDuty5", "abcdef1234567890abcdef1234567890", true)]
    [InlineData("CallOfDuty5", "abc", false)] // too short
    [InlineData("CallOfDuty5", "", false)]
    [InlineData("CallOfDuty5", "abcdef1234567890abcdef1234567890a", false)] // 33 chars
    public void IsValid_CallOfDuty5_ReturnsExpected(string gameType, string guid, bool expected)
    {
        var result = _sut.IsValid(gameType, guid);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsValid_UnsupportedGameType_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _sut.IsValid("UnsupportedGame", "abcd1234"));
    }
}
