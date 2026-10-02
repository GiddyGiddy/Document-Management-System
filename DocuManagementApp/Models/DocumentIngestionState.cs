namespace DocuManagementApp.Models;

public enum DocumentProcessingStatus
{
  Completed = 0,
  Received = 1,
  Processing = 2,
  Failed = 3
}

public enum DocumentOutputFormat
{
  Original,
  Pdf,
  PdfA
}