using DocuManagementApp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocuManagementApp.Tests;

public sealed class OfficeToPdfConversionServiceTests
{
    private readonly OfficeToPdfConversionService _service = new(
        new ConfigurationBuilder().Build(),
        NullLogger<OfficeToPdfConversionService>.Instance);

    [Theory]
    [InlineData("report.doc")]
    [InlineData("report.docx")]
    [InlineData("report.rtf")]
    [InlineData("report.odt")]
    [InlineData("report.xls")]
    [InlineData("report.xlsx")]
    [InlineData("report.ods")]
    [InlineData("REPORT.DOCX")]
    public void IsSupportedExtensionAcceptsSupportedOfficeDocuments(string fileName)
    {
        Assert.True(_service.IsSupportedExtension(fileName));
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("report.txt")]
    [InlineData("report.csv")]
    [InlineData("report")]
    public void IsSupportedExtensionRejectsUnsupportedDocuments(string fileName)
    {
        Assert.False(_service.IsSupportedExtension(fileName));
    }

    [Fact]
    public async Task ConvertToPdfReturnsFailureWhenSourceIsMissing()
    {
        var missingFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.docx");
        var outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var result = await _service.ConvertToPdfAsync(missingFile, outputDirectory, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Source file not found.", result.ErrorMessage);
        Assert.False(Directory.Exists(outputDirectory));
    }
}