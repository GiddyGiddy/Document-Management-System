using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Xml;
using System.Xml.Xsl;
using Fonet;

namespace DocuManagementApp.Services
{
    [ProgId("DocManagerNET.PdfADocumentService")]
    [Guid("5B715A16-6F1D-43C0-9D6B-6F7C66E39A9A")]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    [ComVisible(true)]
    public class PdfADocumentService
    {
        public enum PdfAComplianceLevel
        {
            PdfA2B = 1
        }

        private string? _xmlPfad;
        private string? _xslPfad;
        private string? _bildPfad;
        private string? _pdfaZielPfad;
        private string? _quellePfad;
        private string? _errorMsg;
        private bool _useEncodingDefault;
        private PdfAComplianceLevel _pdfaComplianceLevel = PdfAComplianceLevel.PdfA2B;
        private bool _disablePdfEncryptionForCompliance = true;
        private bool _enforcePdfaConformanceOutput = true;
        private string? _pdfAuthor;
        private string? _pdfTitle;
        private string? _pdfSubject;
        private string? _pdfKeywords;
        private string? _lastValidationReport;
        private readonly IPdfAProcessingService _pdfaProcessingService;

        public PdfADocumentService(IPdfAProcessingService pdfaProcessingService)
        {
            _pdfaProcessingService = pdfaProcessingService;
        }

        public string? XMLPfad
        {
            get { return _xmlPfad; }
            set { _xmlPfad = value; }
        }

        public string? XSLPfad
        {
            get { return _xslPfad; }
            set { _xslPfad = value; }
        }

        public string? BildPfad
        {
            get { return _bildPfad; }
            set { _bildPfad = value; }
        }

        public string? PdfaZielPfad
        {
            get { return _pdfaZielPfad; }
            set { _pdfaZielPfad = value; }
        }

        public string? QuellPfad
        {
            get { return _quellePfad; }
            set { _quellePfad = value; }
        }

        public int PdfaCompliance
        {
            get { return (int)_pdfaComplianceLevel; }
            set { _pdfaComplianceLevel = (PdfAComplianceLevel)value; }
        }

        public bool DisablePdfEncryptionForCompliance
        {
            get { return _disablePdfEncryptionForCompliance; }
            set { _disablePdfEncryptionForCompliance = value; }
        }

        public bool EnforcePdfaConformanceOutput
        {
            get { return _enforcePdfaConformanceOutput; }
            set { _enforcePdfaConformanceOutput = value; }
        }

        public string PdfAuthor
        {
            get { return _pdfAuthor ?? string.Empty; }
            set { _pdfAuthor = value; }
        }

        public string PdfTitle
        {
            get { return _pdfTitle ?? string.Empty; }
            set { _pdfTitle = value; }
        }

        public string PdfSubject
        {
            get { return _pdfSubject ?? string.Empty; }
            set { _pdfSubject = value; }
        }

        public string PdfKeywords
        {
            get { return _pdfKeywords ?? string.Empty; }
            set { _pdfKeywords = value; }
        }

        public int ConfigurePdfaCompliance(int complianceLevel, bool disablePdfEncryption)
        {
            if (!Enum.IsDefined(typeof(PdfAComplianceLevel), complianceLevel))
            {
                _errorMsg = "Unsupported PDF/A compliance level. Only PDF/A-2b is supported by the configured converter.";
                return 1;
            }

            _pdfaComplianceLevel = (PdfAComplianceLevel)complianceLevel;
            _disablePdfEncryptionForCompliance = disablePdfEncryption;
            return 0;
        }

