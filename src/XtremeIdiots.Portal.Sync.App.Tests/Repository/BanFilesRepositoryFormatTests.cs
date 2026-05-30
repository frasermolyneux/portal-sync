using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Sync.App.Repository;

namespace XtremeIdiots.Portal.Sync.App.Tests.Repository;

/// <summary>
/// Pure-function tests for the format helpers on <see cref="BanFilesRepository"/>.
/// The helpers are deliberately static + parameter-free so they don't need any blob
/// client mocking — they're the single source of truth for per-game-type wire format.
/// </summary>
[Trait("Category", "Unit")]
public class BanFilesRepositoryFormatTests
{
    [Theory]
    [InlineData(GameType.CallOfDuty2, false)]
    [InlineData(GameType.CallOfDuty4, false)]
    [InlineData(GameType.CallOfDuty5, false)]
    [InlineData(GameType.CallOfDuty4x, true)]
    [InlineData(GameType.Unknown, false)]
    public void UsesSimplebanlistV2_OnlyTrueForCod4x(GameType gameType, bool expected)
    {
        Assert.Equal(expected, BanFilesRepository.UsesSimplebanlistV2(gameType));
    }

    [Theory]
    [InlineData(GameType.CallOfDuty2)]
    [InlineData(GameType.CallOfDuty4)]
    [InlineData(GameType.CallOfDuty5)]
    public void FormatBanLine_LegacyGames_EmitsGuidSpaceBansyncDashName(GameType gameType)
    {
        var line = BanFilesRepository.FormatBanLine(gameType, "1234567890abcdef", "PlayerOne");

        Assert.Equal("1234567890abcdef [BANSYNC]-PlayerOne", line);
    }

    [Fact]
    public void FormatBanLine_Cod4x_EmitsSimplebanlistV2WithBansyncReason()
    {
        // 19-digit cod4x playerid (Steam64-shape) — what the parser stores in Player.Guid.
        var line = BanFilesRepository.FormatBanLine(GameType.CallOfDuty4x, "1234567890123456789", "PlayerOne");

        Assert.Equal(@"\playerid\1234567890123456789\asteamid\0\rsn\[BANSYNC] PlayerOne", line);
    }

    [Fact]
    public void FormatBanLine_Cod4x_AlwaysSetsAsteamidToZeroSentinel()
    {
        // Sanity: the format always emits `asteamid\0` because the portal stores the
        // playerid in Player.Guid and does not track a separate Steam64 today.
        var line = BanFilesRepository.FormatBanLine(GameType.CallOfDuty4x, "76561197960287930", "AnotherPlayer");

        Assert.Contains(@"\asteamid\0\", line, StringComparison.Ordinal);
        Assert.StartsWith(@"\playerid\76561197960287930\", line, StringComparison.Ordinal);
        Assert.EndsWith(@"\rsn\[BANSYNC] AnotherPlayer", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatBanLine_Cod4x_EmbedsBansyncTagInsideReasonSoCountTagsStillWorks()
    {
        // The agent's CountTags() classifies a line as sync-pushed when it Contains
        // "[BANSYNC]". Putting the tag inside the reason field keeps that working
        // without any agent-side changes.
        var line = BanFilesRepository.FormatBanLine(GameType.CallOfDuty4x, "1234567890123456789", "x");

        Assert.Contains("[BANSYNC]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatBanLine_Cod4x_BackslashInUsername_IsNeutralisedToForwardSlash()
    {
        // \ is the simplebanlist v2 field separator. A raw backslash in the username
        // could otherwise inject a forged field (e.g. \admin\true). Neutralise to /.
        var line = BanFilesRepository.FormatBanLine(
            GameType.CallOfDuty4x,
            "1234567890123456789",
            @"attacker\rsn\Innocent");

        Assert.Equal(
            @"\playerid\1234567890123456789\asteamid\0\rsn\[BANSYNC] attacker/rsn/Innocent",
            line);
        Assert.DoesNotContain(@"attacker\rsn", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GameType.CallOfDuty4)]
    [InlineData(GameType.CallOfDuty4x)]
    public void FormatBanLine_NewlineOrCarriageReturnInUsername_IsNeutralisedToSpace(GameType gameType)
    {
        // WriteLineAsync appends \n per line, so a newline in the username would split
        // the entry into two lines and corrupt the file for either format.
        var line = BanFilesRepository.FormatBanLine(
            gameType,
            "1234567890abcdef",
            "Player\r\nForged");

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("Player  Forged", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsSentinelBan_NullOrBlankGuid_ReturnsTrue(string? guid)
    {
        Assert.True(BanFilesRepository.IsSentinelBan(guid, "RealPlayer"));
    }

    [Fact]
    public void IsSentinelBan_GuidIsZeroSentinel_ReturnsTrue()
    {
        // cod4x parser emits playerid "0" for unauthenticated / not-yet-resolved
        // players. Banning that record would persist a global "ban anyone with no id".
        Assert.True(BanFilesRepository.IsSentinelBan("0", "RealPlayer"));
    }

    [Fact]
    public void IsSentinelBan_UsernameIsBotClient_ReturnsTrue()
    {
        // PunkBuster / plugin layer surfaces "BOT-Client" as the username for bots.
        // We never want a bot in the ban file.
        Assert.True(BanFilesRepository.IsSentinelBan("1234567890123456789", "BOT-Client"));
    }

    [Theory]
    [InlineData("1234567890abcdef", "RealPlayer")]
    [InlineData("1234567890123456789", "AnotherPlayer")]
    [InlineData("00abc", "Player")] // only the literal "0" is the sentinel — not strings containing "0"
    [InlineData("0", "")] // username does not rescue a sentinel guid
    public void IsSentinelBan_HappyPathAndEdgeCases(string guid, string username)
    {
        var isSentinel = BanFilesRepository.IsSentinelBan(guid, username);

        if (guid == "0")
            Assert.True(isSentinel);
        else
            Assert.False(isSentinel);
    }

    [Fact]
    public void IsSentinelBan_UsernameComparisonIsCaseSensitive()
    {
        // StringComparison.Ordinal: only the literal "BOT-Client" is the sentinel —
        // a real player called "bot-client" should still be banned.
        Assert.False(BanFilesRepository.IsSentinelBan("1234567890abcdef", "bot-client"));
        Assert.False(BanFilesRepository.IsSentinelBan("1234567890abcdef", "BOT-CLIENT"));
    }
}
