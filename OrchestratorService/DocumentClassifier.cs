namespace OrchestratorService;

public enum DocumentKind
{
    Invoice,
    Contract,
    IdentityDocument,
    FinancialStatement,
    Pdf,
    Image,
    Spreadsheet,
    Text,
    Unknown
}

public interface IDocumentClassifier
{
    DocumentKind Classify(string fileName, string? contentType);
}

public sealed class DocumentClassifier : IDocumentClassifier
{
    public DocumentKind Classify(string fileName, string? contentType)
    {
        var normalizedName = Path.GetFileName(fileName).ToLowerInvariant();
        if (ContainsAny(normalizedName, "invoice", "bill"))
        {
            return DocumentKind.Invoice;
        }

        if (ContainsAny(normalizedName, "contract", "agreement", "nda"))
        {
            return DocumentKind.Contract;
        }

        if (ContainsAny(normalizedName, "passport", "identity", "driver-license", "drivers-license"))
        {
            return DocumentKind.IdentityDocument;
        }

        if (ContainsAny(normalizedName, "financial-statement", "annual-report", "balance-sheet"))
        {
            return DocumentKind.FinancialStatement;
        }

        var mediaType = contentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (mediaType == "application/pdf" || Path.GetExtension(normalizedName) == ".pdf")
        {
            return DocumentKind.Pdf;
        }

        if (mediaType?.StartsWith("image/", StringComparison.Ordinal) == true)
        {
            return DocumentKind.Image;
        }

        if (mediaType is "text/csv" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" ||
            Path.GetExtension(normalizedName) is ".csv" or ".xls" or ".xlsx")
        {
            return DocumentKind.Spreadsheet;
        }

        if (mediaType?.StartsWith("text/", StringComparison.Ordinal) == true ||
            Path.GetExtension(normalizedName) is ".txt" or ".md")
        {
            return DocumentKind.Text;
        }

        return DocumentKind.Unknown;
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.Ordinal));
    }
}