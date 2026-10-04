namespace ExtractionService;

public sealed record InvoiceExtraction
{
    public string VendorName { get; init; } = string.Empty;
    public string InvoiceNumber { get; init; } = string.Empty;
    public DateOnly? InvoiceDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public string Currency { get; init; } = string.Empty;
    public IReadOnlyList<InvoiceLineItem> LineItems { get; init; } = [];
    public decimal Subtotal { get; init; }
    public decimal Tax { get; init; }
    public decimal Total { get; init; }
}

public sealed record InvoiceLineItem
{
    public string Description { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal LineTotal { get; init; }
}

public sealed record ContractExtraction
{
    public string ContractType { get; init; } = string.Empty;
    public IReadOnlyList<string> Parties { get; init; } = [];
    public DateOnly? EffectiveDate { get; init; }
    public DateOnly? ExpirationDate { get; init; }
    public string? GoverningLaw { get; init; }
    public IReadOnlyList<string> KeyObligations { get; init; } = [];
    public string? TerminationClause { get; init; }
}

public sealed record IdentityDocumentExtraction
{
    public string DocumentType { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string DocumentNumber { get; init; } = string.Empty;
    public string? IssuingCountry { get; init; }
    public DateOnly? IssueDate { get; init; }
    public DateOnly? ExpiryDate { get; init; }
    public DateOnly? DateOfBirth { get; init; }
}

public sealed record FinancialStatementExtraction
{
    public string EntityName { get; init; } = string.Empty;
    public string StatementType { get; init; } = string.Empty;
    public DateOnly? PeriodStart { get; init; }
    public DateOnly? PeriodEnd { get; init; }
    public string? Currency { get; init; }
    public decimal? Revenue { get; init; }
    public decimal? Expenses { get; init; }
    public decimal? NetIncome { get; init; }
    public decimal? Assets { get; init; }
    public decimal? Liabilities { get; init; }
    public decimal? Equity { get; init; }
}

public sealed record GeneralDocumentExtraction
{
    public string DocumentType { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<ExtractedFact> KeyFacts { get; init; } = [];
    public IReadOnlyList<ExtractedTable> Tables { get; init; } = [];
}

public sealed record ExtractedFact(string Name, string Value);

public sealed record ExtractedTable(
    string? Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows);

public sealed record ExtractionValidationIssue(string Path, string Message);

public static class SupportedDocumentKinds
{
    public const string Invoice = "Invoice";
    public const string Contract = "Contract";
    public const string IdentityDocument = "IdentityDocument";
    public const string FinancialStatement = "FinancialStatement";
    public const string Pdf = "Pdf";
    public const string Image = "Image";
    public const string Spreadsheet = "Spreadsheet";
    public const string Text = "Text";
    public const string Unknown = "Unknown";

    public static IReadOnlyList<string> All { get; } =
    [Invoice, Contract, IdentityDocument, FinancialStatement, Pdf, Image, Spreadsheet, Text, Unknown];
}

public abstract record DocumentExtractionOutcome(int Attempts, string DocumentKind);

public sealed record ExtractionSuccess(
    int Attempts,
    string DocumentKind,
    string Json) : DocumentExtractionOutcome(Attempts, DocumentKind);

public sealed record ExtractionFailed(
    int Attempts,
    string DocumentKind,
    string? LastCandidateJson,
    IReadOnlyList<ExtractionValidationIssue> Issues) : DocumentExtractionOutcome(Attempts, DocumentKind);