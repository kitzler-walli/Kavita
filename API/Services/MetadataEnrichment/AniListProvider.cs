using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using API.Entities.Enums;
using API.Helpers;
using Flurl.Http;
using Kavita.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace API.Services.MetadataEnrichment;

#nullable enable

public class AniListProvider : IMetadataEnrichmentProvider
{
    private const string GraphQlUrl = "https://graphql.anilist.co";
    private static readonly RateLimiter RateLimiter = new(80, TimeSpan.FromMinutes(1), true);

    private const string MediaQuery = @"
        query ($search: String!) {
          Page(page: 1, perPage: 3) {
            media(search: $search, type: MANGA) {
              id
              siteUrl
              title { romaji english native }
              volumes
              chapters
              startDate { year }
              description(asHtml: false)
              genres
              staff(sort: RELEVANCE, perPage: 5) {
                edges { node { name { full } } role }
              }
            }
          }
        }";

    private readonly ILogger<AniListProvider> _logger;

    public AniListProvider(ILogger<AniListProvider> logger)
    {
        _logger = logger;
        FlurlConfiguration.ConfigureClientForUrl(GraphQlUrl);
    }

    public MetadataSource Source => MetadataSource.AniList;

    public bool CanHandle(EnrichmentContext context)
    {
        // AniList is for manga/LN — primarily archive formats, but also EPUB for light novels
        return context.Format is MangaFormat.Archive or MangaFormat.Epub;
    }

