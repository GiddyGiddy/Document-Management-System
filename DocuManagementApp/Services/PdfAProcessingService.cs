using System.Diagnostics;
using System.Text.Json;

namespace DocuManagementApp.Services;

public sealed record PdfAValidationResult(bool IsCompliant, string Report);

public interface IPdfAProcessingService
{
    Task<PdfAValidationResult> ConvertToPdfAAsync(string sourcePdfPath, string outputPdfPath, CancellationToken cancellationToken);
    Task<PdfAValidationResult> ValidateAsync(string pdfPath, CancellationToken cancellationToken);
}

public sealed record PdfAProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IPdfAProcessRunner
{
    Task<PdfAProcessResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class PdfAProcessRunner : IPdfAProcessRunner
{
    public async Task<PdfAProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start PDF/A tool '{executablePath}'.");
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            return new PdfAProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"PDF/A command '{executablePath}' exceeded the {timeout.TotalSeconds:0}-second timeout.");
        }
    }
}

public sealed class GhostscriptVeraPdfAProcessingService : IPdfAProcessingService
{
    private const string VeraPdfFlavour = "2b";
    private const string ContainerIccProfilePath = "/usr/share/ghostscript/iccprofiles/srgb.icc";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private readonly IPdfAProcessRunner _processRunner;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GhostscriptVeraPdfAProcessingService> _logger;
    private readonly string _containerRuntimeExecutablePath;
    private readonly string _ghostscriptImage;
    private readonly Uri _veraPdfRestBaseUri;
    private readonly TimeSpan _timeout;

    public GhostscriptVeraPdfAProcessingService(
        IConfiguration configuration,
        IPdfAProcessRunner processRunner,
        HttpClient httpClient,
        ILogger<GhostscriptVeraPdfAProcessingService> logger)
    {
        _processRunner = processRunner;
        _httpClient = httpClient;
        _logger = logger;
        var configuredContainerRuntime = configuration["PdfA:ContainerRuntimeExecutablePath"];
        _containerRuntimeExecutablePath = string.IsNullOrWhiteSpace(configuredContainerRuntime)
            ? (OperatingSystem.IsWindows() ? "podman.exe" : "podman")
            : configuredContainerRuntime;
        _ghostscriptImage = configuration["PdfA:GhostscriptImage"] ?? "docker.io/minidocks/ghostscript:latest";
        var veraPdfRestBaseUrl = configuration["PdfA:VeraPdfRestBaseUrl"] ?? "http://localhost:8080";
        if (!Uri.TryCreate(veraPdfRestBaseUrl, UriKind.Absolute, out var veraPdfRestBaseUri))
        {
            throw new InvalidOperationException("PdfA:VeraPdfRestBaseUrl must be an absolute URL.");
        }
        _veraPdfRestBaseUri = veraPdfRestBaseUri;
        _timeout = TimeSpan.FromSeconds(configuration.GetValue("PdfA:TimeoutSeconds", (int)DefaultTimeout.TotalSeconds));
    }

