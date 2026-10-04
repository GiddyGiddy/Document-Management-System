using System.Net;
using System.Text;
using System.Text.Json;
using DocuManagementApp.Services;
using Microsoft.Extensions.Options;

namespace DocuManagementApp.Tests;

public sealed class SearchServiceProxyTests
{
    [Fact]
    public async Task ForwardsFixedSearchRouteAndKeepsApiKeyServerSide()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://search.local/") };
        var proxy = new SearchServiceProxy(httpClient, Options.Create(new SearchProxyOptions { ApiKey = "server-only-key" }));

        var response = await proxy.PostAsync("api/search/deterministic", new { filter = new { documentKind = "Contract" } }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http://search.local/api/search/deterministic", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("server-only-key", capturedRequest?.Headers.GetValues("X-Search-Api-Key").Single());
        Assert.Equal("[]", response.Content);
    }

    [Fact]
    public async Task RejectsRoutesOutsideSearchAllowList()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("Unexpected HTTP call.")));
        var proxy = new SearchServiceProxy(httpClient, Options.Create(new SearchProxyOptions { ApiKey = "server-only-key" }));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => proxy.PostAsync("api/admin/delete", new { }, CancellationToken.None));
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return sendAsync(request, cancellationToken);
        }
    }
}
