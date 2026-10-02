using System.Net;
using System.Net.Http.Headers;
using DocuManagementApp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocuManagementApp.Tests;

public sealed class PdfAProcessingServiceTests : IDisposable
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConvertUsesGhostscriptContainerAndValidatesCandidateWithVeraPdfRest()
    {
        Directory.CreateDirectory(_workingDirectory);
        var source = Path.Combine(_workingDirectory, "source.pdf");
        var output = Path.Combine(_workingDirectory, "archive.pdf");
        await File.WriteAllBytesAsync(source, "%PDF-1.4 source"u8.ToArray());
        var rest = CreateService("""{"report":{"jobs":[{"validationResult":[{"compliant":true}]}]}}""");

        var result = await rest.Service.ConvertToPdfAAsync(source, output, CancellationToken.None);

        Assert.True(result.IsCompliant, result.Report);
        Assert.True(File.Exists(output));
        Assert.Equal("%PDF-2B test output"u8.ToArray(), await File.ReadAllBytesAsync(output));
        Assert.Equal("podman.exe", rest.Runner.Executable);
        Assert.Contains("run", rest.Runner.Arguments);
        Assert.Contains("docker.io/minidocks/ghostscript:latest", rest.Runner.Arguments);
        Assert.Contains("-dPDFA=2", rest.Runner.Arguments);
        Assert.Contains("-sOutputICCProfile=/usr/share/ghostscript/iccprofiles/srgb.icc", rest.Runner.Arguments);
        Assert.Contains(rest.Runner.Arguments, argument => argument.EndsWith(":/input/source.pdf:ro", StringComparison.Ordinal));
        Assert.Contains("/S /GTS_PDFA1", rest.Runner.PdfaDefinition);
        Assert.Equal("http://localhost:8080/api/validate/2b/", rest.Handler.RequestUri?.ToString());
        Assert.Equal("POST", rest.Handler.Method);
        Assert.Equal("file", rest.Handler.MultipartFieldName);
        Assert.Equal("candidate-pdfa.pdf", rest.Handler.UploadedFileName);
    }

    [Fact]
    public async Task ConvertDoesNotPublishOutputWhenVeraPdfReportsNonCompliance()
    {
        Directory.CreateDirectory(_workingDirectory);
        var source = Path.Combine(_workingDirectory, "source.pdf");
        var output = Path.Combine(_workingDirectory, "archive.pdf");
        await File.WriteAllBytesAsync(source, "%PDF-1.4 source"u8.ToArray());
        var rest = CreateService("""{"report":{"jobs":[{"validationResult":[{"compliant":false}]}]}}""");

        var result = await rest.Service.ConvertToPdfAAsync(source, output, CancellationToken.None);

        Assert.False(result.IsCompliant);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ValidateFailsClosedWhenRestResponseHasNoComplianceResult()
    {
        Directory.CreateDirectory(_workingDirectory);
        var source = Path.Combine(_workingDirectory, "source.pdf");
        await File.WriteAllBytesAsync(source, "%PDF-1.4 source"u8.ToArray());
        var rest = CreateService("{}");

        var result = await rest.Service.ValidateAsync(source, CancellationToken.None);

        Assert.False(result.IsCompliant);
        Assert.Contains("compliant", result.Report, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("http://localhost:8080/api/validate/2b/", rest.Handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task ValidateReturnsFailureWhenRestServiceReturnsHttpError()
    {
        Directory.CreateDirectory(_workingDirectory);
        var source = Path.Combine(_workingDirectory, "source.pdf");
        await File.WriteAllBytesAsync(source, "%PDF-1.4 source"u8.ToArray());
        var rest = CreateService("validator unavailable", HttpStatusCode.ServiceUnavailable);

        var result = await rest.Service.ValidateAsync(source, CancellationToken.None);

        Assert.False(result.IsCompliant);
        Assert.Contains("HTTP 503", result.Report);
    }

    [Fact]
    public async Task ConvertFailsWhenSourceIsMissing()
    {
        Directory.CreateDirectory(_workingDirectory);
        var missingSource = Path.Combine(_workingDirectory, "missing.pdf");
        var rest = CreateService("{}");

        var result = await rest.Service.ConvertToPdfAAsync(missingSource, Path.Combine(_workingDirectory, "archive.pdf"), CancellationToken.None);

        Assert.False(result.IsCompliant);
        Assert.Contains("does not exist", result.Report);
        Assert.Null(rest.Runner.Executable);
        Assert.Null(rest.Handler.RequestUri);
    }

    private (GhostscriptVeraPdfAProcessingService Service, FakeVeraPdfRestHandler Handler, FakeContainerProcessRunner Runner) CreateService(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var settings = new Dictionary<string, string?>
        {
            ["PdfA:ContainerRuntimeExecutablePath"] = "podman.exe",
            ["PdfA:GhostscriptImage"] = "docker.io/minidocks/ghostscript:latest",
            ["PdfA:VeraPdfRestBaseUrl"] = "http://localhost:8080",
            ["PdfA:TimeoutSeconds"] = "10"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var handler = new FakeVeraPdfRestHandler(responseBody, statusCode);
        var runner = new FakeContainerProcessRunner();
        var service = new GhostscriptVeraPdfAProcessingService(
            configuration,
            runner,
            new HttpClient(handler),
            NullLogger<GhostscriptVeraPdfAProcessingService>.Instance);
        return (service, handler, runner);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    private sealed class FakeContainerProcessRunner : IPdfAProcessRunner
    {
        public string? Executable { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public string PdfaDefinition { get; private set; } = string.Empty;

        public async Task<PdfAProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Executable = executablePath;
            Arguments = arguments;
            var workVolume = arguments.Single(argument => argument.EndsWith(":/work:rw", StringComparison.Ordinal));
            var hostWorkDirectory = workVolume[..^":/work:rw".Length];
            var definitionPath = Path.Combine(hostWorkDirectory, "PDFA_def.ps");
            PdfaDefinition = await File.ReadAllTextAsync(definitionPath, cancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(hostWorkDirectory, "candidate-pdfa.pdf"),
                "%PDF-2B test output"u8.ToArray(),
                cancellationToken);
            return new PdfAProcessResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class FakeVeraPdfRestHandler(string responseBody, HttpStatusCode statusCode) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Method { get; private set; }
        public string? MultipartFieldName { get; private set; }
        public string? UploadedFileName { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method.Method;
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var uploadedFile = Assert.Single(form);
            MultipartFieldName = uploadedFile.Headers.ContentDisposition?.Name?.Trim('"');
            UploadedFileName = uploadedFile.Headers.ContentDisposition?.FileName?.Trim('"');
            Assert.Contains("X-File-Size", request.Headers.Select(header => header.Key));
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, new MediaTypeHeaderValue("application/json"))
            };
        }
    }
}
