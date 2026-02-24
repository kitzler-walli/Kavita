using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using API.Entities.Enums;
using API.Services.MetadataEnrichment;

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
    public string Publisher { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int Year { get; set; }

    public int? SuggestedLibraryId { get; set; }

    /// <summary>
    /// Source of the metadata: 0=Local, 1=ComicVine, 2=OpenLibrary, 3=AniList
    /// </summary>
    public int MetadataSource { get; set; }

    /// <summary>
    /// External URL from the enrichment provider (e.g. ComicVine issue page, AniList entry)
    /// </summary>
    public string? ExternalUrl { get; set; }
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

    // Enrichment metadata to embed into the file before placing
    public string Title { get; set; } = string.Empty;
    public string Writer { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int Year { get; set; }

    /// <summary>
    /// Source of the metadata: 0=Local, 1=ComicVine, 2=OpenLibrary, 3=AniList
    /// </summary>
    public int MetadataSource { get; set; }

    /// <summary>
    /// External URL from the enrichment provider
    /// </summary>
    public string? ExternalUrl { get; set; }
}

public sealed record ReEnrichUploadDto
{
    [Required] public required string SearchTerm { get; set; }
    [Required] public required MangaFormat Format { get; set; }
    public string? Number { get; set; }
    public string? Isbn { get; set; }
    public MetadataSource? Source { get; set; }
}

public sealed record ReEnrichResultDto
{
    public bool Success { get; set; }
    public int MetadataSource { get; set; }
    public string? ExternalUrl { get; set; }
    public string Series { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Writer { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int Year { get; set; }
}
