import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { environment } from '../environments/environment.development';

import { FileUploadService } from './file-upload.service';

describe('FileUploadService', () => {
  let service: FileUploadService;
  let httpTestingController: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(FileUploadService);
    httpTestingController = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpTestingController.verify();
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  it('posts file metadata and base64 content to the upload endpoint', async () => {
    const file = new File(['ignored'], 'report.txt', { type: 'text/plain' });
    vi.spyOn(file, 'arrayBuffer').mockResolvedValue(new Uint8Array([65, 66, 67]).buffer);

    const responsePromise = firstValueFrom(service.postFile(file));
    let request!: ReturnType<HttpTestingController['expectOne']>;
    await vi.waitFor(() => {
      request = httpTestingController.expectOne(`${environment.apiURL}/fileupload/upload`);
    });

    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      fileName: 'report.txt',
      contentBase64: 'QUJD',
      contentType: 'text/plain',
    });

    request.flush({
      id: 'document-123',
      originalFileName: 'report.txt',
      storedFileName: 'document-123',
      size: 3,
    });

    await expect(responsePromise).resolves.toEqual({
      id: 'document-123',
      originalFileName: 'report.txt',
      storedFileName: 'document-123',
      size: 3,
    });
  });

  it('posts to the PDF/A conversion endpoint for the selected document', async () => {
    const responsePromise = firstValueFrom(service.archiveAsPdfA('document-123'));
    let request!: ReturnType<HttpTestingController['expectOne']>;
    await vi.waitFor(() => {
      request = httpTestingController.expectOne(
        `${environment.apiURL}/fileupload/document-123/convert-to-pdf?toPdfA=true`
      );
    });

    expect(request.request.method).toBe('POST');
    request.flush({ message: 'Document converted to PDF/A successfully.' });

    await expect(responsePromise).resolves.toBe(true);
  });

  it('posts to the PDF conversion endpoint when PDF/A is not requested', async () => {
    const responsePromise = firstValueFrom(service.convertDocument('document-456', false));
    let request!: ReturnType<HttpTestingController['expectOne']>;
    await vi.waitFor(() => {
      request = httpTestingController.expectOne(
        `${environment.apiURL}/fileupload/document-456/convert-to-pdf?toPdfA=false`
      );
    });

    expect(request.request.method).toBe('POST');
    request.flush({ message: 'Document converted to PDF successfully.' });

    await expect(responsePromise).resolves.toBe(true);
  });
});
