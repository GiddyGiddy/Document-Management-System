import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { FileUploadService } from './file-upload.service';
import { SearchApiService } from './search-api.service';
import { App } from './app';

describe('App', () => {
  let searchApiMock: {
    deterministic: ReturnType<typeof vi.fn>;
    hybrid: ReturnType<typeof vi.fn>;
    agentic: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    searchApiMock = {
      deterministic: vi.fn(() => of([])),
      hybrid: vi.fn(() => of([])),
      agentic: vi.fn(() => of({ answer: 'Found an agreement.', sources: [] })),
    };
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [{
        provide: FileUploadService,
        useValue: {
          getUploadedFiles: vi.fn(() => of([])),
          postFile: vi.fn(() => of(null)),
          archiveAsPdfA: vi.fn(() => of(false)),
          getFileUrl: vi.fn(() => ''),
        },
      }, {
        provide: SearchApiService,
        useValue: searchApiMock,
      }],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('should render title', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('h1')?.textContent).toContain('Weniger suchen. Mehr erledigen.');
  });

  it('shows search modes and sends an open question to agentic search', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    app.setSearchMode('agentic');
    app.question = 'Find vendor agreements signed last year with auto-renewal clauses';

    app.runSearch();

    expect(searchApiMock.agentic).toHaveBeenCalledWith(app.question);
    expect(app.agenticAnswer).toBe('Found an agreement.');
    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('#search')).toBeTruthy();
    expect(compiled.querySelector('.agent-answer')?.textContent).toContain('Found an agreement.');
  });
});
