using MX.Api.Client;

using RestSharp;

namespace XtremeIdiots.Portal.Sync.App;

internal sealed class ForumMemberPacingRestClientService(
    IRestClientService inner,
    ForumMemberRequestPacer requestPacer,
    string forumsBaseUrl) : IRestClientService
{
    public Task<RestResponse> ExecuteAsync(string baseUrl, RestRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsMemberLookup(baseUrl, request))
        {
            return inner.ExecuteAsync(baseUrl, request, cancellationToken);
        }

        return requestPacer.ExecuteAsync(
            token => inner.ExecuteAsync(baseUrl, request, token),
            cancellationToken);
    }

    public Task<RestResponse> ExecuteWithNamedOptionsAsync(string optionsName, RestRequest request, CancellationToken cancellationToken = default) =>
        inner.ExecuteWithNamedOptionsAsync(optionsName, request, cancellationToken);

    public void Dispose() => inner.Dispose();

    private bool IsMemberLookup(string baseUrl, RestRequest request) =>
        request.Method == Method.Get
        && request.Resource.StartsWith("api/core/members/", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(baseUrl, UriKind.Absolute, out var requestBaseUri)
        && Uri.TryCreate(forumsBaseUrl, UriKind.Absolute, out var forumsBaseUri)
        && Uri.Compare(requestBaseUri, forumsBaseUri, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
}
