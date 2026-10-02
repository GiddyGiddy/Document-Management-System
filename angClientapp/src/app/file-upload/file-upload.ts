import { CommonModule } from '@angular/common';
import { ChangeDetectorRef, Component, ElementRef, OnInit, ViewChild } from '@angular/core';
import { FileUploadService, UploadedFileInfo } from '../file-upload.service';

interface FileUploadError {
  // Define the error structure
  [key: string]: unknown;
}

@Component({
  selector: 'app-file-upload',
  imports: [CommonModule],
  templateUrl: './file-upload.html',
  styleUrl: './file-upload.css',
})
export class FileUpload implements OnInit {
  @ViewChild('fileInputRef') fileInputRef?: ElementRef<HTMLInputElement>;

  fileToUpload: File | null = null;
  uploadedFiles: UploadedFileInfo[] = [];
  archiveAsPdfA = false;
  readonly maxFileSizeBytes = 5 * 1024 * 1024;
  private readonly officeConversionExtensions = ['doc', 'docx', 'rtf', 'odt', 'xls', 'xlsx', 'ods'];
  private readonly pdfaSupportedExtensions = [...this.officeConversionExtensions, 'pdf'];
  isUploading = false;
  isLoadingFiles = false;
  errorMessage = '';
  successMessage = '';
  warningMessage = '';
  conversionInProgressId: string | null = null;
  conversionMessage = '';
  conversionErrorMessage = '';

  constructor(
    private fileUploadService: FileUploadService,
    private cdr: ChangeDetectorRef
  ) { }

  ngOnInit(): void {
    this.loadUploadedFiles();
  }

  get selectedFileName(): string {
    return this.fileToUpload?.name ?? 'No file selected';
  }

  get canArchiveAsPdfA(): boolean {
    const extension = this.fileToUpload?.name.toLowerCase().split('.').pop() ?? '';
    return this.pdfaSupportedExtensions.includes(extension);
  }

  canConvertToPdf(file: UploadedFileInfo): boolean {
    const extension = file.originalFileName.toLowerCase().split('.').pop() ?? '';
    return this.officeConversionExtensions.includes(extension);
  }

  canConvertToPdfA(file: UploadedFileInfo): boolean {
    const extension = file.originalFileName.toLowerCase().split('.').pop() ?? '';
    return this.pdfaSupportedExtensions.includes(extension);
  }

  getDocumentFormat(file: UploadedFileInfo): string {
    const fileName = file.originalFileName.toLowerCase();
    if (fileName.endsWith('.pdfa.pdf')) {
      return 'PDF/A';
    }
    if (fileName.endsWith('.pdf')) {
      return 'PDF';
    }
    return fileName.split('.').pop()?.toUpperCase() ?? 'FILE';
  }

  convertUploadedDocument(file: UploadedFileInfo, toPdfA: boolean): void {
    if (this.conversionInProgressId || !(toPdfA ? this.canConvertToPdfA(file) : this.canConvertToPdf(file))) {
      return;
    }

    this.conversionInProgressId = file.storedFileName;
    this.conversionMessage = '';
    this.conversionErrorMessage = '';
    this.fileUploadService.convertDocument(file.storedFileName, toPdfA).subscribe({
      next: (converted) => {
        this.conversionInProgressId = null;
        if (converted) {
          this.conversionMessage = toPdfA
            ? `${file.originalFileName} archived as PDF/A.`
            : `${file.originalFileName} converted to PDF.`;
          this.loadUploadedFiles();
        } else {
          this.conversionErrorMessage = 'Conversion failed. The original document remains available.';
        }
        this.cdr.detectChanges();
      },
      error: (error: FileUploadError) => {
        this.conversionInProgressId = null;
        this.conversionErrorMessage = 'Conversion failed. The original document remains available.';
        console.error('Document conversion error:', error);
        this.cdr.detectChanges();
      },
    });
  }

