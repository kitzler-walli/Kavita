using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using API.Data;
using API.Data.Metadata;
using API.Data.Repositories;
using API.DTOs.Uploads;
using API.Entities.Enums;
using API.Services.MetadataEnrichment;
using API.Services.Tasks.Scanner.Parser;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace API.Services;

#nullable enable

public interface IUploadBookService
{
    /// <summary>
    /// Save uploaded files to temp, extract metadata, return per-file metadata.
    /// </summary>
    Task<IList<UploadBookFileDto>> ProcessUploadsAsync(IList<IFormFile> files);

    /// <summary>
    /// Move files from temp to library folder organized by series name, trigger scan.
    /// </summary>
    Task ConfirmUploadsAsync(ConfirmUploadDto dto, int userId);
}

public class UploadBookService : IUploadBookService
{
    private readonly IDirectoryService _directoryService;
    private readonly IArchiveService _archiveService;
    private readonly IBookService _bookService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITaskScheduler _taskScheduler;
    private readonly IMetadataEnrichmentService _enrichmentService;
    private readonly ILogger<UploadBookService> _logger;

    private static readonly Regex SupportedExtensionRegex = new(
        @"^(" + Parser.SupportedExtensions + @")$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(500));

    private static readonly Regex InvalidPathCharsRegex = new(
        @"[<>:""/\\|?*\x00-\x1f]",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(500));

    public UploadBookService(
        IDirectoryService directoryService,
        IArchiveService archiveService,
        IBookService bookService,
        IUnitOfWork unitOfWork,
        ITaskScheduler taskScheduler,
        IMetadataEnrichmentService enrichmentService,
        ILogger<UploadBookService> logger)
    {
        _directoryService = directoryService;
        _archiveService = archiveService;
        _bookService = bookService;
        _unitOfWork = unitOfWork;
        _taskScheduler = taskScheduler;
        _enrichmentService = enrichmentService;
        _logger = logger;
    }

    public async Task<IList<UploadBookFileDto>> ProcessUploadsAsync(IList<IFormFile> files)
    {
        var results = new List<UploadBookFileDto>();
        var libraries = (await _unitOfWork.LibraryRepository.GetLibrariesAsync(
            LibraryIncludes.Folders | LibraryIncludes.FileTypes, track: false)).ToList();

        var enrichmentEnabled = false;
        try
        {
            var enrichmentSetting = await _unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.EnableUploadEnrichment);
            enrichmentEnabled = enrichmentSetting?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception)
        {
            // Setting may not exist yet (pre-migration), default to disabled
        }

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.FileName);
            if (string.IsNullOrEmpty(ext) || !SupportedExtensionRegex.IsMatch(ext))
            {
                _logger.LogWarning("Rejected upload with unsupported extension: {FileName}", file.FileName);
                continue;
            }

            // Save to temp with random name preserving extension
            var tempFileName = $"{Guid.NewGuid()}{ext}";
            var tempPath = Path.Combine(_directoryService.TempDirectory, tempFileName);

            await using (var stream = _directoryService.FileSystem.File.Create(tempPath))
            {
                await file.CopyToAsync(stream);
            }

            var format = Parser.ParseFormat(tempPath);

            // Extract metadata
            var comicInfo = ExtractMetadata(tempPath, format);

            // Fallback to filename if no metadata
            var series = comicInfo?.Series ?? string.Empty;
            var volume = comicInfo?.Volume ?? string.Empty;
            var number = comicInfo?.Number ?? string.Empty;
            var title = comicInfo?.Title ?? string.Empty;
            var writer = comicInfo?.Writer ?? string.Empty;
            var summary = comicInfo?.Summary ?? string.Empty;
            var publisher = comicInfo?.Publisher ?? string.Empty;
            var genre = comicInfo?.Genre ?? string.Empty;
            var year = comicInfo?.Year ?? 0;

            var seriesFromFile = false;
            if (string.IsNullOrWhiteSpace(series))
            {
                // Clean the filename to get a better series guess (strip volume numbers,
                // parenthetical tags like language/publisher/group)
                series = SearchTermCleaner.Clean(Path.GetFileNameWithoutExtension(file.FileName));
                seriesFromFile = true;
            }

