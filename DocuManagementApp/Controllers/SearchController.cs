using System.Text.Json;
using DocuManagementApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocuManagementApp.Controllers;

[ApiController]
[Route("api/search")]
public sealed class SearchController(
    SearchServiceProxy searchProxy,
    IOptions<SearchProxyOptions> options,
    IWebHostEnvironment environment) : ControllerBase
{
    [HttpPost("deterministic")]
    public Task<IActionResult> Deterministic(JsonElement request, CancellationToken cancellationToken)
    {
        return ForwardAsync("api/search/deterministic", request, cancellationToken);
    }

    [HttpPost("hybrid")]
    public Task<IActionResult> Hybrid(JsonElement request, CancellationToken cancellationToken)
    {
        return ForwardAsync("api/search/hybrid", request, cancellationToken);
    }

    [HttpPost("agentic")]
    public Task<IActionResult> Agentic(JsonElement request, CancellationToken cancellationToken)
    {
        return ForwardAsync("api/search/agentic", request, cancellationToken);
    }

    private async Task<IActionResult> ForwardAsync(string route, JsonElement request, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Search proxy is disabled outside Development until authenticated tenant identity is configured." });
        }

        if (!options.Value.Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Search proxy is disabled." });
        }

        try
        {
            var response = await searchProxy.PostAsync(route, request, cancellationToken);
            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                Content = response.Content,
                ContentType = response.ContentType ?? "application/json"
            };
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Search Service is unavailable." });
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = "Search Service timed out." });
        }
    }
}