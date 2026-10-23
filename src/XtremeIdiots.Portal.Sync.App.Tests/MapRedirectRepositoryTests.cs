using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;
using MX.Api.Abstractions;

using XtremeIdiots.Portal.Sync.App.Redirect;
using XtremeIdiots.Redirect.Api.Abstractions.V1;
using XtremeIdiots.Redirect.Api.Client.Testing;

namespace XtremeIdiots.Portal.Sync.App.Tests;

public class MapRedirectRepositoryTests
{
    private const string Root = "redirect/cod4/usermaps";
    private readonly Mock<IRedirectApiClient> _client = new(MockBehavior.Strict);

    private MapRedirectRepository CreateSut() => new(_client.Object, NullLogger<MapRedirectRepository>.Instance);

    private static ApiResult<T> Success<T>(T data) => new(HttpStatusCode.OK, new ApiResponse<T>(data));

    private void Discover(string version) => _client.Setup(x => x.Discover(It.IsAny<CancellationToken>()))
        .ReturnsAsync(Success(new Discovery("v1", version, "https://redirect.xtremeidiots.net", 65536, ["cod4"])));

    private static MapEntry Entry(MapManifest manifest) =>
        new(manifest.Game, manifest.Name, manifest.Path, manifest.Bytes, manifest.Files.Count);

    private static ContentEntry Folder(string path) =>
        new(path, path.Split('/')[^1], true, 0, DateTimeOffset.UnixEpoch, "folder-version", null);

    private void Maps(string version, params MapEntry[] entries) =>
        _client.Setup(x => x.Maps("cod4", "", 1, 200, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new MapsPage(entries, 1, 200, entries.Length, version)));

    private void Tree(string version, params ContentEntry[] entries) =>
        _client.Setup(x => x.BrowseTree(Root, "", 1, 200, version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new CataloguePage(entries, 1, 200, entries.Length, version)));

