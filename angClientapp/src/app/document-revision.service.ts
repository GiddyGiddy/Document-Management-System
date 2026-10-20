import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { environment } from '../environments/environment.development';

export interface RevisionInfo {
  revisionNumber: number;
  baseRevisionNumber: number | null;
  contentType: string;
  sizeBytes: number;
  editedBy: string;
  comment: string | null;
  createdAtUtc: string;
}

export interface RevisionContent {
  text: string;
  contentType: string;
}

@Injectable({ providedIn: 'root' })
export class DocumentRevisionService {
  constructor(private readonly http: HttpClient) {}

  list(documentId: string): Observable<RevisionInfo[]> {
    return this.http.get<RevisionInfo[]>(this.url(documentId));
  }

  getContent(documentId: string, revision: number): Observable<RevisionContent> {
    return this.http
      .get(`${this.url(documentId)}/${revision}/content`, { observe: 'response', responseType: 'arraybuffer' })
      .pipe(
        map((response) => ({
          text: new TextDecoder('utf-8').decode(response.body ?? new ArrayBuffer(0)),
          contentType: response.headers.get('Content-Type') ?? 'application/octet-stream'
        }))
      );
  }

  save(documentId: string, text: string, contentType: string,   expectedRevision: number, comment: string): Observable<RevisionInfo> {
      const bytes = new TextEncoder().encode(text);
      let binary = '';
      bytes.forEach((b) => (binary += String.fromCharCode(b)));
      return this.http.post<RevisionInfo>(
        this.url(documentId),
        { contentBase64: btoa(binary), contentType, expectedRevision, comment: comment || null }
      );
    }

    restore(documentId: string, revision: number, expectedRevision: number): Observable<RevisionInfo> {
      return this.http.post<RevisionInfo>(
        `${this.url(documentId)}/${revision}/restore`,
        { expectedRevision }
      );
    }

  download(documentId: string, revision: number): Observable<Blob> {
    return this.http.get(this.contentUrl(documentId, revision), { responseType: 'blob' });
  }

  contentUrl(documentId: string, revision: number): string {
    return `${this.url(documentId)}/${revision}/content`;
  }

  private url(documentId: string): string {
    return `${environment.apiURL}/documents/${encodeURIComponent(documentId)}/revisions`;
  }
}
