using DocuManagementApp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocuManagementApp.Tests;

public sealed class OfficeToPdfConversionServiceTests
{
    private readonly FakeProcessRunner _processRunner = new();
    private readonly OfficeToPdfConversionService _service;

    public OfficeToPdfConversionServiceTests()
    {
        _service = new OfficeToPdfConversionService(
            new ConfigurationBuilder().Build(),
            _processRunner,
            NullLogger<OfficeToPdfConversionService>.Instance);
    }

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

    [Fact]
    public async Task ConvertToPdfRunsLibreOfficeContainerAndFindsOutputPdf()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var sourcePath = Path.Combine(workingDirectory, "report.docx");
        var outputDirectory = Path.Combine(workingDirectory, "output");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        _processRunner.CreateOutputPdf = true;

        try
        {
            var result = await _service.ConvertToPdfAsync(sourcePath, outputDirectory, CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(Path.Combine(outputDirectory, "report.pdf"), result.OutputPdfPath);
            Assert.Equal("podman.exe", _processRunner.ExecutablePath);
            Assert.Contains("docker.io/linuxserver/libreoffice:latest", _processRunner.Arguments);
            Assert.Contains("/usr/bin/soffice", _processRunner.Arguments);
            Assert.Contains("--headless", _processRunner.Arguments);
            Assert.Contains(_processRunner.Arguments, argument => argument.EndsWith(":/work/input/report.docx:ro", StringComparison.Ordinal));
            Assert.Contains(_processRunner.Arguments, argument => argument.EndsWith(":/work/output:rw", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    private sealed class FakeProcessRunner : IPdfAProcessRunner
    {
        public string? ExecutablePath { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public bool CreateOutputPdf { get; set; }

        public async Task<PdfAProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ExecutablePath = executablePath;
            Arguments = arguments;
            if (CreateOutputPdf)
            {
                var outputMount = arguments.Single(argument => argument.EndsWith(":/work/output:rw", StringComparison.Ordinal));
                var outputDirectory = outputMount[..^":/work/output:rw".Length];
                Directory.CreateDirectory(outputDirectory);
                await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "report.pdf"), [4, 5, 6], cancellationToken);
            }

            return new PdfAProcessResult(0, "convert /work/input/report.docx -> /work/output/report.pdf", string.Empty);
        }
    }
}