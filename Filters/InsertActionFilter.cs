using System.Diagnostics;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Gelato.Services;

namespace Gelato.Filters;

public class InsertActionFilter(
    GelatoManager manager,
    IUserManager userManager,
    TmdbClient tmdb,
    ILogger<InsertActionFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    private readonly KeyLock _lock = new();
    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (
            !ctx.IsInsertableAction()
            || !ctx.TryGetRouteGuid(out var guid)
            || !ctx.TryGetUserId(out var userId)
            || userManager.GetUserById(userId) is not { } user
            || manager.GetLookupHit(guid) is not { } lookupHit
        )
        {
            await next();
            return;
        }

        // Get root folder
        var isSeries = lookupHit.MediaType == StremioMediaType.Series;
        var root = isSeries
            ? manager.TryGetSeriesFolder(userId)
            : manager.TryGetMovieFolder(userId);
        if (root is null)
        {
            log.LogWarning("No {Type} folder configured", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        if (manager.IntoBaseItem(lookupHit.PreviewMeta) is { } item)
        {
            var existing = manager.FindExistingItem(item, user);
            if (existing is not null)
            {
                log.LogInformation(
                    "Media already exists; redirecting to canonical id {Id}",
                    existing.Id
                );
                ctx.ReplaceGuid(existing.Id);
                await next();
                return;
            }
        }

        // Fetch full metadata
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var metaStopwatch = Stopwatch.StartNew();
        var meta = await ResolveLookupMetaAsync(lookupHit, cfg);
        metaStopwatch.Stop();
        if (meta is null)
        {
            log.LogError(
                "No metadata resolved for lookup source={Source} id={Id} type={Type}",
                lookupHit.Source,
                lookupHit.LookupId,
                lookupHit.MediaType
            );
            await next();
            return;
        }

        if (GelatoRuntime.EnableWorkerLogging())
        {
            log.LogInformation(
                "Gelato insert fetched full meta for source={Source} id={Id} type={Type} in {ElapsedMs}ms",
                lookupHit.Source,
                lookupHit.LookupId,
                lookupHit.MediaType,
                metaStopwatch.ElapsedMilliseconds
            );
        }

        // Insert the item
        var insertStopwatch = Stopwatch.StartNew();
        var baseItem = await InsertMetaAsync(guid, root, meta, user, queueRefreshItem: false);
        insertStopwatch.Stop();
        if (baseItem is not null)
        {
            if (GelatoRuntime.EnableWorkerLogging())
            {
                log.LogInformation(
                    "Gelato insert created/resolved item {Name} ({Id}) in {ElapsedMs}ms",
                    baseItem.Name,
                    baseItem.Id,
                    insertStopwatch.ElapsedMilliseconds
                );
            }

            if (
                lookupHit.MediaType == StremioMediaType.Series
                && string.Equals(lookupHit.Source, "tmdb", StringComparison.OrdinalIgnoreCase)
            )
            {
                EnqueueSeriesTreeHydration(cfg, meta);
            }

            ctx.ReplaceGuid(baseItem.Id);
        }

        await next();
    }

    private void EnqueueSeriesTreeHydration(Config.PluginConfiguration cfg, StremioMeta meta)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await cfg.Stremio.IsReady().ConfigureAwait(false))
                {
                    return;
                }

                var seriesMeta = await cfg.Stremio.GetMetaAsync(
                    meta.ImdbId ?? meta.Id,
                    StremioMediaType.Series
                ).ConfigureAwait(false);

                if (seriesMeta?.Videos is not { Count: > 0 })
                {
                    return;
                }

                await manager
                    .SyncSeriesTreesAsync(cfg, seriesMeta, CancellationToken.None)
                    .ConfigureAwait(false);

                if (GelatoRuntime.EnableWorkerLogging())
                {
                    log.LogInformation(
                        "Queued TMDb-backed series tree hydration completed for {SeriesId}",
                        meta.ImdbId ?? meta.Id
                    );
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Series tree hydration failed for {SeriesId}", meta.ImdbId ?? meta.Id);
            }
        });
    }

    private async Task<StremioMeta?> ResolveLookupMetaAsync(
        RemoteLookupHit lookupHit,
        Config.PluginConfiguration cfg
    )
    {
        if (
            string.Equals(lookupHit.Source, "tmdb", StringComparison.OrdinalIgnoreCase)
            && tmdb.IsEnabled(cfg)
            && int.TryParse(lookupHit.TmdbId ?? lookupHit.LookupId, out var tmdbId)
        )
        {
            var detail = lookupHit.MediaType switch
            {
                StremioMediaType.Movie => await tmdb.GetMovieDetailAsync(
                    cfg.TmdbAccessToken,
                    tmdbId
                ).ConfigureAwait(false),
                StremioMediaType.Series => await tmdb.GetSeriesDetailAsync(
                    cfg.TmdbAccessToken,
                    tmdbId
                ).ConfigureAwait(false),
                _ => null,
            };

            if (detail is not null)
            {
                return tmdb.ToMeta(detail, lookupHit.MediaType);
            }
        }

        if (!await cfg.Stremio.IsReady())
        {
            return null;
        }

        return await cfg.Stremio.GetMetaAsync(
            lookupHit.ImdbId ?? lookupHit.PreviewMeta.ImdbId ?? lookupHit.PreviewMeta.Id,
            lookupHit.MediaType
        ).ConfigureAwait(false);
    }

    public async Task<BaseItem?> InsertMetaAsync(
        Guid guid,
        Folder root,
        StremioMeta meta,
        User user,
        bool queueRefreshItem
    )
    {
        BaseItem? baseItem = null;
        var created = false;

        await _lock.RunQueuedAsync(
            guid,
            async ct =>
            {
                meta.Guid = guid;
                (baseItem, created) = await manager.InsertMeta(
                    root,
                    meta,
                    user,
                    false,
                    true,
                    queueRefreshItem,
                    ct
                );
            }
        );

        if (baseItem is not null && created)
            log.LogInformation("inserted new media: {Name}", baseItem.Name);

        return baseItem;
    }
}
