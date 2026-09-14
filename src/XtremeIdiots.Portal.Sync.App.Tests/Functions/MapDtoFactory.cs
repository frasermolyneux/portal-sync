using System.Reflection;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

/// <summary>
/// Builds <see cref="MapDto"/> instances for tests. The repository DTO exposes init-only setters,
/// so values are assigned reflectively rather than through an object initialiser.
/// </summary>
internal static class MapDtoFactory
{
    public static MapDto Create(Guid mapId, GameType gameType, string mapName, List<MapFileDto>? mapFiles)
    {
        var map = new MapDto();

        Set(map, nameof(MapDto.MapId), mapId);
        Set(map, nameof(MapDto.GameType), gameType);
        Set(map, nameof(MapDto.MapName), mapName);
        Set(map, nameof(MapDto.MapFiles), mapFiles!);

        return map;
    }

    private static void Set(MapDto map, string propertyName, object value)
    {
        var property = typeof(MapDto).GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Property {propertyName} not found on {nameof(MapDto)}");

        if (property.SetMethod is null)
        {
            throw new InvalidOperationException($"Property {propertyName} on {nameof(MapDto)} has no setter");
        }

        property.SetValue(map, value, BindingFlags.NonPublic | BindingFlags.Instance, binder: null, index: null, culture: null);
    }
}
