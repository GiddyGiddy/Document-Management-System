using System.Diagnostics;
using System.IO;

namespace DocuManagementApp.Services
{
  public sealed class OfficeConversionResult
  {
    public bool Success { get; set; }
    public string? OutputPdfPath { get; set; }
    public string? ErrorMessage { get; set; }
  }

  public interface IOfficeToPdfConversionService
  {
    bool IsSupportedExtension(string fileName);
    Task<OfficeConversionResult> ConvertToPdfAsync(string sourceFilePath, string outputDirectory, CancellationToken cancellationToken);
  }

  // Converts Word/Excel documents to PDF using a local LibreOffice install or its container image.
  public sealed class OfficeToPdfConversionService : IOfficeToPdfConversionService
  {
    private static readonly string[] SupportedExtensions =
      [".doc", ".docx", ".rtf", ".odt", ".xls", ".xlsx", ".ods"];

    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(2);

    private readonly string? _sofficePath;
    private readonly string _containerRuntimePath;
    private readonly string _containerImage;
    private readonly IPdfAProcessRunner _processRunner;
    private readonly ILogger<OfficeToPdfConversionService> _logger;

    public OfficeToPdfConversionService(
      IConfiguration configuration,
      IPdfAProcessRunner processRunner,
      ILogger<OfficeToPdfConversionService> logger)
    {
      _logger = logger;
      _processRunner = processRunner;
      _sofficePath = ResolveSofficePath(configuration);
      var configuredRuntimePath = configuration["LibreOffice:ContainerRuntimeExecutablePath"];
      _containerRuntimePath = string.IsNullOrWhiteSpace(configuredRuntimePath)
        ? (OperatingSystem.IsWindows() ? "podman.exe" : "podman")
        : configuredRuntimePath;
      _containerImage = configuration["LibreOffice:ContainerImage"] ?? "docker.io/linuxserver/libreoffice:latest";
    }

    public bool IsSupportedExtension(string fileName)
    {
      return SupportedExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());
    }

    public async Task<OfficeConversionResult> ConvertToPdfAsync(string sourceFilePath, string outputDirectory, CancellationToken cancellationToken)
    {
      if (!File.Exists(sourceFilePath))
      {
        return new OfficeConversionResult { Success = false, ErrorMessage = "Source file not found." };
      }

      Directory.CreateDirectory(outputDirectory);

      var outputPdfPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(sourceFilePath) + ".pdf");
      try
      {
        PdfAProcessResult processResult;
        if (_sofficePath is not null)
        {
          processResult = await RunLocalLibreOfficeAsync(sourceFilePath, outputDirectory, cancellationToken);
        }
        else
        {
          processResult = await RunContainerLibreOfficeAsync(sourceFilePath, outputDirectory, cancellationToken);
        }

        if (processResult.ExitCode != 0)
        {
          var details = string.Join(Environment.NewLine, new[] { processResult.StandardError, processResult.StandardOutput }
            .Where(output => !string.IsNullOrWhiteSpace(output)));
          _logger.LogError("LibreOffice conversion of '{SourceFile}' failed with exit code {ExitCode}. Output: {Output}",
            sourceFilePath, processResult.ExitCode, details);
          return new OfficeConversionResult
          {
            Success = false,
            ErrorMessage = $"LibreOffice exited with code {processResult.ExitCode}: {details}".Trim()
          };
        }

        if (!File.Exists(outputPdfPath))
        {
          var diagnostics = string.Join(Environment.NewLine, new[] { processResult.StandardError, processResult.StandardOutput }
            .Where(output => !string.IsNullOrWhiteSpace(output)));
          return new OfficeConversionResult
          {
            Success = false,
            ErrorMessage = $"Conversion completed but the output PDF was not found at '{outputPdfPath}'. {diagnostics}".Trim()
          };
        }

        return new OfficeConversionResult { Success = true, OutputPdfPath = outputPdfPath };
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
        return new OfficeConversionResult { Success = false, ErrorMessage = $"LibreOffice conversion timed out after {ConversionTimeout.TotalSeconds:0} seconds." };
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Unexpected error while converting '{SourceFile}' to PDF.", sourceFilePath);
        return new OfficeConversionResult { Success = false, ErrorMessage = ex.Message };
      }
    }

    private async Task<PdfAProcessResult> RunLocalLibreOfficeAsync(
      string sourceFilePath,
      string outputDirectory,
      CancellationToken cancellationToken)
    {
      var startInfo = new ProcessStartInfo
      {
        FileName = _sofficePath!,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
      };
      startInfo.ArgumentList.Add("--headless");
      startInfo.ArgumentList.Add("--norestore");
      startInfo.ArgumentList.Add("--convert-to");
      startInfo.ArgumentList.Add("pdf");
      startInfo.ArgumentList.Add("--outdir");
      startInfo.ArgumentList.Add(outputDirectory);
      startInfo.ArgumentList.Add(sourceFilePath);

      using var timeoutCts = new CancellationTokenSource(ConversionTimeout);
      using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
      using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Failed to start LibreOffice at '{_sofficePath}'.");
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

        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException($"LibreOffice conversion timed out after {ConversionTimeout.TotalSeconds:0} seconds.");
      }
    }

    private Task<PdfAProcessResult> RunContainerLibreOfficeAsync(
      string sourceFilePath,
      string outputDirectory,
      CancellationToken cancellationToken)
    {
      var inputFileName = Path.GetFileName(sourceFilePath);
      var containerSourcePath = $"/work/input/{inputFileName}";
      var arguments = new[]
      {
        "run",
        "--rm",
        "--entrypoint",
        "/usr/bin/soffice",
        "--volume",
        $"{Path.GetFullPath(sourceFilePath)}:{containerSourcePath}:ro",
        "--volume",
        $"{Path.GetFullPath(outputDirectory)}:/work/output:rw",
        _containerImage,
        "--headless",
        "--norestore",
        "--convert-to",
        "pdf",
        "--outdir",
        "/work/output",
        containerSourcePath
      };

      return _processRunner.RunAsync(_containerRuntimePath, arguments, ConversionTimeout, cancellationToken);
    }

    private static string? ResolveSofficePath(IConfiguration configuration)
    {
      var configured = configuration["LibreOffice:ExecutablePath"];
      if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
      {
        return configured;
      }

      string[] candidates =
      [
        @"C:\Program Files\LibreOffice\program\soffice.exe",
        @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
        "/usr/bin/soffice",
        "/usr/bin/libreoffice",
        "/opt/libreoffice/program/soffice"
      ];

      return candidates.FirstOrDefault(File.Exists);
    }
  }
}
