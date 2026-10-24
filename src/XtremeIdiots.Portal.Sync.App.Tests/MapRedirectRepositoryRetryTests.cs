using System.Net;

using Microsoft.Extensions.Logging;

using Moq;
using MX.Api.Abstractions;

using XtremeIdiots.Portal.Sync.App.Redirect;
using XtremeIdiots.Redirect.Api.Abstractions.V1;
using XtremeIdiots.Redirect.Api.Client.Testing;

namespace XtremeIdiots.Portal.Sync.App.Tests;

public class MapRedirectRepositoryRetryTests
{
    private const string Root = "redirect/cod4/usermaps";
    private readonly Mock<IRedirectApiClient> _client = new(MockBehavior.Strict);
    private readonly Mock<ILogger<MapRedirectRepository>> _logger = new();

    private MapRedirectRepository CreateSut() => new(_client.Object, _logger.Object);

    private static ApiResult<T> Success<T>(T data) => new(HttpStatusCode.OK, new ApiResponse<T>(data));

    private static Discovery Discovery(string version) =>
        new("v1", version, "https://redirect.xtremeidiots.net", 65536, ["cod4"]);

    private void SetupEmptySnapshotPages(string version, CancellationToken token)
    {
        _client.Setup(x => x.Maps("cod4", "", 1, 200, version, token))
            .ReturnsAsync(Success(new MapsPage([], 1, 200, 0, version)));
        _client.Setup(x => x.BrowseTree(Root, "", 1, 200, version, token))
            .ReturnsAsync(Success(new CataloguePage([], 1, 200, 0, version)));
    }

    private static MapEntry Entry(MapManifest manifest) =>
        new(manifest.Game, manifest.Name, manifest.Path, manifest.Bytes, manifest.Files.Count);

    private static ContentEntry Folder(string path) =>
        new(path, path.Split('/')[^1], true, 0, DateTimeOffset.UnixEpoch, "folder-version", null);

    [Fact]
    public async Task ImmediateSuccessUsesCanonicalGameAndForwardsCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _client.Setup(x => x.Discover(token)).ReturnsAsync(Success(Discovery("version")));
        SetupEmptySnapshotPages("version", token);

        var snapshot = await CreateSut().GetSnapshotForGame("COD4X", token);

        Assert.Empty(snapshot.Maps);
        AssertRequests(discoveries: 1, maps: 1, trees: 1, token);
        Assert.Empty(WarningAttempts());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ConflictRetriesAdvanceAttemptAndThenSucceed(int conflicts)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var discoveryCalls = 0;
        _client.Setup(x => x.Discover(token)).Returns(() =>
        {
            if (discoveryCalls++ < conflicts)
            {
                return Task.FromException<ApiResult<Discovery>>(
                    new HttpRequestException("catalogue changed", null, HttpStatusCode.Conflict));
            }

            return Task.FromResult(Success(Discovery("version")));
        });
        SetupEmptySnapshotPages("version", token);

        var snapshot = await CreateSut().GetSnapshotForGame("COD4x", token);

