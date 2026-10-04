import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../environments/environment.development';
import { SearchApiService } from './search-api.service';

describe('SearchApiService', () => {
  let service: SearchApiService;
  let httpTestingController: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(SearchApiService);
    httpTestingController = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpTestingController.verify());

  it('sends deterministic filters through the API proxy', () => {
    const filter = { documentKind: 'Contract', dateFrom: '2025-01-01' };
    service.deterministic(filter).subscribe();

    const request = httpTestingController.expectOne(`${environment.apiURL}/search/deterministic`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body.filter).toEqual(filter);
    request.flush([]);
  });

  it('sends semantic query and filters through the hybrid proxy', () => {
    const filter = { entityName: 'Northwind', dateTo: '2025-12-31' };
    service.hybrid('auto-renewal clause', filter).subscribe();

    const request = httpTestingController.expectOne(`${environment.apiURL}/search/hybrid`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body.semanticQuery).toBe('auto-renewal clause');
    expect(request.request.body.filter).toEqual(filter);
    request.flush([]);
  });

  it('sends natural-language questions to the agentic proxy', () => {
    service.agentic('Find vendor agreements signed last year').subscribe();

    const request = httpTestingController.expectOne(`${environment.apiURL}/search/agentic`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body.question).toContain('vendor agreements');
    request.flush({ answer: 'Found agreements.', sources: [] });
  });
});