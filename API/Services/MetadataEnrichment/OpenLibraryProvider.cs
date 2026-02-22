using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using API.Entities.Enums;
using API.Helpers;
using Flurl;
using Flurl.Http;
using Kavita.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace API.Services.MetadataEnrichment;

#nullable enable

public class OpenLibraryProvider : IMetadataEnrichmentProvider
{
    private const string BaseUrl = "https://openlibrary.org";
    private static readonly RateLimiter RateLimiter = new(1, TimeSpan.FromSeconds(1), true);

    private readonly ILogger<OpenLibraryProvider> _logger;

    public OpenLibraryProvider(ILogger<OpenLibraryProvider> logger)
    {
        _logger = logger;
        FlurlConfiguration.ConfigureClientForUrl(BaseUrl);
    }

    public MetadataSource Source => MetadataSource.OpenLibrary;

    public bool CanHandle(EnrichmentContext context)
    {
        return context.Format is MangaFormat.Epub or MangaFormat.Pdf;
    }

    public async Task<EnrichmentResult> EnrichAsync(EnrichmentContext context, CancellationToken ct = default)
    {
        if (!RateLimiter.TryAcquire(string.Empty))
        {
            _logger.LogInformation("Open Library rate limit exhausted, skipping enrichment");

            return new EnrichmentResult { Source = Source, Success = false };
        }

        // Strategy 1: If ISBN is available, look up by ISBN
        if (!string.IsNullOrWhiteSpace(context.Isbn))
        {
            return await EnrichByIsbn(context, ct);
        }

        // Strategy 2: Search by title/author
        if (!string.IsNullOrWhiteSpace(context.Series))
        {
            return await EnrichBySearch(context, ct);
        }

        return new EnrichmentResult { Source = Source, Success = false };
    }

    private async Task<EnrichmentResult> EnrichByIsbn(EnrichmentContext context, CancellationToken ct)
    {
        try
        {
            var json = await $"{BaseUrl}/isbn/{context.Isbn}.json"
                .WithHeader("User-Agent", "Kavita/1.0 (self-hosted book server)")
                .GetStringAsync(cancellationToken: ct);

            var edition = JsonSerializer.Deserialize<OpenLibraryEdition>(json);
            if (edition == null)
            {
                return new EnrichmentResult { Source = Source, Success = false };
            }

            var externalUrl = !string.IsNullOrWhiteSpace(edition.Key)
                ? $"{BaseUrl}{edition.Key}"
                : null;

            return new EnrichmentResult
            {
                Source = Source,
                Success = true,
                Title = string.IsNullOrWhiteSpace(context.Title) ? edition.Title : null,
                Publisher = string.IsNullOrWhiteSpace(context.Publisher) && edition.Publishers is { Count: > 0 }
                    ? edition.Publishers[0]
                    : null,
                Year = context.Year == 0 && edition.PublishDate != null
                    ? ExtractYear(edition.PublishDate)
                    : null,
                Summary = string.IsNullOrWhiteSpace(context.Summary) && edition.Description != null
                    ? GetDescriptionText(edition.Description)
                    : null,
                Genre = string.IsNullOrWhiteSpace(context.Genre) && edition.Subjects is { Count: > 0 }
                    ? string.Join(", ", edition.Subjects.Take(5))
                    : null,
                ExternalUrl = externalUrl
            };
        }
        catch (FlurlHttpException ex) when (ex.StatusCode == 404)
        {
            _logger.LogDebug("ISBN {Isbn} not found on Open Library", context.Isbn);

            return new EnrichmentResult { Source = Source, Success = false };
        }
    }

    private async Task<EnrichmentResult> EnrichBySearch(EnrichmentContext context, CancellationToken ct)
    {
        var searchTerm = SearchTermCleaner.Clean(context.Series);
        var queryParams = new Dictionary<string, object>
        {
            ["title"] = searchTerm,
            ["limit"] = "3",
            ["fields"] = "title,author_name,first_publish_year,isbn,publisher,subject,key"
        };

        if (!string.IsNullOrWhiteSpace(context.Writer))
        {
            queryParams["author"] = context.Writer;
        }

        var searchResult = await new Url($"{BaseUrl}/search.json")
            .SetQueryParams(queryParams)
            .WithHeader("User-Agent", "Kavita/1.0 (self-hosted book server)")
            .GetJsonAsync<OpenLibrarySearchResult>(cancellationToken: ct);

        if (searchResult?.Docs == null || searchResult.Docs.Count == 0)
        {
            return new EnrichmentResult { Source = Source, Success = false };
        }

        var doc = searchResult.Docs[0];
        var externalUrl = !string.IsNullOrWhiteSpace(doc.Key)
            ? $"{BaseUrl}{doc.Key}"
            : null;

        return new EnrichmentResult
        {
            Source = Source,
            Success = true,
            Writer = string.IsNullOrWhiteSpace(context.Writer) && doc.AuthorName is { Count: > 0 }
                ? string.Join(", ", doc.AuthorName)
                : null,
            Year = context.Year == 0 ? doc.FirstPublishYear : null,
            Publisher = string.IsNullOrWhiteSpace(context.Publisher) && doc.Publisher is { Count: > 0 }
                ? doc.Publisher[0]
                : null,
            Genre = string.IsNullOrWhiteSpace(context.Genre) && doc.Subject is { Count: > 0 }
                ? string.Join(", ", doc.Subject.Take(5))
                : null,
            ExternalUrl = externalUrl
        };
    }

    private static int? ExtractYear(string dateStr)
    {
        // Try common formats: "2020", "January 1, 2020", "2020-01-01"
        if (int.TryParse(dateStr, out var year))
        {
            return year;
        }

        if (DateTime.TryParse(dateStr, out var date))
        {
            return date.Year;
        }

        return null;
    }

    private static string? GetDescriptionText(JsonElement? description)
    {
        if (description == null) return null;

        // Description can be a string or an object with a "value" key
        if (description.Value.ValueKind == JsonValueKind.String)
        {
            return description.Value.GetString();
        }

        if (description.Value.ValueKind == JsonValueKind.Object &&
            description.Value.TryGetProperty("value", out var value))
        {
            return value.GetString();
        }

        return null;
    }

    #region Open Library Response Models

    private class OpenLibraryEdition
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("publishers")]
        public List<string>? Publishers { get; set; }

        [JsonPropertyName("publish_date")]
        public string? PublishDate { get; set; }

        [JsonPropertyName("description")]
        public JsonElement? Description { get; set; }

        [JsonPropertyName("subjects")]
        public List<string>? Subjects { get; set; }
    }

    private class OpenLibrarySearchResult
    {
        [JsonPropertyName("docs")]
        public List<OpenLibraryDoc> Docs { get; set; } = [];
    }

    private class OpenLibraryDoc
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("author_name")]
        public List<string>? AuthorName { get; set; }

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; set; }

        [JsonPropertyName("isbn")]
        public List<string>? Isbn { get; set; }

        [JsonPropertyName("publisher")]
        public List<string>? Publisher { get; set; }

        [JsonPropertyName("subject")]
        public List<string>? Subject { get; set; }
    }

    #endregion
}
