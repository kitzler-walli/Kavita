using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using API.Entities.Enums;

namespace API.Services.MetadataEnrichment;

#nullable enable

public enum MetadataSource
{
    Local = 0,
    ComicVine = 1,
    OpenLibrary = 2,
    AniList = 3
}

public record EnrichmentContext
{
    public required MangaFormat Format { get; init; }
    public string Series { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Writer { get; init; } = string.Empty;
    public string Volume { get; init; } = string.Empty;
    public string Number { get; init; } = string.Empty;
    public string Isbn { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Genre { get; init; } = string.Empty;
    public int Year { get; init; }
}

/// <summary>
/// Result of an enrichment attempt. Null fields mean "not enriched, keep original".
/// </summary>
public record EnrichmentResult
{
    public MetadataSource Source { get; init; }
    public bool Success { get; init; }
    public string? Series { get; init; }
    public string? Title { get; init; }
    public string? Writer { get; init; }
    public string? Volume { get; init; }
    public string? Number { get; init; }
    public string? Summary { get; init; }
    public string? Publisher { get; init; }
    public string? Genre { get; init; }
    public string? ExternalUrl { get; init; }
    public int? Year { get; init; }
}

public interface IMetadataEnrichmentProvider
{
    MetadataSource Source { get; }
    bool CanHandle(EnrichmentContext context);
    Task<EnrichmentResult> EnrichAsync(EnrichmentContext context, CancellationToken ct = default);
}

/// <summary>
/// Utility to clean noisy filename-derived series names for API searches.
/// Strips volume/chapter numbers, parenthetical tags (language, group), and trailing noise.
/// </summary>
public static partial class SearchTermCleaner
{
    // Matches parenthetical groups: (GER), (altraverse), (FG-Manga), etc.
    [GeneratedRegex(@"\([^)]*\)", RegexOptions.Compiled)]
    private static partial Regex ParentheticalRegex();

    // Matches volume/chapter indicators: v01, Vol 2, 05, #12, ch.3, etc. at end or mid-string
    [GeneratedRegex(@"\b(?:v(?:ol(?:ume)?)?\.?\s*\d+|ch(?:apter)?\.?\s*\d+|#\d+|\d{2,})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VolumeChapterRegex();

    // Collapse multiple spaces
    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    public static string Clean(string seriesName)
    {
        if (string.IsNullOrWhiteSpace(seriesName)) return seriesName;

        // Remove parenthetical content
        var cleaned = ParentheticalRegex().Replace(seriesName, " ");

        // Remove volume/chapter numbers
        cleaned = VolumeChapterRegex().Replace(cleaned, " ");

        // Collapse spaces and trim
        cleaned = MultiSpaceRegex().Replace(cleaned, " ").Trim();

        // Remove trailing hyphens/dashes left from cleanup
        cleaned = cleaned.TrimEnd('-', ' ').Trim();

        // If cleaning removed everything, fall back to original
        return string.IsNullOrWhiteSpace(cleaned) ? seriesName.Trim() : cleaned;
    }
}