  onFileSelected(event: Event): void {
     console.log('File selected event:', event);
    const input = event.target as HTMLInputElement | null;
    const file = input?.files?.item(0) ?? null;
    console.log('Selected file:', file);
    this.handleFileInput(file);
  }

  handleFileInput(file: File | null): void {
    this.successMessage = '';
    this.errorMessage = '';
    this.warningMessage = '';

     console.log('Uploading file to:', file);
    if (!file) {
      this.fileToUpload = null;
      this.archiveAsPdfA = false;
      console.log('No file selected.');
      return;
    }

    if (file.size > this.maxFileSizeBytes) {
      this.fileToUpload = null;
      this.archiveAsPdfA = false;
      this.errorMessage = 'File is too large. Maximum allowed size is 5 MB.';
      console.log('File is too large:', file.size, 'bytes');
      return;
    }

    this.fileToUpload = file;
    if (!this.canArchiveAsPdfA) {
      this.archiveAsPdfA = false;
    }
  }

  setArchiveAsPdfA(event: Event): void {
    this.archiveAsPdfA = (event.target as HTMLInputElement).checked;
  }

  clearSelection(input: HTMLInputElement): void {
    this.fileToUpload = null;
    this.archiveAsPdfA = false;
    this.errorMessage = '';
    this.successMessage = '';
    this.warningMessage = '';
    input.value = '';
  }

  uploadFileToActivity(): void {
    if (!this.fileToUpload || this.isUploading) {
      return;
    }

    const shouldArchiveAsPdfA = this.archiveAsPdfA;
    this.isUploading = true;
    this.successMessage = '';
    this.errorMessage = '';
    this.warningMessage = '';

    this.fileUploadService.postFile(this.fileToUpload, shouldArchiveAsPdfA).subscribe({
      next: (upload) => {
        this.isUploading = false;

        if (upload) {
          const archived = upload.requestedOutputFormat === 'PdfA';
          const successMessage = archived
            ? 'Upload complete. The original and PDF/A archive are available.'
            : 'File uploaded successfully.';
          if (this.fileInputRef?.nativeElement) {
            this.clearSelection(this.fileInputRef.nativeElement);
          }
          this.successMessage = successMessage;
          this.warningMessage = '';
          this.loadUploadedFiles();
          this.cdr.detectChanges();
          return;
        }

        this.errorMessage = 'Upload failed. Please try again.';
        this.cdr.detectChanges();
      },
      error: (error: FileUploadError) => {
        this.isUploading = false;
        this.errorMessage = 'Upload failed. Please try again.';
        console.error('File upload error:', error);
        this.cdr.detectChanges();
      },
    });
  }

  loadUploadedFiles(): void {
    this.isLoadingFiles = true;
    this.fileUploadService.getUploadedFiles().subscribe({
      next: (files) => {
        this.uploadedFiles = files;
        this.isLoadingFiles = false;
        this.cdr.detectChanges();
      },
      error: () => {
        this.isLoadingFiles = false;
        this.cdr.detectChanges();
      },
    });
  }

  formatSize(sizeInBytes: number): string {
    if (sizeInBytes < 1024) {
      return `${sizeInBytes} B`;
    }

    if (sizeInBytes < 1024 * 1024) {
      return `${(sizeInBytes / 1024).toFixed(1)} KB`;
    }

    return `${(sizeInBytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  // Drives which inline preview markup the template renders for a given file.
  getFileKind(file: UploadedFileInfo): 'pdf' | 'image' | 'video' | 'audio' | 'other' {
    const extension = file.originalFileName.toLowerCase().split('.').pop() ?? '';

    if (extension === 'pdf') {
      return 'pdf';
    }
    if (['png', 'jpg', 'jpeg', 'gif', 'bmp'].includes(extension)) {
      return 'image';
    }
    if (['mp4', 'mkv'].includes(extension)) {
      return 'video';
    }
    if (extension === 'mp3') {
      return 'audio';
    }
    return 'other';
  }

  getFileUrl(file: UploadedFileInfo): string {
    return this.fileUploadService.getFileUrl(file.storedFileName);
  }
}