            // Try external metadata enrichment for empty fields
            var metadataSource = (int)MetadataSource.Local;
            string? externalUrl = null;

            if (enrichmentEnabled)
            {
                try
                {
                    var enrichContext = new EnrichmentContext
                    {
                        Format = format,
                        Series = series,
                        Title = title,
                        Writer = writer,
                        Volume = volume,
                        Number = number,
                        Isbn = comicInfo?.Isbn ?? string.Empty,
                        Summary = summary,
                        Publisher = comicInfo?.Publisher ?? string.Empty,
                        Genre = comicInfo?.Genre ?? string.Empty,
                        Year = comicInfo?.Year ?? 0
                    };

                    var enrichResult = await _enrichmentService.EnrichAsync(enrichContext);
                    if (enrichResult.Success)
                    {
                        // Fill only empty fields — never overwrite local metadata
                        // Exception: if the series was derived from the filename, prefer
                        // the canonical name from the enrichment provider
                        if (!string.IsNullOrWhiteSpace(enrichResult.Series) &&
                            (string.IsNullOrWhiteSpace(series) || seriesFromFile))
                            series = enrichResult.Series;
                        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(enrichResult.Title))
                            title = enrichResult.Title;
                        if (string.IsNullOrWhiteSpace(writer) && !string.IsNullOrWhiteSpace(enrichResult.Writer))
                            writer = enrichResult.Writer;
                        if (string.IsNullOrWhiteSpace(volume) && !string.IsNullOrWhiteSpace(enrichResult.Volume))
                            volume = enrichResult.Volume;
                        if (string.IsNullOrWhiteSpace(number) && !string.IsNullOrWhiteSpace(enrichResult.Number))
                            number = enrichResult.Number;
                        if (string.IsNullOrWhiteSpace(summary) && !string.IsNullOrWhiteSpace(enrichResult.Summary))
                            summary = enrichResult.Summary;
                        if (string.IsNullOrWhiteSpace(publisher) && !string.IsNullOrWhiteSpace(enrichResult.Publisher))
                            publisher = enrichResult.Publisher;
                        if (string.IsNullOrWhiteSpace(genre) && !string.IsNullOrWhiteSpace(enrichResult.Genre))
                            genre = enrichResult.Genre;
                        if (year == 0 && enrichResult.Year is > 0)
                            year = enrichResult.Year.Value;

                        metadataSource = (int)enrichResult.Source;
                        externalUrl = enrichResult.ExternalUrl;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Metadata enrichment failed for {FileName}, using local metadata", file.FileName);
                }
            }

            // Suggest library based on file type
            int? suggestedLibraryId = SuggestLibrary(format, libraries);

