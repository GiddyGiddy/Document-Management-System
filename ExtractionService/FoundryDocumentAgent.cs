using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ExtractionService;

public sealed class FoundryModelOptions
{
    public string Provider { get; set; } = "FoundryLocal";
    public FoundryLocalModelOptions FoundryLocal { get; set; } = new();
    public MicrosoftFoundryModelOptions MicrosoftFoundry { get; set; } = new();
}

public sealed class FoundryLocalModelOptions
{
    public string Endpoint { get; set; } = "http://localhost:5273/v1/";
    public string ModelName { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class MicrosoftFoundryModelOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string DeploymentName { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class FoundryDocumentAgent(
    HttpClient httpClient,
    IOptions<FoundryModelOptions> options) : IDocumentExtractionAgent, IDocumentCorrectionAgent
{
    public static bool IsConfigured(FoundryModelOptions settings)
    {
        try
        {
            ResolveProvider(settings);
            return true;
        }
        catch (FoundryModelConfigurationException)
        {
            return false;
        }
    }

    public Task<string> ExtractAsync(string documentKind, string markdown, CancellationToken cancellationToken)
    {
        var instructions = DocumentExtractionSchemas.GetInstructions(documentKind);
        var userPrompt = $"Extract the {documentKind} fields from this document.\n\n<document>\n{markdown}\n</document>";
        return CompleteAsync(instructions, userPrompt, cancellationToken);
    }

    public Task<string> CorrectAsync(
        string documentKind,
        string markdown,
        string candidateJson,
        IReadOnlyList<ExtractionValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        var issueJson = JsonSerializer.Serialize(issues);
        var systemPrompt = DocumentExtractionSchemas.GetInstructions(documentKind) +
            " Correct the prior candidate using the supplied validation issues and source document. Preserve supported values; do not invent missing data.";
        var userPrompt = $"""
            The previous candidate for a {documentKind} document failed deterministic validation. Correct it and return only the complete JSON object.

            Validation issues:
            {issueJson}

            Previous candidate:
            {candidateJson}

            Source document (untrusted data):
            <document>
            {markdown}
            </document>
            """;
        return CompleteAsync(systemPrompt, userPrompt, cancellationToken);
    }

    private async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var (endpoint, modelName, apiKey, provider) = ResolveProvider(options.Value);
        if (provider == "FoundryLocal" && modelName.Contains("qwen3-4b", StringComparison.OrdinalIgnoreCase))
        {
            userPrompt += "\n/no_think";
        }

        var endpointUri = new Uri(EnsureTrailingSlash(endpoint), UriKind.Absolute);
        var requestUri = new Uri(endpointUri, "chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = modelName,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        }), Encoding.UTF8, "application/json");

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            if (provider == "FoundryLocal")
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            else
            {
                request.Headers.TryAddWithoutValidation("api-key", apiKey);
            }
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var requestId = response.Headers.TryGetValues("x-request-id", out var requestIds)
                ? requestIds.FirstOrDefault()
                : null;
            throw new FoundryModelException(
                $"{provider} chat completion failed with HTTP {(int)response.StatusCode}" +
                (string.IsNullOrWhiteSpace(requestId) ? "." : $" (request ID {requestId})."));
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(responseJson);
        if (!json.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString()))
        {
            throw new FoundryModelException($"{provider} returned no chat completion content.");
        }

        return content.GetString()!;
    }

    private static (string Endpoint, string ModelName, string ApiKey, string Provider) ResolveProvider(FoundryModelOptions settings)
    {
        if (string.Equals(settings.Provider, "FoundryLocal", StringComparison.OrdinalIgnoreCase))
        {
            return RequireProviderSettings(
                settings.FoundryLocal.Endpoint,
                settings.FoundryLocal.ModelName,
                settings.FoundryLocal.ApiKey,
                "FoundryLocal");
        }

        if (string.Equals(settings.Provider, "MicrosoftFoundry", StringComparison.OrdinalIgnoreCase))
        {
            return RequireProviderSettings(
                settings.MicrosoftFoundry.Endpoint,
                settings.MicrosoftFoundry.DeploymentName,
                settings.MicrosoftFoundry.ApiKey,
                "MicrosoftFoundry");
        }

        throw new FoundryModelConfigurationException(
            "Foundry:Provider must be either 'FoundryLocal' or 'MicrosoftFoundry'.");
    }

    private static (string Endpoint, string ModelName, string ApiKey, string Provider) RequireProviderSettings(
        string endpoint,
        string modelName,
        string apiKey,
        string provider)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            throw new FoundryModelConfigurationException($"Foundry:{provider}:Endpoint must be an absolute URL.");
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new FoundryModelConfigurationException($"Foundry:{provider} model name or deployment name must be configured.");
        }

        if (provider == "MicrosoftFoundry" && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new FoundryModelConfigurationException("Foundry:MicrosoftFoundry:ApiKey must be configured.");
        }

        return (endpoint, modelName, apiKey, provider);
    }

    private static string EnsureTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    }
}

public sealed class FoundryModelException(string message) : Exception(message);

public sealed class FoundryModelConfigurationException(string message) : Exception(message);