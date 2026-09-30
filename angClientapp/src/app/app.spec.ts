import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { FileUploadService } from './file-upload.service';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
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
});