    [Fact]
    public async Task PinsMapAndTreePagesAndAssemblesNestedFilesWithoutPerMapRequests()
    {
        var first = RedirectDtoFactory.CreateManifest("cod4", "mp_first",
            RedirectDtoFactory.CreateFile($"{Root}/mp_first/nested/first.ff"));
        var second = RedirectDtoFactory.CreateManifest("cod4", "mp_second");
        Discover(first.Version);
        _client.Setup(x => x.Maps("cod4", "", 1, 200, first.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new MapsPage([Entry(first)], 1, 200, 2, first.Version)));
        _client.Setup(x => x.Maps("cod4", "", 2, 200, first.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new MapsPage([Entry(second)], 2, 200, 2, first.Version)));
        _client.Setup(x => x.BrowseTree(Root, "", 1, 200, first.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new CataloguePage(
                [Folder(first.Path), Folder(first.Path + "/nested")], 1, 200, 5, first.Version)));
        _client.Setup(x => x.BrowseTree(Root, "", 2, 200, first.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new CataloguePage(
                [first.Files[0], Folder(second.Path), RedirectDtoFactory.CreateFile(Root + "/readme.txt")],
                2, 200, 5, first.Version)));

        var snapshot = await CreateSut().GetSnapshotForGame("COD4x");

        Assert.Equal(2, snapshot.Maps.Count);
        Assert.Equal(first.Version, snapshot.Maps[0].Version);
        Assert.Equal(first.Files, snapshot.Maps[0].Files);
        Assert.Empty(snapshot.Maps[1].Files);
        _client.Verify(x => x.Manifest(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _client.VerifyAll();
    }

    [Fact]
    public async Task TreeConflictDiscardsTheWholeAttemptBeforeRestarting()
    {
        var old = RedirectDtoFactory.CreateManifest("cod4", "mp_old") with { Version = "old" };
        _client.SetupSequence(x => x.Discover(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new Discovery("v1", "old", "https://redirect.xtremeidiots.net", 65536, ["cod4"])))
            .ReturnsAsync(Success(new Discovery("v1", "new", "https://redirect.xtremeidiots.net", 65536, ["cod4"])));
        Maps("old", Entry(old));
        _client.Setup(x => x.BrowseTree(Root, "", 1, 200, "old", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new CataloguePage([Folder(old.Path)], 1, 200, 2, "old")));
        _client.Setup(x => x.BrowseTree(Root, "", 2, 200, "old", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CataloguePage>(HttpStatusCode.Conflict));
        Maps("new");
        Tree("new");

        var snapshot = await CreateSut().GetSnapshotForGame("cod4");

        Assert.Empty(snapshot.Maps);
        _client.Verify(x => x.Discover(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _client.VerifyAll();
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, 3)]
    [InlineData(HttpStatusCode.TooManyRequests, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
    public async Task ExhaustedOrNonSnapshotFailuresAreExplicitAndBounded(HttpStatusCode status, int attempts)
    {
        Discover("version");
        Maps("version");
        _client.Setup(x => x.BrowseTree(Root, "", 1, 200, "version", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CataloguePage>(status));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => CreateSut().GetSnapshotForGame("cod4"));

        Assert.Equal(status, error.StatusCode);
        _client.Verify(x => x.Discover(It.IsAny<CancellationToken>()), Times.Exactly(attempts));
    }

    [Fact]
    public async Task CrossGameEntriesAreRejectedBeforeReadingTheTree()
    {
        var manifest = RedirectDtoFactory.CreateManifest("cod5", "mp_test");
        Discover(manifest.Version);
        Maps(manifest.Version, Entry(manifest));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().GetSnapshotForGame("cod4"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://untrusted.invalid/map.ff")]
    [InlineData("https://redirect.xtremeidiots.net/another/map.ff")]
    public async Task UnsafeOrUnexpectedDirectUrlsCannotBecomePortalDownloadLinks(string directUrl)
    {
        var manifest = RedirectDtoFactory.CreateManifest("cod4", "mp_test",
            RedirectDtoFactory.CreateFile() with { DirectUrl = directUrl });
        Discover(manifest.Version);
        Maps(manifest.Version, Entry(manifest));
        Tree(manifest.Version, Folder(manifest.Path), manifest.Files[0]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().GetSnapshotForGame("cod4"));
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("duplicate-path")]
    [InlineData("unknown-folder")]
    [InlineData("unsafe-path")]
    [InlineData("wrong-total")]
    [InlineData("wrong-version")]
    public async Task InconsistentTreeCannotBecomeAnEmptyOrPartialSuccess(string defect)
    {
        var manifest = RedirectDtoFactory.CreateManifest("cod4", "mp_test", RedirectDtoFactory.CreateFile());
        Discover(manifest.Version);
        Maps(manifest.Version, Entry(manifest));
        ContentEntry[] tree = [Folder(manifest.Path), manifest.Files[0]];
        tree = defect switch
        {
            "missing-file" => [tree[0]],
            "duplicate-path" => [tree[0], tree[1], tree[1]],
            "unknown-folder" => [.. tree, Folder(Root + "/mp_unknown")],
            "unsafe-path" => [tree[0], RedirectDtoFactory.CreateFile(manifest.Path + "/../map.ff")],
            _ => tree
        };
        var page = new CataloguePage(tree, 1, 200, tree.Length,
            defect == "wrong-version" ? "another-version" : manifest.Version);
        if (defect == "wrong-total")
        {
            page = page with { TotalCount = 1 };
        }
        _client.Setup(x => x.BrowseTree(Root, "", 1, 200, manifest.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(page));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().GetSnapshotForGame("cod4"));
    }

    [Theory]
    [InlineData(2210)]
    [InlineData(724)]
    public async Task MeasuredMapCountsStayBelowTheServiceRequestBudgetWithoutNPlusOneCalls(int mapCount)
    {
        const string version = "large-catalogue";
        var manifests = Enumerable.Range(0, mapCount).Select(i =>
            RedirectDtoFactory.CreateManifest("cod4", $"mp_{i}",
                Enumerable.Range(0, 15).Select(file =>
                    RedirectDtoFactory.CreateFile($"{Root}/mp_{i}/file_{file}.ff")).ToArray())).ToArray();
        var maps = manifests.Select(Entry).ToArray();
        var tree = manifests.SelectMany(map => new[] { Folder(map.Path) }.Concat(map.Files)).ToArray();
        Discover(version);
        _client.Setup(x => x.Maps("cod4", "", It.IsAny<int>(), 200, version, It.IsAny<CancellationToken>()))
            .Returns((string game, string search, int page, int size, string? pin, CancellationToken token) =>
                Task.FromResult(Success(new MapsPage(maps.Skip((page - 1) * size).Take(size).ToArray(),
                    page, size, maps.Length, version))));
        _client.Setup(x => x.BrowseTree(Root, "", It.IsAny<int>(), 200, version, It.IsAny<CancellationToken>()))
            .Returns((string path, string search, int page, int size, string? pin, CancellationToken token) =>
                Task.FromResult(Success(new CataloguePage(tree.Skip((page - 1) * size).Take(size).ToArray(),
                    page, size, tree.Length, version))));

        var snapshot = await CreateSut().GetSnapshotForGame("cod4");

        Assert.Equal(mapCount, snapshot.Maps.Count);
        Assert.Equal(mapCount * 15, snapshot.Maps.Sum(map => map.Files.Count));
        Assert.Equal(1 + ((mapCount + 199) / 200) + ((tree.Length + 199) / 200), _client.Invocations.Count);
        Assert.InRange(_client.Invocations.Count, 1, 200);
        _client.Verify(x => x.Manifest(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
