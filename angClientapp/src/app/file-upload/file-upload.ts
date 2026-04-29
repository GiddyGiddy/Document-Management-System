import { ChangeDetectorRef, Component, ElementRef, ViewChild } from '@angular/core';
import { FileUploadService } from '../file-upload.service';

interface FileUploadResponse {
  // Define the response structure from the server
  [key: string]: unknown;
}

interface FileUploadError {
  // Define the error structure
  [key: string]: unknown;
}

@Component({
  selector: 'app-file-upload',
  imports: [],
  templateUrl: './file-upload.html',
  styleUrl: './file-upload.css',
})
export class FileUpload {
  @ViewChild('fileInputRef') fileInputRef?: ElementRef<HTMLInputElement>;

  fileToUpload: File | null = null;
  readonly maxFileSizeBytes = 5 * 1024 * 1024;
  isUploading = false;
  errorMessage = '';
  successMessage = '';

  constructor(
    private fileUploadService: FileUploadService,
    private cdr: ChangeDetectorRef
  ) { }

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
}
