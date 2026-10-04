using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SearchService;

public interface IExtractionArtifactClient
{
    Task<ExtractionArtifactResponse> GetAsync(string artifactReference, CancellationToken cancellationToken);
}

public sealed class ExtractionArtifactClient(
    HttpClient httpClient,
    SearchOptions options) : IExtractionArtifactClient
{
    public async Task<ExtractionArtifactResponse> GetAsync(string artifactReference, CancellationToken cancellationToken)
    {
        var configuredBaseUri = new Uri(options.ArtifactApi.BaseUrl.EndsWith('/')
            ? options.ArtifactApi.BaseUrl
            : options.ArtifactApi.BaseUrl + "/", UriKind.Absolute);
        if (!Uri.TryCreate(artifactReference, UriKind.Absolute, out var artifactUri) ||
            !string.Equals(artifactUri.GetLeftPart(UriPartial.Authority), configuredBaseUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) ||
            !artifactUri.AbsolutePath.StartsWith("/api/internal/extraction-artifacts/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The extraction artifact URL is outside the configured artifact API.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, artifactUri);
        request.Headers.TryAddWithoutValidation("X-Extraction-Artifact-Key", options.ArtifactApi.ApiKey);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException("The extraction artifact was not found.");
        }

        response.EnsureSuccessStatusCode();
        var artifact = await response.Content.ReadFromJsonAsync<ExtractionArtifactResponse>(cancellationToken: cancellationToken);
        return artifact ?? throw new InvalidDataException("The extraction artifact response was empty.");
    }
}