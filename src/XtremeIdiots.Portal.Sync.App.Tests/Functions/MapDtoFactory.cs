using System.Reflection;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

/// <summary>
/// Builds <see cref="MapDto"/> instances for tests. The repository DTO exposes non-public setters,
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
        var property = typeof(MapDto).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Property {propertyName} not found on {nameof(MapDto)}");

        var setter = property.SetMethod
            ?? throw new InvalidOperationException($"Property {propertyName} on {nameof(MapDto)} has no setter");

        setter.Invoke(map, [value]);
    }
}
