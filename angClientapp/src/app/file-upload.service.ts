import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs/internal/Observable';
import { map, catchError } from 'rxjs/operators';
import { of, from } from 'rxjs';
import { switchMap } from 'rxjs/operators';
import { environment } from '../environments/environment.development';

interface UploadFileRequest {
  fileName: string;
  contentBase64: string;
  contentType: string;
}

export interface UploadedFileInfo {
  storedFileName: string;
  originalFileName: string;
  size: number;
  uploadedAt: string;
}

export interface UploadReceipt {
  id: string;
  originalFileName: string;
  storedFileName: string;
  size: number;
}

@Injectable({
  providedIn: 'root',
})
export class FileUploadService {
  constructor(private httpClient: HttpClient) { }

  postFile(fileToUpload: File): Observable<UploadReceipt | null> {
    const endpoint = `${environment.apiURL}/fileupload/upload`;
    return from(fileToUpload.arrayBuffer()).pipe(
      switchMap((buffer: ArrayBuffer) => {
        const bytes = new Uint8Array(buffer);
        const binary = Array.from(bytes, (byte) => String.fromCharCode(byte)).join('');
        const payload: UploadFileRequest = {
          fileName: fileToUpload.name,
          contentBase64: btoa(binary),
          contentType: fileToUpload.type || 'application/octet-stream',
        };
      console.log('Uploading file to endpoint:', endpoint, 'with payload:', payload);
        return this.httpClient.post<UploadReceipt>(endpoint, payload);
      }),
      catchError((error) => {
        console.error('File upload error:', error);
        return of(null);
      })
    );
  }

  archiveAsPdfA(documentId: string): Observable<boolean> {
    const endpoint = `${environment.apiURL}/fileupload/${encodeURIComponent(documentId)}/convert-to-pdf?toPdfA=true`;
    return this.httpClient.post(endpoint, {}).pipe(
      map(() => true),
      catchError((error) => {
        console.error('PDF/A archiving failed:', error);
        return of(false);
      })
    );
  }

  getUploadedFiles(): Observable<UploadedFileInfo[]> {
    const endpoint = `${environment.apiURL}/fileupload/files`;
    return this.httpClient.get<UploadedFileInfo[]>(endpoint).pipe(
      catchError((error) => {
        console.error('Failed to load uploaded files:', error);
        return of([]);
      })
    );
  }

  getFileUrl(storedFileName: string): string {
    return `${environment.apiURL}/fileupload/download/${encodeURIComponent(storedFileName)}`;
  }
}
