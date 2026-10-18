import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { AuthService } from '../auth.service';
import { DocumentRevisionService, RevisionInfo } from '../document-revision.service';

@Component({
  selector: 'app-document-revisions',
  imports: [FormsModule, DatePipe],
  templateUrl: './document-revisions.html',
  styleUrl: './document-revisions.css'
})
export class DocumentRevisions implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly api = inject(DocumentRevisionService);
  protected readonly auth = inject(AuthService);

  readonly documentId = this.route.snapshot.paramMap.get('id') ?? '';
  readonly revisions = signal<RevisionInfo[]>([]);
  readonly currentRevision = computed(() => this.revisions().reduce((max, r) => Math.max(max, r.revisionNumber), 0));
  readonly currentInfo = computed(() => this.revisions().find((r) => r.revisionNumber === this.currentRevision()));
  readonly editable = computed(() => this.isText(this.currentInfo()?.contentType));
  readonly message = signal('');
  readonly error = signal('');
  readonly busy = signal(false);

  text = '';
  comment = '';
  viewing: number | null = null;
  private loadedRevision = 0;

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.error.set('');
    this.api.list(this.documentId).subscribe({
      next: (list) => {
        this.revisions.set(list);
        this.loadCurrent();
      },
      error: (e) => this.error.set(e.status === 404 ? 'Dokument nicht gefunden.' : 'Verlauf konnte nicht geladen werden.')
    });
  }

  view(revision: number): void {
    this.api.getContent(this.documentId, revision).subscribe({
      next: (c) => {
        this.text = c.text;
        this.viewing = revision;
      },
      error: () => this.error.set('Revision konnte nicht geladen werden.')
    });
  }

  save(): void {
    if (this.busy() || !this.editable()) {
      return;
    }
    this.busy.set(true);
    this.error.set('');
    this.api
      .save(this.documentId, this.text, this.currentInfo()?.contentType ?? 'text/plain', this.loadedRevision, this.comment)
      .subscribe({
        next: (r) => this.done(`Revision ${r.revisionNumber} gespeichert.`),
        error: (e) => this.fail(e)
      });
  }

  restore(revision: number): void {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set('');
    this.api.restore(this.documentId, revision, this.currentRevision()).subscribe({
      next: (r) => this.done(`Revision ${revision} als Revision ${r.revisionNumber} wiederhergestellt.`),
      error: (e) => this.fail(e)
    });
  }

  downloadUrl(revision: number): string {
    return this.api.contentUrl(this.documentId, revision);
  }

  private loadCurrent(): void {
    const current = this.currentRevision();
    this.loadedRevision = current;
    if (current > 0 && this.editable()) {
      this.view(current);
    } else {
      this.text = '';
      this.viewing = null;
    }
  }

  private done(message: string): void {
    this.busy.set(false);
    this.comment = '';
    this.message.set(message);
    this.reload();
  }

  private fail(e: { status?: number }): void {
    this.busy.set(false);
    this.message.set('');
    if (e.status === 409) {
      this.error.set('Das Dokument wurde zwischenzeitlich geändert. Bitte neu laden.');
    } else if (e.status === 400) {
      this.error.set('Ungültiger Inhalt.');
    } else if (e.status === 404) {
      this.error.set('Dokument nicht gefunden.');
    } else {
      this.error.set('Speichern fehlgeschlagen.');
    }
  }

  private isText(contentType?: string): boolean {
    const t = (contentType ?? '').toLowerCase();
    return t.startsWith('text/') || t.includes('json') || t.includes('xml');
  }
}
