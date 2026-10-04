using ExtractionService;

namespace DocuManagementApp.Tests;

public sealed class DocumentExtractionValidatorTests
{
    public static IEnumerable<object[]> SupportedKindCandidates =>
    [
        [SupportedDocumentKinds.Invoice, DocumentCriticLoopTests.ValidInvoiceJson],
        [SupportedDocumentKinds.Contract, """
            {"contractType":"Services agreement","parties":["Acme Ltd","Example LLC"],"effectiveDate":"2026-01-01","expirationDate":"2026-12-31","governingLaw":"New York","keyObligations":["Provide services"],"terminationClause":"30 days notice"}
            """],
        [SupportedDocumentKinds.IdentityDocument, """
            {"documentType":"Passport","fullName":"Example Person","documentNumber":"A1234567","issuingCountry":"US","issueDate":"2020-01-01","expiryDate":"2030-01-01","dateOfBirth":"1990-01-01"}
            """],
        [SupportedDocumentKinds.FinancialStatement, """
            {"entityName":"Example Inc","statementType":"Income statement","periodStart":"2026-01-01","periodEnd":"2026-12-31","currency":"USD","revenue":100,"expenses":30,"netIncome":70,"assets":300,"liabilities":150,"equity":150}
            """],
        [SupportedDocumentKinds.Pdf, GeneralDocumentJson],
        [SupportedDocumentKinds.Image, GeneralDocumentJson],
        [SupportedDocumentKinds.Spreadsheet, GeneralDocumentJson],
        [SupportedDocumentKinds.Text, GeneralDocumentJson],
        [SupportedDocumentKinds.Unknown, GeneralDocumentJson]
    ];

    private const string GeneralDocumentJson = """
        {
          "documentType": "Report",
          "title": "Quarterly overview",
          "summary": "A concise report summary.",
          "keyFacts": [{"name":"quarter","value":"Q1"}],
          "tables": [{"title":"Revenue","columns":["Region","Amount"],"rows":[["North","100"]]}]
        }
        """;

    [Theory]
    [MemberData(nameof(SupportedKindCandidates))]
    public void EveryClassifiedKindHasInstructionsAndAcceptsItsSchema(string kind, string candidateJson)
    {
        var validator = new DocumentExtractionValidator();

        var instructions = DocumentExtractionSchemas.GetInstructions(kind);
        var issues = validator.Validate(kind, candidateJson);

        Assert.Contains(kind, instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(issues);
    }

    [Fact]
    public void FinancialStatementArithmeticMismatchIsRejected()
    {
        const string candidate = """
            {"entityName":"Example Inc","statementType":"Income statement","periodStart":"2026-01-01","periodEnd":"2026-12-31","currency":"USD","revenue":100,"expenses":30,"netIncome":80,"assets":300,"liabilities":150,"equity":150}
            """;

        var issues = new DocumentExtractionValidator().Validate(SupportedDocumentKinds.FinancialStatement, candidate);

        Assert.Contains(issues, issue => issue.Path == "netIncome");
    }

    [Fact]
    public void GeneralDocumentRejectsRowsThatDoNotMatchTableColumns()
    {
        const string candidate = """
            {"documentType":"Spreadsheet","title":null,"summary":"Monthly amounts.","keyFacts":[],"tables":[{"title":"Amounts","columns":["Month","Amount"],"rows":[["January"]]}]}
            """;

        var issues = new DocumentExtractionValidator().Validate(SupportedDocumentKinds.Spreadsheet, candidate);

        Assert.Contains(issues, issue => issue.Path == "tables[0].rows[0]");
    }
}