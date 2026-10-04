using System.Text.Json;

namespace ExtractionService;

public sealed record ExtractionArtifact(
    Guid TaskId,
    Guid CorrelationId,
    Guid DocumentId,
    string DocumentKind,
    string LayoutMarkdown,
    JsonElement Layout,
    JsonElement ExtractionResult,
    int Attempts,
    DateTimeOffset UpdatedAtUtc);

public static class ExtractionArtifactReference
{
    public static string Create(string publicBaseUrl, Guid taskId)
    {
        var baseUri = new Uri(publicBaseUrl.EndsWith('/') ? publicBaseUrl : publicBaseUrl + "/", UriKind.Absolute);
        return new Uri(baseUri, $"api/internal/extraction-artifacts/{taskId:D}").ToString();
    }
}

public sealed class ExtractionArtifactApiOptions
{
    public string ListenUrl { get; set; } = "http://localhost:5085";
    public string PublicBaseUrl { get; set; } = "http://localhost:5085";
    public string ApiKey { get; set; } = string.Empty;
}