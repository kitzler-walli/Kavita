import {MangaFormat} from '../manga-format';

export interface UploadBookFileDto {
  tempFileName: string;
  originalFileName: string;
  format: MangaFormat;
  series: string;
  volume: string;
  number: string;
  title: string;
  writer: string;
  summary: string;
  suggestedLibraryId: number | null;
}

export interface ConfirmUploadDto {
  libraryId: number;
  files: ConfirmUploadFileDto[];
}

export interface ConfirmUploadFileDto {
  tempFileName: string;
  originalFileName: string;
  series: string;
  volume: string;
  number: string;
}
