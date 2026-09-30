using System.Diagnostics;

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

  // Converts Word/Excel documents to PDF by shelling out to a headless LibreOffice install.
  public sealed class OfficeToPdfConversionService : IOfficeToPdfConversionService
  {
    private static readonly string[] SupportedExtensions =
      [".doc", ".docx", ".rtf", ".odt", ".xls", ".xlsx", ".ods"];

    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(2);

    private readonly string _sofficePath;
    private readonly ILogger<OfficeToPdfConversionService> _logger;

    public OfficeToPdfConversionService(IConfiguration configuration, ILogger<OfficeToPdfConversionService> logger)
    {
      _logger = logger;
      _sofficePath = ResolveSofficePath(configuration);
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

      var startInfo = new ProcessStartInfo
      {
        FileName = _sofficePath,
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

      try
      {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
          return new OfficeConversionResult { Success = false, ErrorMessage = $"Failed to start LibreOffice at '{_sofficePath}'." };
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stdErrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
        await process.WaitForExitAsync(linkedCts.Token);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
          _logger.LogError("LibreOffice conversion of '{SourceFile}' failed with exit code {ExitCode}. Stdout: {StdOut} Stderr: {StdErr}",
            sourceFilePath, process.ExitCode, stdOut, stdErr);
          return new OfficeConversionResult { Success = false, ErrorMessage = $"LibreOffice exited with code {process.ExitCode}: {stdErr}" };
        }

        var outputPdfPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(sourceFilePath) + ".pdf");
        if (!File.Exists(outputPdfPath))
        {
          return new OfficeConversionResult { Success = false, ErrorMessage = "Conversion completed but the output PDF was not found." };
        }

        return new OfficeConversionResult { Success = true, OutputPdfPath = outputPdfPath };
      }
      catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
      {
        return new OfficeConversionResult { Success = false, ErrorMessage = $"LibreOffice conversion timed out after {ConversionTimeout.TotalSeconds:0} seconds." };
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Unexpected error while converting '{SourceFile}' to PDF.", sourceFilePath);
        return new OfficeConversionResult { Success = false, ErrorMessage = ex.Message };
      }
    }

    private static string ResolveSofficePath(IConfiguration configuration)
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

      return candidates.FirstOrDefault(File.Exists) ?? "soffice";
    }
  }
}
