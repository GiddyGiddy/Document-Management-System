using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace DocuManagementApp.Services;

public sealed class SearchProxyOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://localhost:5190";
    public string ApiKey { get; set; } = string.Empty;
}

public sealed record SearchProxyResponse(HttpStatusCode StatusCode, string Content, string? ContentType);

public sealed class SearchServiceProxy(HttpClient httpClient, IOptions<SearchProxyOptions> options)
{
    private readonly SearchProxyOptions _options = options.Value;

    public async Task<SearchProxyResponse> PostAsync<TRequest>(string route, TRequest body, CancellationToken cancellationToken)
    {
        if (route is not ("api/search/deterministic" or "api/search/hybrid" or "api/search/agentic"))
        {
            throw new ArgumentOutOfRangeException(nameof(route), "Only known Search Service API routes can be proxied.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.TryAddWithoutValidation("X-Search-Api-Key", _options.ApiKey);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return new SearchProxyResponse(response.StatusCode, content, response.Content.Headers.ContentType?.ToString());
    }
}