using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using API.Data;
using API.Entities.Enums;
using API.Helpers;
using Flurl;
using Flurl.Http;
using Kavita.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace API.Services.MetadataEnrichment;

#nullable enable

public class ComicVineProvider : IMetadataEnrichmentProvider
{
    private const string BaseUrl = "https://comicvine.gamespot.com/api";
    private static readonly RateLimiter RateLimiter = new(180, TimeSpan.FromHours(1), true);

    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ComicVineProvider> _logger;

    public ComicVineProvider(IUnitOfWork unitOfWork, ILogger<ComicVineProvider> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        FlurlConfiguration.ConfigureClientForUrl(BaseUrl);
    }

    public MetadataSource Source => MetadataSource.ComicVine;

    public bool CanHandle(EnrichmentContext context)
    {
        return context.Format == MangaFormat.Archive;
    }

    public async Task<EnrichmentResult> EnrichAsync(EnrichmentContext context, CancellationToken ct = default)
    {
        var apiKey = (await _unitOfWork.SettingsRepository.GetSettingAsync(ServerSettingKey.ComicVineApiKey)).Value;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug("ComicVine API key not configured, skipping enrichment");

            return new EnrichmentResult { Source = Source, Success = false };
        }

        if (!RateLimiter.TryAcquire(string.Empty))
        {
            _logger.LogInformation("ComicVine rate limit exhausted, skipping enrichment");

            return new EnrichmentResult { Source = Source, Success = false };
        }

        // Search for volumes matching the series name
        var searchTerm = SearchTermCleaner.Clean(context.Series);
        var searchResult = await new Url($"{BaseUrl}/search/")
            .SetQueryParams(new
            {
                api_key = apiKey,
                format = "json",
                resources = "volume",
                query = searchTerm,
                field_list = "id,name,start_year,publisher,description,count_of_issues"
            })
            .WithHeader("User-Agent", "Kavita/1.0 (self-hosted book server)")
            .GetJsonAsync<ComicVineSearchResponse>(cancellationToken: ct);

        if (searchResult?.Results == null || searchResult.Results.Count == 0)
        {
            return new EnrichmentResult { Source = Source, Success = false };
        }

        // Pick the best match by name similarity
        var bestVolume = searchResult.Results
            .OrderByDescending(v => StringSimilarity(v.Name ?? string.Empty, context.Series))
            .First();

        string? writer = null;
        string? summary = bestVolume.Description != null ? StripHtml(bestVolume.Description) : null;
        string? publisher = bestVolume.Publisher?.Name;
        string? externalUrl = null;
        int? year = null;

        if (int.TryParse(bestVolume.StartYear, out var startYear))
        {
            year = startYear;
        }

        // If we have an issue number, try to fetch the specific issue
        if (!string.IsNullOrWhiteSpace(context.Number) && !RateLimiter.TryAcquire(string.Empty))
        {
            // Rate limited on second request, return what we have from the volume
        }
        else if (!string.IsNullOrWhiteSpace(context.Number))
        {
            var issueResult = await new Url($"{BaseUrl}/issues/")
                .SetQueryParams(new
                {
                    api_key = apiKey,
                    format = "json",
                    filter = $"volume:{bestVolume.Id},issue_number:{context.Number}",
                    field_list = "id,name,issue_number,description,person_credits,cover_date,site_detail_url"
                })
                .WithHeader("User-Agent", "Kavita/1.0 (self-hosted book server)")
                .GetJsonAsync<ComicVineIssueResponse>(cancellationToken: ct);

            if (issueResult?.Results is { Count: > 0 })
            {
                var issue = issueResult.Results[0];
                if (!string.IsNullOrWhiteSpace(issue.Description))
                {
                    summary = StripHtml(issue.Description);
                }

                externalUrl = issue.SiteDetailUrl;

                // Extract writer from person credits
                var writerCredit = issue.PersonCredits?
                    .FirstOrDefault(p => p.Role != null &&
                                        p.Role.Contains("writer", StringComparison.OrdinalIgnoreCase));
                if (writerCredit != null)
                {
                    writer = writerCredit.Name;
                }
            }
        }

        return new EnrichmentResult
        {
            Source = Source,
            Success = true,
            Series = bestVolume.Name,
            Summary = string.IsNullOrWhiteSpace(context.Summary) ? summary : null,
            Publisher = string.IsNullOrWhiteSpace(context.Publisher) ? publisher : null,
            Writer = string.IsNullOrWhiteSpace(context.Writer) ? writer : null,
            Year = context.Year == 0 ? year : null,
            ExternalUrl = externalUrl
        };
    }

    private static double StringSimilarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;

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

    #region ComicVine API Response Models

    private class ComicVineSearchResponse
    {
        [JsonPropertyName("results")]
        public List<ComicVineVolume> Results { get; set; } = [];
    }

    private class ComicVineVolume
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("start_year")]
        public string? StartYear { get; set; }

        [JsonPropertyName("publisher")]
        public ComicVinePublisher? Publisher { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("count_of_issues")]
        public int? CountOfIssues { get; set; }
    }

    private class ComicVinePublisher
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class ComicVineIssueResponse
    {
        [JsonPropertyName("results")]
        public List<ComicVineIssue> Results { get; set; } = [];
    }

    private class ComicVineIssue
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("issue_number")]
        public string? IssueNumber { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("person_credits")]
        public List<ComicVinePersonCredit>? PersonCredits { get; set; }

        [JsonPropertyName("cover_date")]
        public string? CoverDate { get; set; }

        [JsonPropertyName("site_detail_url")]
        public string? SiteDetailUrl { get; set; }
    }

    private class ComicVinePersonCredit
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }
    }

    #endregion
}
