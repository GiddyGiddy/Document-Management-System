import { ChangeDetectorRef, Component, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink, RouterOutlet } from '@angular/router';
import { FileUpload } from './file-upload/file-upload';
import { AuthService } from './auth.service';
import { FileUploadService } from './file-upload.service';
import { AgenticSearchResponse, SearchApiService, SearchFilter, SearchMode, SearchResult } from './search-api.service';

@Component({
  selector: 'app-root',
  imports: [FileUpload, FormsModule, DecimalPipe, RouterLink, RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App {
  protected readonly title = signal('Document Management System');
  searchMode: SearchMode = 'deterministic';
  documentKind = '';
  entityName = '';
  dateFrom = '';
  dateTo = '';
  keyword = '';
  semanticQuery = '';
  question = '';
  searchResults: SearchResult[] = [];
  agenticAnswer = '';
  isSearching = false;
  hasSearched = false;
  searchError = '';

  constructor(
    private readonly searchApi: SearchApiService,
    private readonly fileUploadService: FileUploadService,
    protected readonly auth: AuthService,
    private readonly changeDetector: ChangeDetectorRef
  ) {}

  // A plain link navigates without the bearer token, so the original is fetched through HttpClient first.
  openOriginal(event: Event, result: SearchResult): void {
    event.preventDefault();
    const popup = window.open('', '_blank');
    this.fileUploadService.fetchFile(result.documentId).subscribe({
      next: (blob) => {
        const url = `${URL.createObjectURL(blob)}#page=${result.citation.pageNumber}`;
        if (popup) {
          popup.location.href = url;
        } else {
          window.open(url, '_blank');
        }
      },
      error: (error: unknown) => {
        popup?.close();
        this.searchError = this.getErrorMessage(error);
        this.changeDetector.markForCheck();
      }
    });
  }

  setSearchMode(mode: SearchMode): void {
    this.searchMode = mode;
    this.searchError = '';
    this.agenticAnswer = '';
    this.searchResults = [];
    this.hasSearched = false;
  }

  runSearch(): void {
    if (this.isSearching) {
      return;
    }

    this.searchError = '';
    this.agenticAnswer = '';
    this.searchResults = [];
    this.hasSearched = true;

    if (!this.auth.isSignedIn) {
      this.finishWithError('Bitte melden Sie sich an, um Dokumente zu durchsuchen.');
      return;
    }

    this.isSearching = true;

    if (this.searchMode === 'agentic') {
      const question = this.question.trim();
      if (!question) {
        this.finishWithError('Geben Sie eine Frage zu Ihren Dokumenten ein.');
        return;
      }

      this.searchApi.agentic(question).subscribe({
        next: (response: AgenticSearchResponse) => {
          this.agenticAnswer = response.answer;
          this.searchResults = response.sources ?? [];
          this.isSearching = false;
          this.changeDetector.markForCheck();
        },
        error: (error: unknown) => this.finishWithError(this.getErrorMessage(error))
      });
      return;
    }

    const filter = this.buildFilter();
    if (this.searchMode === 'hybrid') {
      if (!this.semanticQuery.trim()) {
        this.finishWithError('Geben Sie einen Suchbegriff für die semantische Suche ein.');
        return;
      }

      this.searchApi.hybrid(this.semanticQuery.trim(), filter).subscribe({
        next: (results) => this.finishWithResults(results),
        error: (error: unknown) => this.finishWithError(this.getErrorMessage(error))
      });
      return;
    }

    this.searchApi.deterministic(filter).subscribe({
      next: (results) => this.finishWithResults(results),
      error: (error: unknown) => this.finishWithError(this.getErrorMessage(error))
    });
  }

  private buildFilter(): SearchFilter {
    return {
      documentKind: this.documentKind || undefined,
      entityName: this.entityName.trim() || undefined,
      dateFrom: this.dateFrom || undefined,
      dateTo: this.dateTo || undefined,
      keyword: this.keyword.trim() || undefined
    };
  }

  private finishWithResults(results: SearchResult[]): void {
    this.searchResults = results;
    this.isSearching = false;
    this.changeDetector.markForCheck();
  }

  private finishWithError(message: string): void {
    this.searchError = message;
    this.isSearching = false;
    this.changeDetector.markForCheck();
  }

  private getErrorMessage(error: unknown): string {
    const response = error as { status?: number; error?: { message?: string } };
    if (response.status === 401) {
      return 'Ihre Sitzung ist abgelaufen oder ungültig. Bitte melden Sie sich erneut an.';
    }

    if (response.status === 403) {
      return 'Ihnen fehlt die Berechtigung (Rolle Viewer, Contributor, Reviewer oder Admin) für die Suche.';
    }

    if (response.status === 503) {
      return response.error?.message ?? 'Die Suche ist derzeit nicht verfügbar. Prüfen Sie Search Service und Modellkonfiguration.';
    }

    return response.error?.message ?? 'Die Suche konnte nicht ausgeführt werden. Bitte versuchen Sie es erneut.';
  }
}
