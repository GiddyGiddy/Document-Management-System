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
            PdfA1B = 1
        }

        private string? _xmlPfad;
        private string? _xslPfad;
        private string? _bildPfad;
        private string? _pdfaZielPfad;
        private string? _quellePfad;
        private string? _errorMsg;
        private bool _useEncodingDefault;
        private PdfAComplianceLevel _pdfaComplianceLevel = PdfAComplianceLevel.PdfA1B;
        private bool _disablePdfEncryptionForCompliance = true;
        private bool _enforcePdfaConformanceOutput = true;
        private string? _pdfAuthor;
        private string? _pdfTitle;
        private string? _pdfSubject;
        private string? _pdfKeywords;
        private string? _lastValidationReport;

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
                _errorMsg = "Unsupported PDF/A compliance level. Only PDF/A-1b is supported by the current converter.";
                return 1;
            }

            _pdfaComplianceLevel = (PdfAComplianceLevel)complianceLevel;
            _disablePdfEncryptionForCompliance = disablePdfEncryption;
            return 0;
        }

        public int DokumentAlsPdfaGenerieren()
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
                _errorMsg = "Unsupported PDF/A compliance level. Only PDF/A-1b is supported by the current converter.";
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

                PdfAValidationReport report = BuildPdfaValidationReport(tempPdfFile);
                _lastValidationReport = report.ToText();

                if (!report.IsConformant)
                {
                    if (_enforcePdfaConformanceOutput)
                    {
                        _errorMsg = report.Summary;
                        return PdfaConformanceFailed;
                    }

                    _errorMsg = "PDF/A validation warning: " + report.Summary;
                }
                else
                {
                    _errorMsg = null;
                }

                File.Copy(tempPdfFile, _pdfaZielPfad, true);
                return Ok;
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
            const int Ok = 0;
            const int QuelleLeerOderNichtGefunden = 1;
            const int ZielPfadUngueltig = 2;
            const int KonvertierungFehlgeschlagen = 3;
            const int UnsupportedPdfaCompliance = 4;
            const int PdfaConformanceFailed = 5;

            if (!IsPdfaComplianceSupported())
            {
                _errorMsg = "Unsupported PDF/A compliance level. Only PDF/A-1b is supported by the current converter.";
                return UnsupportedPdfaCompliance;
            }

            if (string.IsNullOrWhiteSpace(_quellePfad) || !File.Exists(_quellePfad))
            {
                _errorMsg = _quellePfad;
                return QuelleLeerOderNichtGefunden;
            }

            if (string.IsNullOrWhiteSpace(_pdfaZielPfad))
            {
                _errorMsg = _pdfaZielPfad;
                return ZielPfadUngueltig;
            }

            string? targetDirectory = Path.GetDirectoryName(_pdfaZielPfad);
            if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
            {
                _errorMsg = _pdfaZielPfad;
                return ZielPfadUngueltig;
            }

            try
            {
                PdfAValidationReport report = BuildPdfaValidationReport(_quellePfad);
                _lastValidationReport = report.ToText();

                if (!report.IsConformant)
                {
                    if (_enforcePdfaConformanceOutput)
                    {
                        _errorMsg = report.Summary;
                        return PdfaConformanceFailed;
                    }

                    _errorMsg = "PDF/A validation warning: " + report.Summary;
                }
                else
                {
                    _errorMsg = null;
                }

                File.Copy(_quellePfad, _pdfaZielPfad, true);
                return Ok;
            }
            catch (Exception ex)
            {
                _errorMsg = ex.Message;
                if (ex.InnerException != null)
                {
                    _errorMsg += ex.InnerException;
                }
                return KonvertierungFehlgeschlagen;
            }
        }

        public int DokumentAlsPdfaGenerierenMitDefaultEncoding()
        {
            _useEncodingDefault = true;
            return DokumentAlsPdfaGenerieren();
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

            PdfAValidationReport report = BuildPdfaValidationReport(pdfPath);
            return report.Suggestions.Count == 0
                ? "No remediation suggestions. The document passed internal PDF/A checks."
                : string.Join(Environment.NewLine, report.Suggestions.ToArray());
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
                PdfAValidationReport report = BuildPdfaValidationReport(pdfPath);
                _lastValidationReport = report.ToText();

                if (!report.IsConformant)
                {
                    _errorMsg = report.Summary;
                    return NichtKonform;
                }

                _errorMsg = null;
                return Ok;
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
            return _pdfaComplianceLevel == PdfAComplianceLevel.PdfA1B;
        }

        private PdfAValidationReport BuildPdfaValidationReport(string pdfPath)
        {
            byte[] bytes = File.ReadAllBytes(pdfPath);
            string content = Encoding.ASCII.GetString(bytes);
            string lowerContent = content.ToLowerInvariant();

            PdfAValidationReport report = new PdfAValidationReport();

            if (!content.StartsWith("%PDF-", StringComparison.Ordinal))
            {
                report.AddIssue(
                    "Missing PDF header.",
                    "Regenerate the file with a compliant PDF writer and ensure the file starts with '%PDF-'.");
            }

            // PDF/A-1 is based on PDF 1.4. Higher versions are suspicious for strict A-1 workflows.
            string header = content.Length >= 8 ? content.Substring(0, 8) : content;
            if (header.StartsWith("%PDF-", StringComparison.Ordinal) && header.Length >= 8)
            {
                string version = header.Substring(5, 3);
                if (string.CompareOrdinal(version, "1.4") > 0)
                {
                    report.AddIssue(
                        "PDF version appears higher than 1.4 for a PDF/A-1b target.",
                        "Export using PDF/A-1b settings or force PDF version 1.4 during creation.");
                }
            }

            if (lowerContent.Contains("/encrypt"))
            {
                report.AddIssue(
                    "Encrypted PDF is not PDF/A compliant.",
                    "Disable user/owner passwords and all encryption before archiving as PDF/A.");
            }

            if (!lowerContent.Contains("pdfaid:part") || !lowerContent.Contains("pdfaid:conformance"))
            {
                report.AddIssue(
                    "Missing PDF/A XMP identification metadata.",
                    "Embed XMP with pdfaid:part and pdfaid:conformance (for example, part='1' conformance='B').");
            }

            if (!lowerContent.Contains("/metadata"))
            {
                report.AddIssue(
                    "Missing Metadata entry in document catalog.",
                    "Include an XMP metadata stream and reference it from the catalog with /Metadata.");
            }

            if (!lowerContent.Contains("/outputintent") && !lowerContent.Contains("/outputintents"))
            {
                report.AddIssue(
                    "Missing OutputIntent entry.",
                    "Embed an output intent with an ICC profile (typically sRGB IEC61966-2.1)." );
            }

            if (!lowerContent.Contains("/iccprofile"))
            {
                report.AddIssue(
                    "Missing ICC profile.",
                    "Embed an ICC profile and wire it through /OutputIntent (/DestOutputProfile)." );
            }

            bool hasEmbeddedFontMarker =
                lowerContent.Contains("/fontfile") ||
                lowerContent.Contains("/fontfile2") ||
                lowerContent.Contains("/fontfile3");
            if (!hasEmbeddedFontMarker)
            {
                report.AddIssue(
                    "No embedded font markers detected.",
                    "Embed all fonts (or approved subsets) during PDF generation.");
            }

            if (lowerContent.Contains("/javascript") || lowerContent.Contains("/js"))
            {
                report.AddIssue(
                    "JavaScript actions detected.",
                    "Remove all JavaScript from the PDF. PDF/A disallows active scripting.");
            }

            if (lowerContent.Contains("/launch") || lowerContent.Contains("/richmedia") || lowerContent.Contains("/movie"))
            {
                report.AddIssue(
                    "Interactive or multimedia actions detected.",
                    "Remove launch, rich-media, movie, and similar interactive features.");
            }

            if (lowerContent.Contains("/embeddedfile") || lowerContent.Contains("/filespec"))
            {
                report.AddIssue(
                    "Embedded files or file specifications detected.",
                    "Remove attachments for PDF/A-1b output or target a PDF/A part that allows them.");
            }

            if (lowerContent.Contains("/devicergb") && !lowerContent.Contains("/outputintent"))
            {
                report.AddIssue(
                    "DeviceRGB used without output intent.",
                    "Map colors through an ICC-based profile and include /OutputIntent.");
            }

            report.FinalizeSummary();
            return report;
        }

        private sealed class PdfAValidationReport
        {
            public readonly List<string> Issues = new List<string>();
            public readonly List<string> Suggestions = new List<string>();
            public bool IsConformant { get; private set; }
            public string? Summary { get; private set; }

            public void AddIssue(string issue, string suggestion)
            {
                Issues.Add(issue);
                Suggestions.Add("- " + suggestion);
            }

            public void FinalizeSummary()
            {
                if (Issues.Count == 0)
                {
                    IsConformant = true;
                    Summary = "PDF passed internal PDF/A checks.";
                    return;
                }

                IsConformant = false;
                Summary = "PDF/A checks failed: " + string.Join(" ", Issues.ToArray());
            }

            public string ToText()
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(IsConformant ? "Conformance: PASS" : "Conformance: FAIL");
                sb.AppendLine("Summary: " + Summary);

                if (Issues.Count > 0)
                {
                    sb.AppendLine("Issues:");
                    for (int i = 0; i < Issues.Count; i++)
                    {
                        sb.AppendLine("- " + Issues[i]);
                    }
                }

                if (Suggestions.Count > 0)
                {
                    sb.AppendLine("Suggestions:");
                    for (int i = 0; i < Suggestions.Count; i++)
                    {
                        sb.AppendLine(Suggestions[i]);
                    }
                }

                return sb.ToString().TrimEnd();
            }
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
