import {inject, Injectable} from '@angular/core';
import {HttpClient} from '@angular/common/http';
import {Observable, Subject} from 'rxjs';
import {environment} from 'src/environments/environment';
import {ConfirmUploadDto, MetadataSource, ReEnrichResultDto, UploadBookFileDto} from '../_models/upload/upload-book-file-dto';
import {MangaFormat} from '../_models/manga-format';
import {AccountService} from './account.service';

export interface UploadProgressEvent {
  /** Index of file currently uploading (0-based) */
  fileIndex: number;
  /** Total number of files */
  totalFiles: number;
  /** Upload progress of the current file (0-100) */
  fileProgress: number;
  /** Overall progress across all files (0-100) */
  overallProgress: number;
  /** Name of the file currently uploading */
  fileName: string;
  /** Set when ALL files are done */
  result?: UploadBookFileDto[];
  /** Files that failed to upload */
  errors?: string[];
  /** Detailed error info per failed file: "filename (reason)" */
  errorDetails?: string[];
}

@Injectable({
  providedIn: 'root'
})
export class UploadBookService {

  private readonly httpClient = inject(HttpClient);
  private readonly accountService = inject(AccountService);
  private readonly baseUrl = environment.apiUrl;

  /**
   * Upload files one at a time sequentially. Emits progress events per file.
   * Continues uploading remaining files even if one fails.
   */
  uploadBooksSequentially(files: File[]): Observable<UploadProgressEvent> {
    const subject = new Subject<UploadProgressEvent>();
    this.processFilesSequentially(files, subject);

    return subject.asObservable();
  }

  confirmUpload(dto: ConfirmUploadDto): Observable<void> {
    return this.httpClient.post<void>(this.baseUrl + 'upload/confirm-upload', dto);
  }

  reEnrich(searchTerm: string, format: MangaFormat, number?: string, isbn?: string, source?: MetadataSource): Observable<ReEnrichResultDto> {
    return this.httpClient.post<ReEnrichResultDto>(this.baseUrl + 'upload/re-enrich', {searchTerm, format, number, isbn, source});
  }

  private async processFilesSequentially(files: File[], subject: Subject<UploadProgressEvent>) {
    const allResults: UploadBookFileDto[] = [];
    const errors: string[] = [];
    const errorDetails: string[] = [];
    const totalFiles = files.length;

    for (let i = 0; i < totalFiles; i++) {
      const file = files[i];
      try {
        const result = await this.uploadSingleFile(file, i, totalFiles, subject);
        allResults.push(...result);
      } catch (err: any) {
        const reason = this.getErrorReason(err);
        errors.push(file.name);
        errorDetails.push(`${file.name} (${reason})`);
        // Emit progress so UI updates even on failure
        subject.next({
          fileIndex: i,
          totalFiles,
          fileProgress: 100,
          overallProgress: Math.round(((i + 1) / totalFiles) * 100),
          fileName: file.name,
        });
      }
    }

    // Emit final event with all results
    subject.next({
      fileIndex: totalFiles - 1,
      totalFiles,
      fileProgress: 100,
      overallProgress: 100,
      fileName: files[totalFiles - 1].name,
      result: allResults,
      errors: errors.length > 0 ? errors : undefined,
      errorDetails: errorDetails.length > 0 ? errorDetails : undefined,
    });
    subject.complete();
  }

  private static readonly UPLOAD_TIMEOUT_MS = 30 * 60 * 1000; // 30 minutes per file

  /**
   * Uses raw XMLHttpRequest instead of Angular HttpClient because the app is
   * configured with withFetch(), and the Fetch API does not support upload
   * progress events.
   */
  private uploadSingleFile(
    file: File,
    fileIndex: number,
    totalFiles: number,
    subject: Subject<UploadProgressEvent>
  ): Promise<UploadBookFileDto[]> {
    return new Promise((resolve, reject) => {
      const formData = new FormData();
      formData.append('files', file, file.name);

      const xhr = new XMLHttpRequest();
      xhr.open('POST', this.baseUrl + 'upload/upload-books');
      xhr.timeout = UploadBookService.UPLOAD_TIMEOUT_MS;

      const token = this.accountService.currentUserSignal()?.token;
      if (token) {
        xhr.setRequestHeader('Authorization', `Bearer ${token}`);
      }

      xhr.upload.addEventListener('progress', (event) => {
        const fileProgress = event.lengthComputable ? Math.round((100 * event.loaded) / event.total) : 0;
        const overallProgress = Math.round(((fileIndex + fileProgress / 100) / totalFiles) * 100);
        subject.next({
          fileIndex,
          totalFiles,
          fileProgress,
          overallProgress,
          fileName: file.name,
        });
      });

      xhr.addEventListener('load', () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          try {
            resolve(JSON.parse(xhr.responseText) ?? []);
          } catch {
            resolve([]);
          }
        } else {
          reject({status: xhr.status, statusText: xhr.statusText});
        }
      });

      xhr.addEventListener('error', () => reject({status: 0}));
      xhr.addEventListener('timeout', () => reject({name: 'TimeoutError'}));

      xhr.send(formData);
    });
  }

  private getErrorReason(err: any): string {
    if (err?.name === 'TimeoutError') {
      return 'timeout';
    }
    const status = err?.status;
    if (status === 413) {
      return 'file too large';
    }
    if (status === 0 || status === undefined) {
      return 'network error';
    }

    return `error ${status}`;
  }
}
