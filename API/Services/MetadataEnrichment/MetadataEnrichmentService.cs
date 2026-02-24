using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace API.Services.MetadataEnrichment;

#nullable enable

public interface IMetadataEnrichmentService
{
    Task<EnrichmentResult> EnrichAsync(EnrichmentContext context, CancellationToken ct = default);
    Task<IList<EnrichmentResult>> SearchAsync(EnrichmentContext context, CancellationToken ct = default);
}

public class MetadataEnrichmentService : IMetadataEnrichmentService
{
    private readonly IEnumerable<IMetadataEnrichmentProvider> _providers;
    private readonly ILogger<MetadataEnrichmentService> _logger;

    public MetadataEnrichmentService(
        IEnumerable<IMetadataEnrichmentProvider> providers,
        ILogger<MetadataEnrichmentService> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public async Task<EnrichmentResult> EnrichAsync(EnrichmentContext context, CancellationToken ct = default)
    {
        var applicable = _providers
            .Where(p => p.CanHandle(context))
            .Where(p => context.PreferredSource == null || p.Source == context.PreferredSource)
            .OrderBy(p => p.Source) // ComicVine=1 first, then OpenLibrary=2, then AniList=3
            .ToList();

        foreach (var provider in applicable)
        {
            try
            {
                _logger.LogDebug("Trying enrichment provider {Source} for series '{Series}'",
                    provider.Source, context.Series);

                var result = await provider.EnrichAsync(context, ct);
                if (result.Success)
                {
                    _logger.LogInformation("Enriched '{Series}' from {Source}",
                        context.Series, result.Source);

                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Enrichment provider {Source} failed for '{Series}', trying next",
                    provider.Source, context.Series);
            }
        }

        return new EnrichmentResult { Source = MetadataSource.Local, Success = false };
    }

    public async Task<IList<EnrichmentResult>> SearchAsync(EnrichmentContext context, CancellationToken ct = default)
    {
        var applicable = _providers
            .Where(p => p.CanHandle(context))
            .Where(p => context.PreferredSource == null || p.Source == context.PreferredSource)
            .OrderBy(p => p.Source)
            .ToList();

        var allResults = new List<EnrichmentResult>();

        foreach (var provider in applicable)
        {
            try
            {
                _logger.LogDebug("Searching enrichment provider {Source} for '{Series}'",
                    provider.Source, context.Series);

                var results = await provider.SearchAsync(context, ct: ct);
                allResults.AddRange(results);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Enrichment search provider {Source} failed for '{Series}', continuing to next",
                    provider.Source, context.Series);
            }
        }

        return allResults
            .OrderByDescending(r => r.MatchScore)
            .ThenBy(r => r.Source)
            .ToList();
    }
}