        Assert.Empty(snapshot.Maps);
        AssertRequests(discoveries: conflicts + 1, maps: 1, trees: 1, token);
        Assert.Equal(Enumerable.Range(1, conflicts), WarningAttempts());
    }

    [Fact]
    public async Task ThirdConflictPropagatesTheSameExceptionAfterThreeAttempts()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var conflicts = Enumerable.Range(1, 3)
            .Select(attempt => new HttpRequestException($"conflict {attempt}", null, HttpStatusCode.Conflict))
            .ToArray();
        var discoveryCalls = 0;
        _client.Setup(x => x.Discover(token)).Returns(() =>
            Task.FromException<ApiResult<Discovery>>(conflicts[discoveryCalls++]));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateSut().GetSnapshotForGame("cod4", token));

        Assert.Same(conflicts[2], error);
        AssertRequests(discoveries: 3, maps: 0, trees: 0, token);
        Assert.Equal([1, 2], WarningAttempts());
    }

    [Fact]
    public async Task NonConflictFailurePropagatesWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var failure = new HttpRequestException("service unavailable", null, HttpStatusCode.ServiceUnavailable);
        _client.Setup(x => x.Discover(token))
            .Returns(() => Task.FromException<ApiResult<Discovery>>(failure));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateSut().GetSnapshotForGame("cod4", token));

        Assert.Same(failure, error);
        AssertRequests(discoveries: 1, maps: 0, trees: 0, token);
        Assert.Empty(WarningAttempts());
    }

    [Fact]
    public async Task CancellationBeforeWorkPreventsAnyRequests()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateSut().GetSnapshotForGame("cod4", cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(_client.Invocations);
        Assert.Empty(WarningAttempts());
    }

    [Fact]
    public async Task CancellationAfterConflictPreventsTheNextAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var conflict = new HttpRequestException("catalogue changed", null, HttpStatusCode.Conflict);
        _client.Setup(x => x.Discover(token)).Returns(() =>
        {
            cancellation.Cancel();
            return Task.FromException<ApiResult<Discovery>>(conflict);
        });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateSut().GetSnapshotForGame("cod4", token));

        Assert.Equal(token, error.CancellationToken);
        AssertRequests(discoveries: 1, maps: 0, trees: 0, token);
        Assert.Equal([1], WarningAttempts());
    }

    [Fact]
    public async Task ConflictAfterPartialPaginationRediscoversAndDiscardsPartialResults()
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var oldManifest = RedirectDtoFactory.CreateManifest("cod4", "mp_old") with { Version = "old" };
        var newManifest = RedirectDtoFactory.CreateManifest("cod4", "mp_new") with { Version = "new" };
        var discoveryCalls = 0;
        _client.Setup(x => x.Discover(token)).Returns(() =>
            Task.FromResult(Success(Discovery(discoveryCalls++ == 0 ? "old" : "new"))));
        _client.Setup(x => x.Maps("cod4", "", 1, 200, "old", token))
            .ReturnsAsync(Success(new MapsPage([Entry(oldManifest)], 1, 200, 1, "old")));
        _client.Setup(x => x.BrowseTree(Root, "", 1, 200, "old", token))
            .ReturnsAsync(Success(new CataloguePage([Folder(oldManifest.Path)], 1, 200, 2, "old")));
        _client.Setup(x => x.BrowseTree(Root, "", 2, 200, "old", token))
            .ReturnsAsync(new ApiResult<CataloguePage>(HttpStatusCode.Conflict));
        _client.Setup(x => x.Maps("cod4", "", 1, 200, "new", token))
            .ReturnsAsync(Success(new MapsPage([Entry(newManifest)], 1, 200, 1, "new")));
        _client.Setup(x => x.BrowseTree(Root, "", 1, 200, "new", token))
            .ReturnsAsync(Success(new CataloguePage([Folder(newManifest.Path)], 1, 200, 1, "new")));

        var snapshot = await CreateSut().GetSnapshotForGame("COD4X", token);

        Assert.Equal(["mp_new"], snapshot.Maps.Select(map => map.Name));
        Assert.All(snapshot.Maps, map => Assert.Equal("new", map.Version));
        AssertRequests(discoveries: 2, maps: 2, trees: 3, token);
        Assert.Equal([1], WarningAttempts());
        _client.VerifyAll();
    }

    private void AssertRequests(int discoveries, int maps, int trees, CancellationToken token)
    {
        Assert.Equal(discoveries, _client.Invocations.Count(invocation => invocation.Method.Name == nameof(IRedirectApiClient.Discover)));
        Assert.Equal(maps, _client.Invocations.Count(invocation => invocation.Method.Name == nameof(IRedirectApiClient.Maps)));
        Assert.Equal(trees, _client.Invocations.Count(invocation => invocation.Method.Name == nameof(IRedirectApiClient.BrowseTree)));
        Assert.All(_client.Invocations, invocation => Assert.Equal(token, invocation.Arguments[^1]));
        _client.VerifyAll();
    }

    private int[] WarningAttempts() =>
        _logger.Invocations
            .Where(invocation => invocation.Method.Name == nameof(ILogger.Log) &&
                invocation.Arguments[0] is LogLevel.Warning)
            .Select(invocation =>
            {
                var state = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(invocation.Arguments[2]);
                return (int)Assert.Single(state, item => item.Key == "Attempt").Value!;
            })
            .ToArray();
}