        public int DokumentAlsPdfaGenerieren()
        {
            return DokumentAlsPdfaGenerierenAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task<int> DokumentAlsPdfaGenerierenAsync(CancellationToken cancellationToken)
        {
            const int Ok = 0;
            const int XmlPfadLeer = 1;
            const int XslPfadLeer = 2;
            const int XmlNichtGefunden = 3;
            const int XslNichtGefunden = 4;
            const int FehlerBeimErzeugen = 5;
            const int PdfaZielPfadUngueltig = 6;
            const int BildPfadUngueltig = 7;
            const int UnsupportedPdfaCompliance = 8;
            const int PdfaConformanceFailed = 9;

            if (!IsPdfaComplianceSupported())
            {
                _errorMsg = "Unsupported PDF/A compliance level. Only PDF/A-2b is supported by the configured converter.";
                return UnsupportedPdfaCompliance;
            }

            if (string.IsNullOrWhiteSpace(_xmlPfad))
            {
                return XmlPfadLeer;
            }

            if (!File.Exists(_xmlPfad))
            {
                _errorMsg = _xmlPfad;
                return XmlNichtGefunden;
            }

            if (string.IsNullOrWhiteSpace(_xslPfad))
            {
                return XslPfadLeer;
            }

            if (!File.Exists(_xslPfad))
            {
                _errorMsg = _xslPfad;
                return XslNichtGefunden;
            }

            if (string.IsNullOrWhiteSpace(_pdfaZielPfad))
            {
                _errorMsg = _pdfaZielPfad;
                return PdfaZielPfadUngueltig;
            }

            string targetDirectory = Path.GetDirectoryName(_pdfaZielPfad) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
            {
                _errorMsg = _pdfaZielPfad;
                return PdfaZielPfadUngueltig;
            }

            if (string.IsNullOrWhiteSpace(_bildPfad) || !Directory.Exists(_bildPfad))
            {
                _errorMsg = _bildPfad;
                return BildPfadUngueltig;
            }

            string tempFoFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".fo");
            string tempPdfFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");

            try
            {
                XslCompiledTransform transform = new XslCompiledTransform();
                transform.Load(_xslPfad);

                string xmlText = _useEncodingDefault
                    ? File.ReadAllText(_xmlPfad, Encoding.Default)
                    : File.ReadAllText(_xmlPfad);

                XmlDocument xmlDocument = new XmlDocument();
                try
                {
                    xmlDocument.LoadXml(xmlText);
                }
                catch
                {
                    xmlText = new string((from c in xmlText
                                          where c == 0x9 || c == 0xa || c == 0xd ||
                                                (c >= 0x20 && c <= 0xd7ff) ||
                                                (c >= 0xe000 && c <= 0xfffd)
                                          select c).ToArray());
                    xmlDocument.LoadXml(xmlText);
                }

                using (XmlWriter writer = XmlWriter.Create(tempFoFile))
                {
                    transform.Transform(xmlDocument, writer);
                }

                FonetDriver driver = FonetDriver.Make();
                Fonet.Render.Pdf.PdfRendererOptions options = CreatePdfRendererOptions();
                driver.Options = options;
                driver.BaseDirectory = new DirectoryInfo(_bildPfad);
                driver.Render(tempFoFile, tempPdfFile);

                var result = await _pdfaProcessingService.ConvertToPdfAAsync(
                    tempPdfFile,
                    _pdfaZielPfad,
                    cancellationToken);
                _lastValidationReport = result.Report;

                if (!result.IsCompliant)
                {
                    _errorMsg = result.Report;
                    return PdfaConformanceFailed;
                }

                _errorMsg = null;
                return Ok;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _errorMsg = ex.Message;
                if (ex.InnerException != null)
                {
                    _errorMsg += ex.InnerException;
                }
                return FehlerBeimErzeugen;
            }
            finally
            {
                TryDeleteFile(tempFoFile);
                TryDeleteFile(tempPdfFile);
            }
        }

