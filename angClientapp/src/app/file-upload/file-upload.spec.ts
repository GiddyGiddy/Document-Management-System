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

  it('enables PDF/A archiving for supported office documents', () => {
    component.handleFileInput(new File(['document'], 'report.DOCX'));

    expect(component.canArchiveAsPdfA).toBe(true);
  });

  it('disables PDF/A archiving for unsupported types and clears the selection', () => {
    component.archiveAsPdfA = true;

    component.handleFileInput(new File(['pdf'], 'report.pdf'));

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
});
