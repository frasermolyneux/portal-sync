using Microsoft.Extensions.Logging;

using Moq;

using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests;

public class MapRotationActivitiesFormatTests
{
    private readonly MapRotationActivities _activities;

    public MapRotationActivitiesFormatTests()
    {
        _activities = new MapRotationActivities(
            Mock.Of<ILogger<MapRotationActivities>>(),
            Mock.Of<IRepositoryApiClient>(),
            Mock.Of<IServersApiClient>());
    }

    [Fact]
    public async Task FormatRotationString_StandardFormat_ProducesCorrectOutput()
    {
        // Arrange
        var input = new FormatRotationInput(
            ["mp_backlot", "mp_crash", "mp_crossfire"],
            "ftag",
            "sv_maprotation");

        // Act
        var result = await _activities.FormatRotationString(input);

        // Assert
        Assert.Single(result.Parts);
        Assert.Equal("sv_maprotation", result.Parts[0].VariableName);
        Assert.Equal("gametype ftag map mp_backlot map mp_crash map mp_crossfire", result.Parts[0].Value);
    }

    [Fact]
    public async Task FormatRotationString_AacpFormat_ProducesSemicolonSeparated()
    {
        // Arrange
        var input = new FormatRotationInput(
            ["mp_backlot", "mp_crash", "mp_crossfire"],
            "ftag",
            "scr_aacp_maps_1");

        // Act
        var result = await _activities.FormatRotationString(input);

        // Assert
        Assert.Single(result.Parts);
        Assert.Equal("scr_aacp_maps_1", result.Parts[0].VariableName);
        Assert.Equal("mp_backlot;mp_crash;mp_crossfire", result.Parts[0].Value);
    }

    [Fact]
    public async Task FormatRotationString_EmptyMaps_StandardFormat_ReturnsGametypeOnly()
    {
        // Arrange
        var input = new FormatRotationInput([], "ftag", "sv_maprotation");

        // Act
        var result = await _activities.FormatRotationString(input);

        // Assert
        Assert.Single(result.Parts);
        Assert.Equal("gametype ftag", result.Parts[0].Value);
    }

    [Fact]
    public async Task FormatRotationString_EmptyMaps_AacpFormat_ReturnsEmptyString()
    {
        // Arrange
        var input = new FormatRotationInput([], "ftag", "scr_aacp_maps_1");

        // Act
        var result = await _activities.FormatRotationString(input);

        // Assert
        Assert.Single(result.Parts);
        Assert.Equal("", result.Parts[0].Value);
    }

    [Fact]
    public async Task FormatRotationString_ExceedsLimit_SplitsIntoMultipleParts()
    {
        // Arrange — generate enough maps to exceed 1024 chars
        var maps = Enumerable.Range(1, 80)
            .Select(i => $"mp_testmap_{i:D3}")
            .ToList();

        var input = new FormatRotationInput(maps, "ftag", "sv_maprotation");

        // Act
        var result = await _activities.FormatRotationString(input);

        // Assert
        Assert.True(result.Parts.Count > 1, $"Expected multiple parts but got {result.Parts.Count}");
        Assert.Equal("sv_maprotation", result.Parts[0].VariableName);
        Assert.Equal("sv_maprotation_1", result.Parts[1].VariableName);

        foreach (var part in result.Parts)
        {
            Assert.True(part.Value.Length <= 1024 || result.Parts.IndexOf(part) == 0,
                $"Part {part.VariableName} has {part.Value.Length} chars, exceeding 1024 limit");
        }

        var allMapEntries = result.Parts.SelectMany(p => p.Value.Split(' ')
            .Where(t => t.StartsWith("mp_")))
            .ToList();
        Assert.Equal(80, allMapEntries.Count);
    }

    [Fact]
    public async Task FormatRotationString_AacpExceedsLimit_SplitsWithCorrectNaming()
    {
        // Arrange
        var maps = Enumerable.Range(1, 100)
            .Select(i => $"mp_testmap_{i:D3}")
            .ToList();

        var input = new FormatRotationInput(maps, "ftag", "scr_aacp_maps_1");

        // Act
        var result = await _activities.FormatRotationString(input);

        // Assert
        Assert.True(result.Parts.Count > 1);
        Assert.Equal("scr_aacp_maps_1", result.Parts[0].VariableName);
        Assert.Equal("scr_aacp_maps_2", result.Parts[1].VariableName);

        var allMaps = result.Parts.SelectMany(p => p.Value.Split(';')).ToList();
        Assert.Equal(100, allMaps.Count);
    }

    [Fact]
    public async Task FormatRotationString_AacpStartingAtHigherIndex_ContinuesCorrectly()
    {
        // Arrange
        var maps = Enumerable.Range(1, 100)
            .Select(i => $"mp_testmap_{i:D3}")
            .ToList();

        var input = new FormatRotationInput(maps, "ftag", "scr_aacp_maps_3");

        // Act
        var result = await _activities.FormatRotationString(input);

        // Assert
        Assert.True(result.Parts.Count > 1);
        Assert.Equal("scr_aacp_maps_3", result.Parts[0].VariableName);
        Assert.Equal("scr_aacp_maps_4", result.Parts[1].VariableName);
    }

