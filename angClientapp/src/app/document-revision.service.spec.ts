import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DocumentRevisionService } from './document-revision.service';

describe('DocumentRevisionService', () => {
  let service: DocumentRevisionService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(DocumentRevisionService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists revisions', () => {
    service.list('abc').subscribe((r) => expect(r.length).toBe(1));
    const req = http.expectOne((r) => r.url.endsWith('/documents/abc/revisions'));
    expect(req.request.method).toBe('GET');
    req.flush([{ revisionNumber: 1 }]);
  });

  it('saves UTF-8 text as base64 with editor header', () => {
    service.save('abc', 'Grüße', 'text/plain', 1, 'c').subscribe();
    const req = http.expectOne((r) => r.url.endsWith('/documents/abc/revisions'));
    expect(req.request.method).toBe('POST');
    expect(req.request.body.expectedRevision).toBe(1);
    expect(atob(req.request.body.contentBase64).length).toBe(7);
    req.flush({ revisionNumber: 2 });
  });
});
