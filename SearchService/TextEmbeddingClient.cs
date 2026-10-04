using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SearchService;

public interface ITextEmbeddingClient
{
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

public sealed class OpenAiCompatibleEmbeddingClient(
    HttpClient httpClient,
    SearchOptions options) : ITextEmbeddingClient
{
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var settings = options.Embeddings;
        if (!settings.IsConfigured || !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("Search embeddings require a supported provider, endpoint, and model name; Microsoft Foundry also requires its API key.");
        }

        var requestUri = new Uri(new Uri(endpoint.ToString().TrimEnd('/') + "/"), "embeddings");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = settings.ModelName,
            input = texts
        }), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            var apiKeyHeaderName = settings.ResolvedApiKeyHeaderName;
            if (string.Equals(apiKeyHeaderName, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(apiKeyHeaderName, settings.ApiKey);
            }
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Embedding endpoint returned HTTP {(int)response.StatusCode}.");
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Embedding response is missing its data array.");
        }

        var embeddings = data.EnumerateArray()
            .OrderBy(item => item.TryGetProperty("index", out var index) ? index.GetInt32() : 0)
            .Select(item =>
            {
                if (!item.TryGetProperty("embedding", out var vector) || vector.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("Embedding response contains an item without an embedding vector.");
                }

                return vector.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            })
            .ToArray();

        if (embeddings.Length != texts.Count || embeddings.Any(vector => vector.Length != options.EmbeddingDimensions))
        {
            throw new InvalidDataException("Embedding response count or vector dimensions did not match Search configuration.");
        }

        return embeddings;
    }
}