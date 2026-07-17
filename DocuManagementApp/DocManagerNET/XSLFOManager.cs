using System;
using System.IO;
using System.Runtime.InteropServices;
using Fonet;
using System.Xml.Xsl;
using System.Xml;
using System.Text;
using System.Linq;



namespace DocManagerNET
{
    [ProgId("DocManagerNET.XSLFOManager")]
    [Guid("CCFC21E3-3A02-4b11-869D-2F780807AF8B")]
    [ClassInterface(ClassInterfaceType.AutoDual)]
    [ComVisible(true)]

    public class XSLFOManager
    {
        public XSLFOManager()
        {
        }

        ~XSLFOManager()
        {
        }

        public enum EnumZielformat { PDF, RTF, HTML };

        private String _xmlPfad;
        private string _xslPfad;
        private EnumZielformat _zielformat;
        private string _zielPfad;
        private string _errorMsg;
        private string _bildPfad;
        private bool _useEncodingDefault = false;
        private string _quellePfad;
        private string _pdfazielPfad;
        public String XMLPfad
        {
            get
            {
                return _xmlPfad;
            }
            set
            {
                _xmlPfad = value;
            }
        }

        public string XSLPfad
        {
            get
            {
                return _xslPfad;
            }
            set
            {
                _xslPfad = value;
            }
        }

        public string ZielPfad
        {
            get
            {
                return _zielPfad;
            }
            set
            {
                _zielPfad = value;
            }
        }

        public string BildPfad
        {
            get
            {
                return _bildPfad;
            }
            set
            {
                _bildPfad = value;
            }
        }

        public int Zielformat
        {
            get
            {
                return (int)_zielformat;
            }
            set
            {
                _zielformat = (EnumZielformat)value;
            }
        }

        /// <summary>
        /// Erstellt das Dokument im Zielformat im Zielpfad
        /// </summary>
        /// <returns>
        /// 0 - Funktion fehlerfrei ausgeführt
        /// 1 - Property XMLPfad beinhaltet kein Pfad
        /// 2 - Property XSLPfad beinhaltet kein Pfad
        /// 3 - XML Datei nicht vorhanden
        /// 4 - XSL Datei nicht vorhanden
        /// 5 - Fehler beim Erzeugen
        /// 6 - Property ZielPfad beinhaltet kein Pfad
        /// 7 - Property BildPfad beinhaltet kein Pfad
        /// </returns>
        public int DokumentGenerieren()
        {
            int iRet = 0;

            if (_xmlPfad.Length == 0)
                return 1;

            if (!File.Exists(_xmlPfad))
            {
                _errorMsg = _xmlPfad;
                return 3;
            }

            if (_xslPfad.Length == 0)
                return 2;

            if (!File.Exists(_xslPfad))
            {
                _errorMsg = _xslPfad;
                return 4;
            }


            string root = Path.GetPathRoot(_zielPfad);
            if (!Directory.Exists(root))
            {
                _errorMsg = _zielPfad;
                return 6;
            }

            if (!Directory.Exists(_bildPfad))
            {
                _errorMsg = _bildPfad;
                return 7;
            }

            try
            {
                string tempFoFile = Path.GetRandomFileName();

                XslCompiledTransform myXslTransform;
                myXslTransform = new XslCompiledTransform();
                myXslTransform.Load(_xslPfad);

                string text = "";
                if (_useEncodingDefault)
                {
                    text = File.ReadAllText(_xmlPfad, Encoding.Default);
                }
                else
                {
                    text = File.ReadAllText(_xmlPfad);
                }

                XmlDocument doc = new XmlDocument();
                try
                {
                    doc.LoadXml(text);
                }
                catch
                {
                    text = new string((from pC in text
                                       where (pC == 0x9 || pC == 0xa || pC == 0xd || (pC >= 0x20 && pC <= 0xd7ff) || (pC >= 0xe000 && pC <= 0xfffd))
                                       select pC).ToArray());
                    doc.LoadXml(text);
                }
                using (XmlWriter writer = XmlWriter.Create(tempFoFile))
                    myXslTransform.Transform(doc, writer);

                Fonet.FonetDriver driver = FonetDriver.Make();
                Fonet.Render.Pdf.PdfRendererOptions opt = new Fonet.Render.Pdf.PdfRendererOptions();
                opt.Author = "TURBOMED";
                driver.Options = opt;
                driver.BaseDirectory = new DirectoryInfo(_bildPfad);

                driver.Render(tempFoFile, _zielPfad);
                File.Delete(tempFoFile);
            }
            catch (Exception exp)
            {
                _errorMsg = exp.Message;
                _errorMsg += exp.InnerException;
                iRet = 5;
            }


            return iRet;
        }

        /// <summary>
        /// Gibt den Fehlertext der Exception zurück
        /// </summary>
        /// <remarks>Tritt in der Methode KokumentGenerieren ein Fehler auf der mit dem Returnwert 5 angegeben wird kann über diese Methode der Fehlertext der Exception abgefragt werden.</remarks>
        public string GetErrorMessage()
        {
            //throw new System.Exception(_errorMsg);
            return _errorMsg;
        }

        public int DokumentGenerierenMitDefaultEncoding()
        {
            _useEncodingDefault = true;
            return DokumentGenerieren();
        }
        public String QuellPfad
        {
            get
            {
                return _quellePfad;
            }
            set
            {
                _quellePfad = value;
            }
        }

        public string PdfaZielPfad
        {
            get
            {
                return _pdfazielPfad;
            }
            set
            {
                _pdfazielPfad = value;
            }
        }

        public bool writeSimplePDFDinA4(string foblocks, string pdfFileName)
        {

            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                     "<fo:root xmlns:fo=\"http://www.w3.org/1999/XSL/Format\">" +
                     "<fo:layout-master-set>" +
                     "<fo:simple-page-master master-name=\"simple\"" +
                     " page-height=\"29.7cm\"" +
                     " page-width=\"21cm\"" +
                     " margin-top=\"1cm\"" +
                     " margin-bottom=\"2cm\"" +
                     " margin-left=\"2.5cm\"" +
                     " margin-right=\"2.5cm\">" +
                     "<fo:region-body margin-top=\"3cm\"/>" +
                     "<fo:region-before extent=\"3cm\"/>" +
                     "<fo:region-after extent=\"1.5cm\"/>" +
                     "</fo:simple-page-master>" +
                     "</fo:layout-master-set>" +
                     "<fo:page-sequence master-reference=\"simple\">" +
                     " <fo:flow flow-name=\"xsl-region-body\">";

            xml += foblocks;

            xml += "</fo:flow>" +
                   "</fo:page-sequence>" +
                   "</fo:root>";

            return writeSimplePDF(xml, pdfFileName);


        }

        public bool writeSimplePDF(string fullfo, string pdfFileName)
        {

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(fullfo);
                Fonet.FonetDriver driver = FonetDriver.Make();
                driver.Render(doc, new FileStream(pdfFileName, FileMode.Create));
                return true;
            }
            catch (Exception exp)
            {
                _errorMsg = exp.Message;
                _errorMsg += exp.InnerException;
                return false;
            }


        }
    }
}