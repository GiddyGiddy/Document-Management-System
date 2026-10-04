namespace ExtractionService;

public interface IDocumentExtractionAgent
{
    Task<string> ExtractAsync(string documentKind, string markdown, CancellationToken cancellationToken);
}

public interface IDocumentCorrectionAgent
{
    Task<string> CorrectAsync(
        string documentKind,
        string markdown,
        string candidateJson,
        IReadOnlyList<ExtractionValidationIssue> issues,
        CancellationToken cancellationToken);
}

public sealed class DocumentCriticLoop(
    IDocumentExtractionAgent extractionAgent,
    IDocumentCorrectionAgent correctionAgent,
    DocumentExtractionValidator validator)
{
    private const int MaximumAttempts = 3;

    public async Task<DocumentExtractionOutcome> RunAsync(string documentKind, string markdown, CancellationToken cancellationToken)
    {
        var normalizedKind = DocumentExtractionSchemas.NormalizeKind(documentKind);
        var candidateJson = await extractionAgent.ExtractAsync(normalizedKind, markdown, cancellationToken);
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            var issues = validator.Validate(normalizedKind, candidateJson);
            if (issues.Count == 0)
            {
                return new ExtractionSuccess(attempt, normalizedKind, candidateJson);
            }

            if (attempt == MaximumAttempts)
            {
                return new ExtractionFailed(attempt, normalizedKind, candidateJson, issues);
            }

            candidateJson = await correctionAgent.CorrectAsync(normalizedKind, markdown, candidateJson, issues, cancellationToken);
        }

        throw new InvalidOperationException("The invoice critic loop exited without a result.");
    }
}