        public int PdfNachPdfaKonvertieren()
        {
            if (!IsPdfaComplianceSupported())
            {
                _errorMsg = "Unsupported PDF/A compliance level. Only PDF/A-2b is supported by the configured converter.";
                return 4;
            }

            if (string.IsNullOrWhiteSpace(_quellePfad) || !File.Exists(_quellePfad))
            {
                _errorMsg = "Source PDF path is missing or does not exist.";
                return 1;
            }

            if (string.IsNullOrWhiteSpace(_pdfaZielPfad))
            {
                _errorMsg = "Output PDF path is required.";
                return 2;
            }

            try
            {
                var result = ConvertPdfToPdfaAsync(_quellePfad, _pdfaZielPfad, CancellationToken.None)
                    .GetAwaiter().GetResult();
                return result.IsCompliant ? 0 : 5;
            }
            catch (Exception exception)
            {
                _errorMsg = exception.Message;
                return 3;
            }
        }

        public async Task<PdfAValidationResult> ConvertPdfToPdfaAsync(
            string sourcePdfPath,
            string outputPdfPath,
            CancellationToken cancellationToken)
        {
            var result = await _pdfaProcessingService.ConvertToPdfAAsync(sourcePdfPath, outputPdfPath, cancellationToken);
            _lastValidationReport = result.Report;
            _errorMsg = result.IsCompliant ? null : result.Report;
            return result;
        }

        public async Task<PdfAValidationResult> ValidatePdfaAsync(string pdfPath, CancellationToken cancellationToken)
        {
            var result = await _pdfaProcessingService.ValidateAsync(pdfPath, cancellationToken);
            _lastValidationReport = result.Report;
            _errorMsg = result.IsCompliant ? null : result.Report;
            return result;
        }

        public int DokumentAlsPdfaGenerierenMitDefaultEncoding()
        {
            _useEncodingDefault = true;
            return DokumentAlsPdfaGenerieren();
        }

        public async Task<int> DokumentAlsPdfaGenerierenMitDefaultEncodingAsync(CancellationToken cancellationToken)
        {
            _useEncodingDefault = true;
            return await DokumentAlsPdfaGenerierenAsync(cancellationToken);
        }

        public string GetErrorMessage()
        {
            return _errorMsg ?? string.Empty;
        }

        public string GetLastPdfaValidationReport()
        {
            return _lastValidationReport ?? string.Empty;
        }

        public string GetPdfaConformanceSuggestions(string pdfPath)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                return "PDF path is missing or invalid.";
            }

            var result = ValidatePdfaAsync(pdfPath, CancellationToken.None).GetAwaiter().GetResult();
            return result.Report;
        }

        public int ValidatePdfaConformance(string pdfPath)
        {
            const int Ok = 0;
            const int PfadUngueltig = 1;
            const int NichtKonform = 2;
            const int Fehler = 3;

            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                _errorMsg = "PDF path is missing or invalid.";
                return PfadUngueltig;
            }

            try
            {
                var result = ValidatePdfaAsync(pdfPath, CancellationToken.None).GetAwaiter().GetResult();
                return result.IsCompliant ? Ok : NichtKonform;
            }
            catch (Exception ex)
            {
                _errorMsg = ex.Message;
                if (ex.InnerException != null)
                {
                    _errorMsg += ex.InnerException;
                }
                return Fehler;
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
                // Intentionally ignored: temp file cleanup must not hide successful output.
            }
        }

        private bool IsPdfaComplianceSupported()
        {
            return _pdfaComplianceLevel == PdfAComplianceLevel.PdfA2B;
        }

        private Fonet.Render.Pdf.PdfRendererOptions CreatePdfRendererOptions()
        {
            Fonet.Render.Pdf.PdfRendererOptions options = new Fonet.Render.Pdf.PdfRendererOptions();
            options.Author = _pdfAuthor ?? string.Empty;
            options.Title = _pdfTitle ?? string.Empty;
            options.Subject = _pdfSubject ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(_pdfKeywords))
            {
                foreach (string keyword in _pdfKeywords.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmedKeyword = keyword.Trim();
                    if (trimmedKeyword.Length > 0)
                    {
                        options.AddKeyword(trimmedKeyword);
                    }
                }
            }

            if (_disablePdfEncryptionForCompliance)
            {
                options.OwnerPassword = string.Empty;
                options.UserPassword = string.Empty;
            }

            return options;
        }
    }
}
