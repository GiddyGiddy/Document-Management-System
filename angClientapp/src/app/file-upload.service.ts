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
}

@Injectable({
  providedIn: 'root',
})
export class FileUploadService {
  constructor(private httpClient: HttpClient) { }
  postFile(fileToUpload: File): Observable<boolean> {
    const endpoint = `${environment.apiURL}/fileupload/upload`;
    return from(fileToUpload.arrayBuffer()).pipe(
      switchMap((buffer: ArrayBuffer) => {
        const bytes = new Uint8Array(buffer);
        const binary = Array.from(bytes, (byte) => String.fromCharCode(byte)).join('');
        const payload: UploadFileRequest = {
          fileName: fileToUpload.name,
          contentBase64: btoa(binary),
        };
      console.log('Uploading file to endpoint:', endpoint, 'with payload:', payload);
        return this.httpClient.post(endpoint, payload);
      }),
      map(() => true),
      catchError((e) => this.handleError(e))
    );
  }

  private handleError(error: any): Observable<boolean> {
    console.error('File upload error:', error);
    return of(false);
  }
}