    public async Task<PdfAValidationResult> ConvertToPdfAAsync(
        string sourcePdfPath,
        string outputPdfPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePdfPath) || !File.Exists(sourcePdfPath))
        {
            return new PdfAValidationResult(false, "Source PDF path is missing or does not exist.");
        }
        if (string.IsNullOrWhiteSpace(outputPdfPath))
        {
            return new PdfAValidationResult(false, "Output PDF path is required.");
        }
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPdfPath));
        if (outputDirectory is null)
        {
            return new PdfAValidationResult(false, "Output PDF path does not have a valid directory.");
        }

        Directory.CreateDirectory(outputDirectory);
        var workDirectory = Path.Combine(Path.GetTempPath(), "pdfa-processing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var candidatePdfPath = Path.Combine(workDirectory, "candidate-pdfa.pdf");
        var definitionPath = Path.Combine(workDirectory, "PDFA_def.ps");

        try
        {
            await File.WriteAllTextAsync(definitionPath, CreatePdfaDefinition(ContainerIccProfilePath), cancellationToken);

            var ghostscriptArguments = new[]
            {
                "run",
                "--rm",
                "--volume",
                $"{Path.GetFullPath(sourcePdfPath)}:/input/source.pdf:ro",
                "--volume",
                $"{Path.GetFullPath(workDirectory)}:/work:rw",
                _ghostscriptImage,
                "-dPDFA=2",
                "-dBATCH",
                "-dNOPAUSE",
                "-dSAFER",
                "-sDEVICE=pdfwrite",
                "-dPDFACompatibilityPolicy=1",
                "-sColorConversionStrategy=RGB",
                "-sProcessColorModel=DeviceRGB",
                "-dEmbedAllFonts=true",
                "-dSubsetFonts=true",
                $"-sOutputICCProfile={ContainerIccProfilePath}",
                "-sOutputFile=/work/candidate-pdfa.pdf",
                "-f",
                "/work/PDFA_def.ps",
                "/input/source.pdf"
            };

            var conversion = await _processRunner.RunAsync(
                _containerRuntimeExecutablePath,
                ghostscriptArguments,
                _timeout,
                cancellationToken);
            if (conversion.ExitCode != 0 || !File.Exists(candidatePdfPath))
            {
                var details = string.Join(Environment.NewLine, new[] { conversion.StandardError, conversion.StandardOutput }.Where(x => !string.IsNullOrWhiteSpace(x)));
                return new PdfAValidationResult(false, $"Ghostscript PDF/A conversion failed with exit code {conversion.ExitCode}. {details}".Trim());
            }

            var validation = await ValidateAsync(candidatePdfPath, cancellationToken);
            if (!validation.IsCompliant)
            {
                return validation;
            }

            File.Copy(candidatePdfPath, outputPdfPath, overwrite: true);
            return validation;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "PDF/A conversion failed for '{SourcePdfPath}'.", sourcePdfPath);
            return new PdfAValidationResult(false, exception.Message);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    public async Task<PdfAValidationResult> ValidateAsync(string pdfPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
        {
            return new PdfAValidationResult(false, "PDF path is missing or does not exist.");
        }
        try
        {
            using var timeoutCts = new CancellationTokenSource(_timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var form = new MultipartFormDataContent();
            await using var pdfStream = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var fileContent = new StreamContent(pdfStream);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            form.Add(fileContent, "file", Path.GetFileName(pdfPath));

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(_veraPdfRestBaseUri, $"/api/validate/{VeraPdfFlavour}/"));
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-File-Size", pdfStream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Content = form;

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, linkedCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(linkedCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new PdfAValidationResult(false, $"veraPDF REST returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {responseBody}".Trim());
            }

            var isCompliant = ReadComplianceFromVeraPdfJson(responseBody);
            return new PdfAValidationResult(isCompliant, responseBody.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "PDF/A validation failed for '{PdfPath}'.", pdfPath);
            return new PdfAValidationResult(false, exception.Message);
        }
    }

    private static bool ReadComplianceFromVeraPdfJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var complianceValues = new List<bool>();
        CollectComplianceValues(document.RootElement, complianceValues);
        if (complianceValues.Count == 0)
        {
            throw new InvalidDataException("veraPDF output did not contain an isCompliant result; validation failed closed.");
        }

        return complianceValues.All(value => value);
    }

    private static void CollectComplianceValues(JsonElement element, ICollection<bool> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if ((string.Equals(property.Name, "isCompliant", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "compliant", StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    values.Add(property.Value.GetBoolean());
                }
                else
                {
                    CollectComplianceValues(property.Value, values);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectComplianceValues(item, values);
            }
        }
    }

    private static string CreatePdfaDefinition(string iccProfilePath)
    {
        var postScriptPath = iccProfilePath.Replace('\\', '/');
        postScriptPath = postScriptPath.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

            return $@"%
/ICCProfile ({postScriptPath}) def
[/_objdef {{icc_PDFA}} /type /stream /OBJ pdfmark
[{{icc_PDFA}} << /N 3 >> /PUT pdfmark
[{{icc_PDFA}} ICCProfile (r) file /PUT pdfmark
[/_objdef {{OutputIntent_PDFA}} /type /dict /OBJ pdfmark
[{{OutputIntent_PDFA}} <<
/Type /OutputIntent
/S /GTS_PDFA1
/DestOutputProfile {{icc_PDFA}}
/OutputConditionIdentifier (sRGB IEC61966-2.1)
/Info (sRGB IEC61966-2.1)
/RegistryName (http://www.color.org)
>> /PUT pdfmark
[{{Catalog}} <</OutputIntents [ {{OutputIntent_PDFA}} ]>> /PUT pdfmark
";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