    [Fact]
    public async Task FormatRotationString_ExactlyAtLimit_NoSplit()
    {
        // Arrange — build a string that fits within 1024 chars
        var prefix = "gametype ftag ";
        var maps = new List<string>();
        var currentLength = prefix.Length;

        while (true)
        {
            var mapName = $"mp_map{maps.Count + 1:D3}";
            var entry = $"map {mapName} ";
            if (currentLength + entry.Length > 1024 + 1) break;
            maps.Add(mapName);
            currentLength += entry.Length;
        }

        var input = new FormatRotationInput(maps, "ftag", "sv_maprotation");

        // Act
        var result = await _activities.FormatRotationString(input);

        // Assert
        Assert.Single(result.Parts);
        Assert.True(result.Parts[0].Value.Length <= 1024);
    }

    [Fact]
    public async Task FormatRotationString_SingleOversizedEntry_DoesNotInfiniteLoop()
    {
        // Arrange — a single map with an absurdly long name
        var longMapName = "mp_" + new string('x', 1100);
        var input = new FormatRotationInput([longMapName], "ftag", "sv_maprotation");

        // Act — should complete without hanging
        var result = await _activities.FormatRotationString(input);

        // Assert
        Assert.Single(result.Parts);
        Assert.Contains(longMapName, result.Parts[0].Value);
    }

    [Fact]
    public async Task FormatRotationString_SplitPreservesMapBoundaries()
    {
        // Arrange
        var maps = Enumerable.Range(1, 80)
            .Select(i => $"mp_testmap_{i:D3}")
            .ToList();

        var input = new FormatRotationInput(maps, "ftag", "sv_maprotation");

        // Act
        var result = await _activities.FormatRotationString(input);

        // Assert — no part should end with a dangling 'map' keyword
        foreach (var part in result.Parts)
        {
            var tokens = part.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] == "map")
                {
                    Assert.True(i + 1 < tokens.Length,
                        $"Part '{part.VariableName}' ends with dangling 'map' keyword");
                    Assert.StartsWith("mp_", tokens[i + 1]);
                }
            }
        }
    }
}

public class RotationVariableNamingTests
{
    [Theory]
    [InlineData("sv_maprotation", "sv_maprotation_", 1)]
    [InlineData("scr_aacp_maps_1", "scr_aacp_maps_", 1)]
    [InlineData("scr_aacp_maps_3", "scr_aacp_maps_", 3)]
    [InlineData("sv_maprotation_10", "sv_maprotation_", 10)]
    [InlineData("scr_small_rotation", "scr_small_rotation_", 1)]
    public void ParseVariableNameBase_CorrectRootAndStartIndex(string input, string expectedRoot, int expectedStart)
    {
        // Act
        var (root, startIndex) = RotationVariableNaming.ParseVariableNameBase(input);

        // Assert
        Assert.Equal(expectedRoot, root);
        Assert.Equal(expectedStart, startIndex);
    }

    [Fact]
    public void GetOverflowVariableNames_ExcludesUsedNames()
    {
        // Arrange
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sv_maprotation_1", "sv_maprotation_2" };

        // Act
        var overflowNames = RotationVariableNaming.GetOverflowVariableNames("sv_maprotation", usedNames).ToList();

        // Assert
        Assert.DoesNotContain("sv_maprotation_1", overflowNames);
        Assert.DoesNotContain("sv_maprotation_2", overflowNames);
        Assert.Contains("sv_maprotation_3", overflowNames);
    }

    [Fact]
    public void GetOverflowVariableNames_AacpNaming_UsesCorrectSeries()
    {
        // Arrange
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scr_aacp_maps_1" };

        // Act
        var overflowNames = RotationVariableNaming.GetOverflowVariableNames("scr_aacp_maps_1", usedNames).ToList();

        // Assert
        Assert.DoesNotContain("scr_aacp_maps_1", overflowNames);
        Assert.Contains("scr_aacp_maps_2", overflowNames);
        Assert.Contains("scr_aacp_maps_3", overflowNames);
    }

    [Fact]
    public void GetOverflowVariableNames_HighStartIndex_ContinuesFromThere()
    {
        // Arrange
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scr_aacp_maps_5" };

        // Act
        var overflowNames = RotationVariableNaming.GetOverflowVariableNames("scr_aacp_maps_5", usedNames).ToList();

        // Assert
        Assert.DoesNotContain("scr_aacp_maps_5", overflowNames);
        Assert.Contains("scr_aacp_maps_6", overflowNames);
        Assert.Contains("scr_aacp_maps_7", overflowNames);
    }

    [Fact]
    public void GetOverflowVariableNames_EmptyUsedSet_ReturnsAllOverflow()
    {
        // Arrange
        var emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Act
        var overflowNames = RotationVariableNaming.GetOverflowVariableNames("sv_maprotation", emptySet).ToList();

        // Assert
        Assert.Equal(10, overflowNames.Count); // startIndex (1) through startIndex + 9
        Assert.Contains("sv_maprotation_1", overflowNames);
        Assert.Contains("sv_maprotation_10", overflowNames);
    }
}
