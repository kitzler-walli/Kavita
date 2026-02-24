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
}