    public async Task<EnrichmentResult> EnrichAsync(EnrichmentContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context.Series))
        {
            return new EnrichmentResult { Source = Source, Success = false };
        }

        if (!RateLimiter.TryAcquire(string.Empty))
        {
            _logger.LogInformation("AniList rate limit exhausted, skipping enrichment");

            return new EnrichmentResult { Source = Source, Success = false };
        }

        var searchTerm = SearchTermCleaner.Clean(context.Series);
        _logger.LogDebug("AniList searching for cleaned term: '{SearchTerm}' (original: '{Original}')",
            searchTerm, context.Series);

        var body = new
        {
            query = MediaQuery,
            variables = new { search = searchTerm }
        };

        IFlurlResponse response;
        try
        {
            response = await GraphQlUrl
                .WithHeader("Content-Type", "application/json")
                .WithHeader("Accept", "application/json")
                .PostJsonAsync(body, cancellationToken: ct);
        }
        catch (FlurlHttpException ex) when (ex.StatusCode == 429)
        {
            // AniList rate limit hit — wait for Retry-After (default 60s, cap at 90s) and retry once
            var retryAfter = 60;
            if (ex.Call?.Response?.Headers != null &&
                ex.Call.Response.Headers.TryGetFirst("Retry-After", out var retryVal) &&
                int.TryParse(retryVal, out var parsed) && parsed > 0)
            {
                retryAfter = Math.Min(parsed, 90);
            }

            _logger.LogInformation("AniList returned 429, waiting {Seconds}s before retry", retryAfter);
            await Task.Delay(TimeSpan.FromSeconds(retryAfter), ct);

            response = await GraphQlUrl
                .WithHeader("Content-Type", "application/json")
                .WithHeader("Accept", "application/json")
                .PostJsonAsync(body, cancellationToken: ct);
        }

        var json = await response.GetStringAsync();
        var result = JsonSerializer.Deserialize<AniListGraphQlResponse>(json);

        var mediaList = result?.Data?.Page?.Media;
        if (mediaList == null || mediaList.Count == 0)
        {
            return new EnrichmentResult { Source = Source, Success = false };
        }

        // Pick the best match by title similarity
        var bestMatch = mediaList
            .OrderByDescending(m => BestTitleSimilarity(m.Title, context.Series))
            .First();

        var bestScore = BestTitleSimilarity(bestMatch.Title, context.Series);
        _logger.LogDebug("AniList best match: '{Title}' (score: {Score:F2}, threshold: 0.40)",
            bestMatch.Title?.English ?? bestMatch.Title?.Romaji ?? "?", bestScore);

        // Only proceed if we have a reasonable match
        if (bestScore < 0.4)
        {
            _logger.LogDebug("AniList match score {Score:F2} below threshold, skipping", bestScore);

            return new EnrichmentResult { Source = Source, Success = false };
        }

        // Extract writer from staff
        string? writer = null;
        var storyStaff = bestMatch.Staff?.Edges?
            .FirstOrDefault(e => e.Role != null &&
                                 e.Role.Contains("Story", StringComparison.OrdinalIgnoreCase));
        if (storyStaff?.Node?.Name?.Full != null)
        {
            writer = storyStaff.Node.Name.Full;
        }

        var summary = bestMatch.Description;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            summary = StripHtml(summary);
        }

        return new EnrichmentResult
        {
            Source = Source,
            Success = true,
            // Always return the canonical series name so the UI can offer a
            // better suggestion than the raw filename
            Series = bestMatch.Title?.English ?? bestMatch.Title?.Romaji,
            Writer = string.IsNullOrWhiteSpace(context.Writer) ? writer : null,
            Summary = string.IsNullOrWhiteSpace(context.Summary) ? summary : null,
            Genre = string.IsNullOrWhiteSpace(context.Genre) && bestMatch.Genres is { Count: > 0 }
                ? string.Join(", ", bestMatch.Genres)
                : null,
            Year = context.Year == 0 ? bestMatch.StartDate?.Year : null,
            ExternalUrl = bestMatch.SiteUrl
        };
    }

    private static double BestTitleSimilarity(AniListTitle? title, string search)
    {
        if (title == null) return 0;

        var candidates = new[] { title.English, title.Romaji, title.Native }
            .Where(t => !string.IsNullOrWhiteSpace(t));

        var searchLower = search.Trim().ToLowerInvariant();
        var best = 0.0;
        foreach (var candidate in candidates)
        {
            var sim = StringSimilarity(candidate!, search);
            if (sim > best) best = sim;

            // Containment check: if the AniList title is a meaningful prefix/substring
            // of the search term (or vice versa), boost the score. This handles translated
            // titles like "Kakegurui - Das Leben ist ein Spiel" containing "Kakegurui".
            var candidateLower = candidate!.Trim().ToLowerInvariant();
            if (candidateLower.Length >= 4 &&
                (searchLower.StartsWith(candidateLower) || searchLower.Contains(candidateLower) ||
                 candidateLower.StartsWith(searchLower) || candidateLower.Contains(searchLower)))
            {
                var containSim = (double)Math.Min(candidateLower.Length, searchLower.Length) /
                                 Math.Max(candidateLower.Length, searchLower.Length);
                // Boost containment matches: a contained title that's at least 40% of the
                // search length is a strong signal
                var boosted = Math.Max(containSim, 0.6);
                if (boosted > best) best = boosted;
            }
        }

        return best;
    }

    private static double StringSimilarity(string a, string b)
    {
        a = a.Trim().ToLowerInvariant();
        b = b.Trim().ToLowerInvariant();

        if (a == b) return 1.0;

        var maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 1.0;

        var distance = LevenshteinDistance(a, b);

        return 1.0 - ((double)distance / maxLen);
    }

    private static int LevenshteinDistance(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    private static string StripHtml(string html)
    {
        return System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", string.Empty).Trim();
    }

    #region AniList Response Models

    private class AniListGraphQlResponse
    {
        [JsonPropertyName("data")]
        public AniListData? Data { get; set; }
    }

    private class AniListData
    {
        [JsonPropertyName("Page")]
        public AniListPage? Page { get; set; }
    }

    private class AniListPage
    {
        [JsonPropertyName("media")]
        public List<AniListMedia>? Media { get; set; }
    }

    private class AniListMedia
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("siteUrl")]
        public string? SiteUrl { get; set; }

        [JsonPropertyName("title")]
        public AniListTitle? Title { get; set; }

        [JsonPropertyName("volumes")]
        public int? Volumes { get; set; }

        [JsonPropertyName("chapters")]
        public int? Chapters { get; set; }

        [JsonPropertyName("startDate")]
        public AniListDate? StartDate { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("genres")]
        public List<string>? Genres { get; set; }

        [JsonPropertyName("staff")]
        public AniListStaffConnection? Staff { get; set; }
    }

    private class AniListTitle
    {
        [JsonPropertyName("romaji")]
        public string? Romaji { get; set; }

        [JsonPropertyName("english")]
        public string? English { get; set; }

        [JsonPropertyName("native")]
        public string? Native { get; set; }
    }

    private class AniListDate
    {
        [JsonPropertyName("year")]
        public int? Year { get; set; }
    }

    private class AniListStaffConnection
    {
        [JsonPropertyName("edges")]
        public List<AniListStaffEdge>? Edges { get; set; }
    }

    private class AniListStaffEdge
    {
        [JsonPropertyName("node")]
        public AniListStaffNode? Node { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }
    }

    private class AniListStaffNode
    {
        [JsonPropertyName("name")]
        public AniListStaffName? Name { get; set; }
    }

    private class AniListStaffName
    {
        [JsonPropertyName("full")]
        public string? Full { get; set; }
    }

    #endregion
}
