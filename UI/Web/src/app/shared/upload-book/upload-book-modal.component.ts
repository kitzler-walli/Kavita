import {ChangeDetectionStrategy, ChangeDetectorRef, Component, inject, OnInit} from '@angular/core';
import {NgbActiveModal} from '@ng-bootstrap/ng-bootstrap';
import {FormControl, FormGroup, FormsModule, ReactiveFormsModule, Validators} from '@angular/forms';
import {ToastrService} from 'ngx-toastr';
import {translate, TranslocoDirective} from '@jsverse/transloco';
import {
  NgxFileDropEntry, NgxFileDropModule,
  FileSystemFileEntry, FileSystemDirectoryEntry, FileSystemEntry
} from 'ngx-file-drop';
import {UploadBookService} from '../../_services/upload-book.service';
import {LibraryService} from '../../_services/library.service';
import {Library} from '../../_models/library/library';
import {ConfirmUploadFileDto, MetadataSource, ReEnrichResultDto, UploadBookFileDto} from '../../_models/upload/upload-book-file-dto';
import {MangaFormat} from '../../_models/manga-format';

enum UploadStep {
  Select,
  Uploading,
  Review,
  Confirming
}

@Component({
  selector: 'app-upload-book-modal',
  imports: [
    TranslocoDirective,
    FormsModule,
    ReactiveFormsModule,
    NgxFileDropModule,
  ],
  templateUrl: './upload-book-modal.component.html',
  styleUrl: './upload-book-modal.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class UploadBookModalComponent implements OnInit {
  protected readonly UploadStep = UploadStep;

  private readonly modalRef = inject(NgbActiveModal);
  private readonly uploadService = inject(UploadBookService);
  private readonly libraryService = inject(LibraryService);
  private readonly toastr = inject(ToastrService);
  private readonly cdRef = inject(ChangeDetectorRef);

  acceptableExtensions = '.cbz,.cbr,.zip,.rar,.epub,.pdf,.cb7,.cbt,.7z,.7zip,.tar.gz';
  private readonly supportedExtSet = new Set(this.acceptableExtensions.split(','));
  step: UploadStep = UploadStep.Select;
  uploadProgress = 0;
  uploadCurrentFile = '';
  uploadFileIndex = 0;
  uploadTotalFiles = 0;

  libraries: Library[] = [];
  selectedLibraryId: number | null = null;

  accumulatedFiles: File[] = [];
  uploadedFiles: UploadBookFileDto[] = [];
  fileForms: FormGroup[] = [];
  expandedRows = new Set<number>();
  reEnrichTerms: string[] = [];
  reEnrichLoading = new Set<number>();
  reEnrichSources: (MetadataSource | null)[] = [];

  ngOnInit(): void {
    this.libraryService.getLibraries().subscribe(libs => {
      this.libraries = libs;
      this.cdRef.markForCheck();
    });
  }

  async dropped(entries: NgxFileDropEntry[]) {
    const files = await this.resolveEntries(entries);
    this.accumulatedFiles = [...this.accumulatedFiles, ...files.filter(f => this.isSupportedFile(f))];
    this.cdRef.markForCheck();
  }

  onBrowseFiles(event: Event) {
    const input = event.target as HTMLInputElement;
    if (input.files) {
      this.accumulatedFiles = [...this.accumulatedFiles, ...Array.from(input.files).filter(f => this.isSupportedFile(f))];
      input.value = '';
      this.cdRef.markForCheck();
    }
  }

  private isSupportedFile(file: File): boolean {
    const name = file.name.toLowerCase();
    // Handle compound extensions like .tar.gz
    if (name.endsWith('.tar.gz')) return this.supportedExtSet.has('.tar.gz');
    const dotIndex = name.lastIndexOf('.');
    if (dotIndex < 0) return false;
    return this.supportedExtSet.has(name.substring(dotIndex));
  }

  removeFile(index: number) {
    this.accumulatedFiles.splice(index, 1);
  }

  clearFiles() {
    this.accumulatedFiles = [];
  }

  startUpload() {
    this.uploadFiles(this.accumulatedFiles);
  }

  toggleExpand(index: number) {
    if (this.expandedRows.has(index)) {
      this.expandedRows.delete(index);
    } else {
      this.expandedRows.add(index);
    }
  }

  getTargetPath(file: UploadBookFileDto, index: number): string {
    const seriesName = this.fileForms[index]?.value?.series || file.series || '?';
    const lib = this.libraries.find(l => l.id === this.selectedLibraryId);
    const libFolder = lib?.folders?.[0] || '(library folder)';

    return `${libFolder}/${seriesName}/${file.originalFileName}`;
  }

  hasDetailMetadata(file: UploadBookFileDto): boolean {
    return !!(file.title || file.writer || file.publisher || file.genre || file.year || file.summary);
  }

  private async resolveEntries(entries: NgxFileDropEntry[]): Promise<File[]> {
    const files: File[] = [];
    for (const entry of entries) {
      if (entry.fileEntry.isFile) {
        const file = await this.getFile(entry.fileEntry as FileSystemFileEntry);
        files.push(file);
      } else if (entry.fileEntry.isDirectory) {
        const dirFiles = await this.traverseDirectory(entry.fileEntry as FileSystemDirectoryEntry);
        files.push(...dirFiles);
      }
    }

    return files;
  }

  private async traverseDirectory(dirEntry: FileSystemDirectoryEntry): Promise<File[]> {
    const files: File[] = [];
    const reader = dirEntry.createReader();
    let batch: FileSystemEntry[];
    do {
      batch = await new Promise<FileSystemEntry[]>(resolve => reader.readEntries(resolve));
      for (const e of batch) {
        if (e.isFile) {
          files.push(await this.getFile(e as unknown as FileSystemFileEntry));
        } else if (e.isDirectory) {
          files.push(...await this.traverseDirectory(e as unknown as FileSystemDirectoryEntry));
        }
      }
    } while (batch.length > 0);

    return files;
  }

  private getFile(fileEntry: FileSystemFileEntry): Promise<File> {
    return new Promise(resolve => fileEntry.file(resolve));
  }

  private uploadFiles(files: File[]) {
    this.step = UploadStep.Uploading;
    this.uploadProgress = 0;
    this.uploadFileIndex = 1;
    this.uploadTotalFiles = files.length;
    this.uploadCurrentFile = files[0]?.name ?? '';
    this.cdRef.markForCheck();

    this.uploadService.uploadBooksSequentially(files).subscribe({
      next: event => {
        this.uploadProgress = event.overallProgress;
        this.uploadFileIndex = event.fileIndex + 1;
        this.uploadCurrentFile = event.fileName;

        if (event.result) {
          // All files failed — nothing to review
          if (event.result.length === 0) {
            const details = event.errorDetails?.join(', ') || '';
            if (details) {
              this.toastr.error(
                translate('upload-book-modal.partial-upload-details', {count: event.errors?.length || 0, details})
              );
            } else {
              this.toastr.error(translate('upload-book-modal.all-uploads-failed'));
            }
            this.step = UploadStep.Select;
            this.cdRef.detectChanges();

            return;
          }

          if (event.errors?.length) {
            const details = event.errorDetails?.join(', ') || event.errors.join(', ');
            this.toastr.warning(
              translate('upload-book-modal.partial-upload-details', {count: event.errors.length, details})
            );
          }

          this.uploadedFiles = event.result;
          this.buildForms();
          this.step = UploadStep.Review;

          // Auto-select library from first file's suggestion
          const suggested = this.uploadedFiles.find(f => f.suggestedLibraryId != null);
          if (suggested?.suggestedLibraryId) {
            this.selectedLibraryId = suggested.suggestedLibraryId;
          }
        }
        this.cdRef.detectChanges();
      },
      error: () => {
        this.toastr.error(translate('upload-book-modal.upload-failed'));
        this.step = UploadStep.Select;
        this.cdRef.detectChanges();
      }
    });
  }

  private buildForms() {
    this.fileForms = this.uploadedFiles.map(file => {
      return new FormGroup({
        series: new FormControl(file.series, [Validators.required]),
        volume: new FormControl(file.volume),
        number: new FormControl(file.number),
      });
    });
    this.reEnrichTerms = this.uploadedFiles.map(file => file.series);
    this.reEnrichSources = this.uploadedFiles.map(() => null);
  }

  applySeriestoAll(index: number) {
    const series = this.fileForms[index].value.series;
    for (const form of this.fileForms) {
      form.controls['series'].setValue(series);
    }
  }

  reEnrich(index: number) {
    const term = this.reEnrichTerms[index]?.trim();
    if (!term || this.reEnrichLoading.has(index)) return;

    this.reEnrichLoading.add(index);
    this.cdRef.markForCheck();

    const source = this.reEnrichSources[index];
    this.uploadService.reEnrich(term, this.uploadedFiles[index].format, undefined, undefined, source ?? undefined).subscribe({
      next: (result: ReEnrichResultDto) => {
        this.reEnrichLoading.delete(index);
        if (result.success) {
          const file = this.uploadedFiles[index];
          if (result.series) file.series = result.series;
          if (result.title) file.title = result.title;
          if (result.writer) file.writer = result.writer;
          if (result.summary) file.summary = result.summary;
          if (result.publisher) file.publisher = result.publisher;
          if (result.genre) file.genre = result.genre;
          if (result.year) file.year = result.year;
          file.metadataSource = result.metadataSource;
          file.externalUrl = result.externalUrl;

          // Update form controls
          if (result.series) this.fileForms[index].controls['series'].setValue(result.series);

          this.toastr.success(translate('upload-book-modal.re-enrich-success'));
        } else {
          this.toastr.info(translate('upload-book-modal.re-enrich-no-results'));
        }
        this.cdRef.markForCheck();
      },
      error: () => {
        this.reEnrichLoading.delete(index);
        this.toastr.error(translate('upload-book-modal.re-enrich-failed'));
        this.cdRef.markForCheck();
      }
    });
  }

  hasMetadata(file: UploadBookFileDto): boolean {
    return file.series.length > 0 && file.title.length > 0;
  }

  allFormsValid(): boolean {
    return this.fileForms.every(f => f.valid) && this.selectedLibraryId != null;
  }

  confirm() {
    if (!this.allFormsValid() || this.selectedLibraryId == null) return;

    this.step = UploadStep.Confirming;
    this.cdRef.markForCheck();

    const files: ConfirmUploadFileDto[] = this.uploadedFiles.map((file, i) => ({
      tempFileName: file.tempFileName,
      originalFileName: file.originalFileName,
      series: this.fileForms[i].value.series,
      volume: this.fileForms[i].value.volume || '',
      number: this.fileForms[i].value.number || '',
      title: file.title,
      writer: file.writer,
      summary: file.summary,
      publisher: file.publisher,
      genre: file.genre,
      year: file.year,
      metadataSource: file.metadataSource,
      externalUrl: file.externalUrl,
    }));

    this.uploadService.confirmUpload({
      libraryId: this.selectedLibraryId,
      files
    }).subscribe({
      next: () => {
        this.toastr.success(translate('upload-book-modal.upload-success'));
        this.modalRef.close(true);
      },
      error: () => {
        this.toastr.error(translate('upload-book-modal.confirm-failed'));
        this.step = UploadStep.Review;
        this.cdRef.markForCheck();
      }
    });
  }

  getProvidersForFile(index: number): {value: MetadataSource; label: string}[] {
    const format = this.uploadedFiles[index]?.format;
    const providers: {value: MetadataSource; label: string}[] = [];
    if (format === MangaFormat.ARCHIVE) {
      providers.push({value: MetadataSource.ComicVine, label: 'enrich-source-comicvine'});
      providers.push({value: MetadataSource.AniList, label: 'enrich-source-anilist'});
    } else if (format === MangaFormat.EPUB) {
      providers.push({value: MetadataSource.OpenLibrary, label: 'enrich-source-openlibrary'});
      providers.push({value: MetadataSource.AniList, label: 'enrich-source-anilist'});
    } else if (format === MangaFormat.PDF) {
      providers.push({value: MetadataSource.OpenLibrary, label: 'enrich-source-openlibrary'});
    }
    return providers;
  }

  close() {
    this.modalRef.close(false);
  }
}
