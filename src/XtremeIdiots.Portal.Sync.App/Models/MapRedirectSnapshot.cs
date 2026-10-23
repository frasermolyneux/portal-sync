using XtremeIdiots.Redirect.Api.Abstractions.V1;

namespace XtremeIdiots.Portal.Sync.App.Models;

public record MapRedirectSnapshot(string DirectBaseUrl, IReadOnlyList<MapManifest> Maps);
