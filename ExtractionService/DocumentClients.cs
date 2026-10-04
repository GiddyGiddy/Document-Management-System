using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ExtractionService;

public sealed class DocumentContentApiOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5176/";
}

public sealed class DocumentIntelligenceOptions
{
    public string Endpoint { get; set; } = "http://localhost:5000";
    public string ApiKey { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "2024-11-30";
    public int PollIntervalSeconds { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 180;
}

public sealed record DocumentContent(byte[] Bytes, string ContentType);

public sealed record DocumentLayoutResult(string Markdown, string RawJson);

public interface IDocumentContentClient
{
    Task<DocumentContent> GetAsync(Guid documentId, CancellationToken cancellationToken);
}

public sealed class DocumentContentApiClient(HttpClient httpClient) : IDocumentContentClient
{
    public async Task<DocumentContent> GetAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var relativeUri = $"api/fileupload/download/{documentId:D}";
        using var response = await httpClient.GetAsync(relativeUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Document '{documentId}' was not found by the content API.");
        }

        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/pdf";
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
        {
            throw new InvalidDataException($"Document '{documentId}' has empty content.");
        }

        return new DocumentContent(bytes, contentType);
    }
}

public interface IDocumentLayoutClient
{
    Task<DocumentLayoutResult> AnalyzeAsync(DocumentContent document, CancellationToken cancellationToken);
}

public sealed class DocumentIntelligenceLayoutClient(
    HttpClient httpClient,
    IOptions<DocumentIntelligenceOptions> options) : IDocumentLayoutClient
{
    public async Task<DocumentLayoutResult> AnalyzeAsync(DocumentContent document, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("DocumentIntelligence:ApiKey must be configured through user secrets or environment variables.");
        }

        var endpoint = new Uri(settings.Endpoint.EndsWith('/') ? settings.Endpoint : settings.Endpoint + "/");
        var analyzeUri = new Uri(endpoint,
            $"formrecognizer/documentModels/prebuilt-layout:analyze?api-version={Uri.EscapeDataString(settings.ApiVersion)}&outputContentFormat=markdown");
        using var content = new ByteArrayContent(document.Bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(document.ContentType);
        using var request = new HttpRequestMessage(HttpMethod.Post, analyzeUri) { Content = content };
        request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", settings.ApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Accepted && !response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new DocumentLayoutException(
                $"Document Intelligence returned {(int)response.StatusCode}: {Limit(details, 1000)}");
        }

        if (response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Accepted)
        {
            return ParseResult(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        var operationLocation = response.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(operationLocation) &&
            response.Headers.TryGetValues("Operation-Location", out var values))
        {
            operationLocation = values.FirstOrDefault();
        }
        if (string.IsNullOrWhiteSpace(operationLocation) || !Uri.TryCreate(operationLocation, UriKind.Absolute, out var operationUri))
        {
            throw new DocumentLayoutException("Document Intelligence accepted the request without an operation location.");
        }

        if (!string.Equals(operationUri.GetLeftPart(UriPartial.Authority), endpoint.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            throw new DocumentLayoutException("Document Intelligence returned an operation URL outside the configured endpoint.");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.TimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), cancellationToken);
            using var pollRequest = new HttpRequestMessage(HttpMethod.Get, operationUri);
            pollRequest.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", settings.ApiKey);
            using var pollResponse = await httpClient.SendAsync(pollRequest, cancellationToken);
            var rawJson = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
            pollResponse.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(rawJson);
            var status = json.RootElement.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;
            if (string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return ParseResult(rawJson);
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var detail = json.RootElement.TryGetProperty("error", out var errorElement)
                    ? errorElement.ToString()
                    : "No error detail was returned.";
                throw new DocumentLayoutException($"Document Intelligence analysis failed: {Limit(detail, 1000)}");
            }
        }

        throw new TimeoutException($"Document Intelligence analysis did not complete within {settings.TimeoutSeconds} seconds.");
    }

    private static DocumentLayoutResult ParseResult(string rawJson)
    {
        using var json = JsonDocument.Parse(rawJson);
        if (!json.RootElement.TryGetProperty("analyzeResult", out var analyzeResult) ||
            !analyzeResult.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            throw new DocumentLayoutException("Document Intelligence response did not include analyzeResult.content.");
        }

        return new DocumentLayoutResult(content.GetString() ?? string.Empty, rawJson);
    }

    private static string Limit(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }
}

public sealed class DocumentLayoutException(string message) : Exception(message);