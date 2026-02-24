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
  publisher: string;
  genre: string;
  year: number;
  suggestedLibraryId: number | null;
  metadataSource: number;  // 0=Local, 1=ComicVine, 2=OpenLibrary, 3=AniList
  externalUrl?: string;
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
  title: string;
  writer: string;
  summary: string;
  publisher: string;
  genre: string;
  year: number;
  metadataSource: number;
  externalUrl?: string;
}

export interface ReEnrichResultDto {
  success: boolean;
  metadataSource: number;
  externalUrl?: string;
  series: string;
  title: string;
  writer: string;
  summary: string;
  publisher: string;
  genre: string;
  year: number;
}
