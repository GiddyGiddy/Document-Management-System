import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { FileUploadService, UploadReceipt, UploadedFileInfo } from '../file-upload.service';

import { FileUpload } from './file-upload';

describe('FileUpload', () => {
  let component: FileUpload;
  let fixture: ComponentFixture<FileUpload>;
  let fileUploadService: {
    postFile: ReturnType<typeof vi.fn>;
    archiveAsPdfA: ReturnType<typeof vi.fn>;
    convertDocument: ReturnType<typeof vi.fn>;
    getUploadedFiles: ReturnType<typeof vi.fn>;
    getFileUrl: ReturnType<typeof vi.fn>;
  };

  const uploadReceipt: UploadReceipt = {
    id: 'document-123',
    originalFileName: 'report.docx',
    storedFileName: 'document-123',
    size: 12,
  };

  beforeEach(async () => {
    fileUploadService = {
      postFile: vi.fn(() => of(uploadReceipt)),
      archiveAsPdfA: vi.fn(() => of(true)),
      convertDocument: vi.fn(() => of(true)),
      getUploadedFiles: vi.fn(() => of([] as UploadedFileInfo[])),
      getFileUrl: vi.fn((storedFileName: string) => `/files/${storedFileName}`),
    };

    await TestBed.configureTestingModule({
      imports: [FileUpload],
      providers: [{ provide: FileUploadService, useValue: fileUploadService }],
    }).compileComponents();

    fixture = TestBed.createComponent(FileUpload);
    component = fixture.componentInstance;
    await fixture.whenStable();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('enables PDF/A archiving for supported office documents and PDFs', () => {
    component.handleFileInput(new File(['document'], 'report.DOCX'));

    expect(component.canArchiveAsPdfA).toBe(true);

    component.handleFileInput(new File(['pdf'], 'report.pdf', { type: 'application/pdf' }));

    expect(component.canArchiveAsPdfA).toBe(true);
  });

  it('disables PDF/A archiving for unsupported types and clears the selection', () => {
    component.archiveAsPdfA = true;

    component.handleFileInput(new File(['image'], 'image.png'));

    expect(component.canArchiveAsPdfA).toBe(false);
    expect(component.archiveAsPdfA).toBe(false);
  });

  it('rejects files larger than 5 MB', () => {
    const oversizedFile = {
      name: 'large.docx',
      size: component.maxFileSizeBytes + 1,
    } as File;

    component.handleFileInput(oversizedFile);

    expect(component.fileToUpload).toBeNull();
    expect(component.errorMessage).toContain('Maximum allowed size is 5 MB');
  });

  it('uploads normally without requesting PDF/A archiving when unchecked', () => {
    component.handleFileInput(new File(['document'], 'report.docx'));

    component.uploadFileToActivity();

    expect(fileUploadService.postFile).toHaveBeenCalledOnce();
    expect(fileUploadService.archiveAsPdfA).not.toHaveBeenCalled();
    expect(fileUploadService.getUploadedFiles).toHaveBeenCalledTimes(2);
    expect(component.successMessage).toBe('File uploaded successfully.');
  });

  it('uploads and archives the document when PDF/A is selected', () => {
    component.handleFileInput(new File(['document'], 'report.docx'));
    component.archiveAsPdfA = true;

    component.uploadFileToActivity();

    expect(fileUploadService.postFile).toHaveBeenCalledOnce();
    expect(fileUploadService.archiveAsPdfA).toHaveBeenCalledWith(uploadReceipt.id);
    expect(component.successMessage).toContain('PDF/A archive are available');
    expect(component.warningMessage).toBe('');
  });

  it('keeps upload success and warns when PDF/A archiving fails', () => {
    fileUploadService.archiveAsPdfA.mockReturnValue(of(false));
    component.handleFileInput(new File(['document'], 'report.docx'));
    component.archiveAsPdfA = true;

    component.uploadFileToActivity();

    expect(component.successMessage).toBe('File uploaded successfully.');
    expect(component.warningMessage).toContain('PDF/A archiving failed');
    expect(component.errorMessage).toBe('');
  });

  it('shows an upload error when the upload fails', () => {
    fileUploadService.postFile.mockReturnValue(of(null));
    component.handleFileInput(new File(['document'], 'report.docx'));

    component.uploadFileToActivity();

    expect(component.errorMessage).toBe('Upload failed. Please try again.');
    expect(fileUploadService.archiveAsPdfA).not.toHaveBeenCalled();
    expect(fileUploadService.getUploadedFiles).toHaveBeenCalledOnce();
  });

  it('converts an existing uploaded document and refreshes the list', () => {
    const uploadedDocument: UploadedFileInfo = {
      storedFileName: uploadReceipt.id,
      originalFileName: 'report.docx',
      size: 12,
      uploadedAt: '2026-10-01T12:00:00Z',
      processingStatus: 'Completed',
      requestedOutputFormat: 'Original',
    };

    component.convertUploadedDocument(uploadedDocument, false);

    expect(fileUploadService.convertDocument).toHaveBeenCalledWith(uploadReceipt.id, false);
    expect(fileUploadService.getUploadedFiles).toHaveBeenCalledTimes(2);
    expect(component.conversionMessage).toContain('converted to PDF');
  });

  it('labels a stored PDF/A rendition in the uploaded list', () => {
    const archivedDocument: UploadedFileInfo = {
      storedFileName: 'pdfa-123',
      originalFileName: 'report.pdfa.pdf',
      size: 42,
      uploadedAt: '2026-10-01T12:00:00Z',
      processingStatus: 'Completed',
      requestedOutputFormat: 'PdfA',
    };

    expect(component.getDocumentFormat(archivedDocument)).toBe('PDF/A');
  });

  it('offers PDF/A conversion but not redundant PDF conversion for an uploaded PDF', () => {
    const uploadedPdf: UploadedFileInfo = {
      storedFileName: 'pdf-123',
      originalFileName: 'report.pdf',
      size: 42,
      uploadedAt: '2026-10-01T12:00:00Z',
      processingStatus: 'Completed',
      requestedOutputFormat: 'Original',
    };

    expect(component.canConvertToPdf(uploadedPdf)).toBe(false);
    expect(component.canConvertToPdfA(uploadedPdf)).toBe(true);
  });
});
