import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../environments/environment.development';

export type SearchMode = 'deterministic' | 'hybrid' | 'agentic';

export interface SearchFilter {
  documentKind?: string;
  entityName?: string;
  dateFrom?: string;
  dateTo?: string;
  keyword?: string;
}

export interface SearchCitation {
  documentId: string;
  sourceUrl: string;
  pageNumber: number;
  boundingBox: number[] | null;
}

export interface SearchResult {
  documentId: string;
  documentKind: string;
  entityName: string;
  documentDate: string | null;
  text: string;
  score: number | null;
  citation: SearchCitation;
}

export interface AgenticSearchResponse {
  answer: string;
  sources: SearchResult[];
}

@Injectable({ providedIn: 'root' })
export class SearchApiService {
  private readonly endpoint = `${environment.apiURL}/search`;

  constructor(private readonly http: HttpClient) {}

  deterministic(filter: SearchFilter, limit = 50): Observable<SearchResult[]> {
    return this.http.post<SearchResult[]>(`${this.endpoint}/deterministic`, { filter, limit });
  }

  hybrid(semanticQuery: string, filter: SearchFilter, limit = 10): Observable<SearchResult[]> {
    return this.http.post<SearchResult[]>(`${this.endpoint}/hybrid`, { semanticQuery, filter, limit });
  }

  agentic(question: string): Observable<AgenticSearchResponse> {
    return this.http.post<AgenticSearchResponse>(`${this.endpoint}/agentic`, { question });
  }
}
