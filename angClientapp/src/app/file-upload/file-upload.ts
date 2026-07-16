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
  readonly maxFileSizeBytes = 5 * 1024 * 1024;
  isUploading = false;
  isLoadingFiles = false;
  errorMessage = '';
  successMessage = '';

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

     console.log('Uploading file to:', file);
    if (!file) {
      this.fileToUpload = null;
      console.log('No file selected.');
      return;
    }

    if (file.size > this.maxFileSizeBytes) {
      this.fileToUpload = null;
      this.errorMessage = 'File is too large. Maximum allowed size is 5 MB.';
      console.log('File is too large:', file.size, 'bytes');
      return;
    }

    this.fileToUpload = file;
  }

  clearSelection(input: HTMLInputElement): void {
    this.fileToUpload = null;
    this.errorMessage = '';
    this.successMessage = '';
    input.value = '';
  }

  uploadFileToActivity(): void {
    if (!this.fileToUpload || this.isUploading) {
      return;
    }
   console.log('Uploading file to:', this.fileToUpload);
    this.isUploading = true;
    this.successMessage = '';
    this.errorMessage = '';
   console.log('Uploading file to:2', this.fileToUpload);
    this.fileUploadService.postFile(this.fileToUpload).subscribe({
      next: (ok: boolean) => {
        this.isUploading = false;

        if (ok) {
          this.successMessage = 'File uploaded successfully.';
          if (this.fileInputRef?.nativeElement) {
            this.clearSelection(this.fileInputRef.nativeElement);
            this.successMessage = 'File uploaded successfully.';
          }
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
        console.log('File upload error:', error);
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
}
