import {inject, Injectable} from '@angular/core';
import {HttpClient, HttpEventType} from '@angular/common/http';
import {Observable, Subject} from 'rxjs';
import {environment} from 'src/environments/environment';
import {ConfirmUploadDto, UploadBookFileDto} from '../_models/upload/upload-book-file-dto';

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
}

@Injectable({
  providedIn: 'root'
})
export class UploadBookService {

  private readonly httpClient = inject(HttpClient);
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

  private async processFilesSequentially(files: File[], subject: Subject<UploadProgressEvent>) {
    const allResults: UploadBookFileDto[] = [];
    const errors: string[] = [];
    const totalFiles = files.length;

    for (let i = 0; i < totalFiles; i++) {
      const file = files[i];
      try {
        const result = await this.uploadSingleFile(file, i, totalFiles, subject);
        allResults.push(...result);
      } catch {
        errors.push(file.name);
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
    });
    subject.complete();
  }

  private uploadSingleFile(
    file: File,
    fileIndex: number,
    totalFiles: number,
    subject: Subject<UploadProgressEvent>
  ): Promise<UploadBookFileDto[]> {
    return new Promise((resolve, reject) => {
      const formData = new FormData();
      formData.append('files', file, file.name);

      this.httpClient.post<UploadBookFileDto[]>(this.baseUrl + 'upload/upload-books', formData, {
        reportProgress: true,
        observe: 'events'
      }).subscribe({
        next: event => {
          if (event.type === HttpEventType.UploadProgress) {
            const fileProgress = event.total ? Math.round((100 * event.loaded) / event.total) : 0;
            const overallProgress = Math.round(((fileIndex + fileProgress / 100) / totalFiles) * 100);
            subject.next({
              fileIndex,
              totalFiles,
              fileProgress,
              overallProgress,
              fileName: file.name,
            });
          }
          if (event.type === HttpEventType.Response) {
            resolve(event.body ?? []);
          }
        },
        error: err => reject(err)
      });
    });
  }
}
