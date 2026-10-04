using System.Net;
using System.Text;
using System.Text.Json;
using ExtractionService;
using Microsoft.Extensions.Options;

namespace DocuManagementApp.Tests;

public sealed class FoundryDocumentAgentTests
{
    [Fact]
    public async Task FoundryLocalUsesV1EndpointAndOptionalBearerKey()
    {
        HttpRequestMessage? capturedRequest = null;
        JsonDocument? capturedBody = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            capturedBody = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Task.FromResult(ChatResponse("{\"vendorName\":\"Example\"}"));
        });
        using var httpClient = new HttpClient(handler);
        var agent = CreateAgent(httpClient, new FoundryModelOptions
        {
            Provider = "FoundryLocal",
            FoundryLocal = new FoundryLocalModelOptions
            {
                Endpoint = "http://localhost:5273/v1/",
                ModelName = "local-invoice-model",
                ApiKey = "local-secret"
            }
        });

        var result = await agent.ExtractAsync(SupportedDocumentKinds.Invoice, "Invoice markdown", CancellationToken.None);

        Assert.Equal("{\"vendorName\":\"Example\"}", result);
        Assert.Equal("http://localhost:5273/v1/chat/completions", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("local-secret", capturedRequest?.Headers.Authorization?.Parameter);
        Assert.Equal("local-invoice-model", capturedBody?.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", capturedBody?.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task MicrosoftFoundryUsesV1EndpointAndApiKeyHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(ChatResponse("{\"vendorName\":\"Example\"}"));
        });
        using var httpClient = new HttpClient(handler);
        var agent = CreateAgent(httpClient, new FoundryModelOptions
        {
            Provider = "MicrosoftFoundry",
            MicrosoftFoundry = new MicrosoftFoundryModelOptions
            {
                Endpoint = "https://example.services.ai.azure.com/openai/v1/",
                DeploymentName = "invoice-deployment",
                ApiKey = "foundry-secret"
            }
        });

        var result = await agent.CorrectAsync(
            SupportedDocumentKinds.Invoice,
            "Invoice markdown",
            "{}",
            [new ExtractionValidationIssue("total", "Total does not match subtotal plus tax.")],
            CancellationToken.None);

        Assert.Equal("{\"vendorName\":\"Example\"}", result);
        Assert.Equal("https://example.services.ai.azure.com/openai/v1/chat/completions", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("foundry-secret", capturedRequest?.Headers.GetValues("api-key").Single());
    }

    [Fact]
    public async Task MissingSelectedModelIsReportedAsConfigurationError()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(ChatResponse("{}"))));
        var agent = CreateAgent(httpClient, new FoundryModelOptions
        {
            Provider = "FoundryLocal",
            FoundryLocal = new FoundryLocalModelOptions { ModelName = string.Empty }
        });

        await Assert.ThrowsAsync<FoundryModelConfigurationException>(
            () => agent.ExtractAsync(SupportedDocumentKinds.Invoice, "Invoice markdown", CancellationToken.None));
    }

    private static FoundryDocumentAgent CreateAgent(HttpClient httpClient, FoundryModelOptions options)
    {
        return new FoundryDocumentAgent(httpClient, Options.Create(options));
    }

    private static HttpResponseMessage ChatResponse(string content)
    {
        var payload = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { content } }
            }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
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