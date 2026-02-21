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
        ILogger<UploadBookService> logger)
    {
        _directoryService = directoryService;
        _archiveService = archiveService;
        _bookService = bookService;
        _unitOfWork = unitOfWork;
        _taskScheduler = taskScheduler;
        _logger = logger;
    }

    public async Task<IList<UploadBookFileDto>> ProcessUploadsAsync(IList<IFormFile> files)
    {
        var results = new List<UploadBookFileDto>();
        var libraries = (await _unitOfWork.LibraryRepository.GetLibrariesAsync(
            LibraryIncludes.Folders | LibraryIncludes.FileTypes, track: false)).ToList();

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

            if (string.IsNullOrWhiteSpace(series))
            {
                series = Path.GetFileNameWithoutExtension(file.FileName);
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
                SuggestedLibraryId = suggestedLibraryId
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
}