            results.Add(new UploadBookFileDto
            {
                TempFileName = tempFileName,
                OriginalFileName = file.FileName,
                Format = format,
                Series = series,
                Volume = volume,
                Number = number,
                Title = title,
                Writer = writer,
                Summary = summary,
                Publisher = publisher,
                Genre = genre,
                Year = year,
                SuggestedLibraryId = suggestedLibraryId,
                MetadataSource = metadataSource,
                ExternalUrl = externalUrl
            });
        }

        return results;
    }

    public async Task ConfirmUploadsAsync(ConfirmUploadDto dto, int userId)
    {
        var library = await _unitOfWork.LibraryRepository.GetLibraryForIdAsync(dto.LibraryId,
            LibraryIncludes.Folders | LibraryIncludes.AppUser);

        if (library == null)
            throw new ArgumentException("Library does not exist");

        if (!library.AppUsers.Any(u => u.Id == userId))
            throw new UnauthorizedAccessException("User does not have access to this library");

        var libraryFolder = library.Folders.FirstOrDefault()?.Path;
        if (string.IsNullOrEmpty(libraryFolder))
            throw new InvalidOperationException("Library has no folder configured");

        foreach (var file in dto.Files)
        {
            var tempPath = Path.Combine(_directoryService.TempDirectory, file.TempFileName);
            if (!_directoryService.FileSystem.File.Exists(tempPath))
                throw new FileNotFoundException($"Temp file not found: {file.TempFileName}");

            var sanitizedSeries = SanitizeDirectoryName(file.Series);
            if (string.IsNullOrWhiteSpace(sanitizedSeries))
                throw new ArgumentException($"Invalid series name for file: {file.OriginalFileName}");

            // Embed enrichment metadata into the file before moving
            EmbedMetadata(tempPath, file);

            var targetDir = Path.Combine(libraryFolder, sanitizedSeries);
            _directoryService.ExistOrCreate(targetDir);

            var targetPath = Path.Combine(targetDir, file.OriginalFileName);
            if (_directoryService.FileSystem.File.Exists(targetPath))
                throw new InvalidOperationException($"File already exists: {file.OriginalFileName}");

            _directoryService.FileSystem.File.Move(tempPath, targetPath);
            _logger.LogInformation("Placed uploaded file {FileName} at {TargetPath}", file.OriginalFileName, targetPath);
        }

        // Trigger library scan
        await _taskScheduler.ScanLibrary(dto.LibraryId);
    }

    private ComicInfo? ExtractMetadata(string filePath, MangaFormat format)
    {
        try
        {
            return format switch
            {
                MangaFormat.Archive => _archiveService.GetComicInfo(filePath),
                MangaFormat.Epub or MangaFormat.Pdf => _bookService.GetComicInfo(filePath),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract metadata from {FilePath}", filePath);

            return null;
        }
    }

    private static int? SuggestLibrary(MangaFormat format, List<Entities.Library> libraries)
    {
        var targetGroup = format switch
        {
            MangaFormat.Archive => FileTypeGroup.Archive,
            MangaFormat.Epub => FileTypeGroup.Epub,
            MangaFormat.Pdf => FileTypeGroup.Pdf,
            MangaFormat.Image => FileTypeGroup.Images,
            _ => (FileTypeGroup?)null
        };

        if (targetGroup == null) return null;

        var match = libraries.FirstOrDefault(l =>
            l.LibraryFileTypes.Any(ft => ft.FileTypeGroup == targetGroup));

        return match?.Id;
    }

    private static string SanitizeDirectoryName(string name)
    {
        var sanitized = InvalidPathCharsRegex.Replace(name.Trim(), "_");

        return sanitized;
    }

    private void EmbedMetadata(string tempPath, ConfirmUploadFileDto file)
    {
        // Only embed if there's actually enrichment data to write
        if (string.IsNullOrWhiteSpace(file.Title) && string.IsNullOrWhiteSpace(file.Writer) &&
            string.IsNullOrWhiteSpace(file.Summary) && string.IsNullOrWhiteSpace(file.Publisher) &&
            string.IsNullOrWhiteSpace(file.Genre) && file.Year == 0)
        {
            return;
        }

        var comicInfo = new ComicInfo
        {
            Title = file.Title,
            Series = file.Series,
            Volume = file.Volume,
            Number = file.Number,
            Writer = file.Writer,
            Summary = file.Summary,
            Publisher = file.Publisher,
            Genre = file.Genre,
            Year = file.Year,
            Web = file.ExternalUrl ?? string.Empty
        };

        var format = Parser.ParseFormat(tempPath);
        switch (format)
        {
            case MangaFormat.Archive:
                if (!_archiveService.WriteComicInfo(tempPath, comicInfo))
                {
                    _logger.LogWarning("Could not embed metadata into archive {FileName} — format may not support writing",
                        file.OriginalFileName);
                }
                break;
            case MangaFormat.Epub:
                if (!_bookService.WriteEpubMetadata(tempPath, comicInfo))
                {
                    _logger.LogWarning("Could not embed metadata into EPUB {FileName}", file.OriginalFileName);
                }
                break;
            default:
                _logger.LogInformation("Skipping metadata embedding for {FileName} — format {Format} does not support writing",
                    file.OriginalFileName, format);
                break;
        }
    }
}
