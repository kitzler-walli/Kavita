using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using API.Entities.Enums;

namespace API.DTOs.Uploads;

#nullable enable

/// <summary>
/// Returned after the upload phase — one per file uploaded.
/// </summary>
public sealed record UploadBookFileDto
{
    public required string TempFileName { get; set; }
    public required string OriginalFileName { get; set; }
    public required MangaFormat Format { get; set; }

    // Extracted metadata (user can edit these before confirming)
    public string Series { get; set; } = string.Empty;
    public string Volume { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Writer { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;

    public int? SuggestedLibraryId { get; set; }
}

/// <summary>
/// Sent by the frontend to confirm placement of previously uploaded files into a library.
/// </summary>
public sealed record ConfirmUploadDto
{
    [Required]
    public required int LibraryId { get; set; }

    [Required]
    public required List<ConfirmUploadFileDto> Files { get; set; }
}

public sealed record ConfirmUploadFileDto
{
    [Required]
    public required string TempFileName { get; set; }

    [Required]
    public required string OriginalFileName { get; set; }

    [Required]
    public required string Series { get; set; }

    public string Volume { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
}
