using System.Diagnostics;
using Gelato.Config;
using Gelato.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public class SearchActionFilter(
    IDtoService dtoService,
    GelatoManager manager,
    TmdbClient tmdb,
    ILogger<SearchActionFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        var requestStopwatch = Stopwatch.StartNew();
        ctx.TryGetUserId(out var userId);
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (
            cfg.DisableSearch
            || !ctx.IsApiSearchAction()
            || !ctx.TryGetActionArgument<string>("searchTerm", out var searchTerm)
        )
        {
            await next();
            return;
        }

        // Strip "local:" prefix if present and pass through to default handler
        if (searchTerm.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            ctx.ActionArguments["searchTerm"] = searchTerm[6..].Trim();
            await next();
            return;
        }

        // Handle Stremio search
        var requestedTypes = GetRequestedItemTypes(ctx);
        if (requestedTypes.Count == 0)
        {
            await next();
            return;
        }

        var canUseTmdb = tmdb.IsEnabled(cfg);
        var canUseStremio = await cfg.Stremio.IsReady();
        if (!canUseTmdb && !canUseStremio)
        {
            await next();
            return;
        }

        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 25);

        var hits = await SearchLookupHitsAsync(searchTerm, requestedTypes, cfg, userId);

        if (GelatoRuntime.EnableWorkerLogging())
        {
            log.LogInformation(
                "Intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit} results={Results}",
                searchTerm,
                string.Join(",", requestedTypes),
                start,
                limit,
                hits.Count
            );
        }

        var dtoStopwatch = Stopwatch.StartNew();
        var dtos = ConvertHitsToDtos(hits);
        dtoStopwatch.Stop();
        var paged = dtos.Skip(start).Take(limit).ToArray();
        requestStopwatch.Stop();

        if (GelatoRuntime.EnableWorkerLogging())
        {
            log.LogInformation(
                "Gelato search completed query=\"{Query}\" totalMs={TotalMs} dtoMs={DtoMs} upstreamResults={Results} returned={Returned}",
                searchTerm,
                requestStopwatch.ElapsedMilliseconds,
                dtoStopwatch.ElapsedMilliseconds,
                hits.Count,
                paged.Length
            );
        }

        ctx.Result = new OkObjectResult(
            new QueryResult<BaseItemDto> { Items = paged, TotalRecordCount = dtos.Count }
        );
    }

    private HashSet<BaseItemKind> GetRequestedItemTypes(ActionExecutingContext ctx)
    {
        var requested = new HashSet<BaseItemKind>([BaseItemKind.Movie, BaseItemKind.Series]);

        // Already parsed as BaseItemKind[] by model binder
        if (
            ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var includeTypes)
            && includeTypes is { Length: > 0 }
        )
        {
            requested = new HashSet<BaseItemKind>(includeTypes);
            // Only keep Movie and Series
            requested.IntersectWith([BaseItemKind.Movie, BaseItemKind.Series]);
        }

        // Remove excluded types
        if (
            ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var excludeTypes)
            && excludeTypes is { Length: > 0 }
        )
        {
            requested.ExceptWith(excludeTypes);
        }

        // If mediaTypes=Video, exclude Series
        if (
            ctx.TryGetActionArgument<MediaType[]>("mediaTypes", out var mediaTypes)
            && mediaTypes.Contains(MediaType.Video)
        )
        {
            requested.Remove(BaseItemKind.Series);
        }

        return requested;
    }

    private async Task<List<RemoteLookupHit>> SearchLookupHitsAsync(
        string searchTerm,
        HashSet<BaseItemKind> requestedTypes,
        PluginConfiguration cfg,
        Guid userId
    )
    {
        var movieFolder = cfg.MovieFolder ?? manager.TryGetMovieFolder(userId);
        var seriesFolder = cfg.SeriesFolder ?? manager.TryGetSeriesFolder(userId);

        cfg.MovieFolder = movieFolder;
        cfg.SeriesFolder = seriesFolder;

        List<RemoteLookupHit> results = [];

        if (tmdb.IsEnabled(cfg))
        {
            try
            {
                results = await SearchTmdbAsync(
                    searchTerm,
                    requestedTypes,
                    cfg,
                    movieFolder is not null,
                    seriesFolder is not null
                ).ConfigureAwait(false);

                if (results.Count > 0)
                {
                    return ApplyReleaseFilter(results, cfg);
                }

                if (GelatoRuntime.EnableWorkerLogging())
                {
                    log.LogInformation(
                        "TMDb search returned no results for query=\"{Query}\". Falling back to AIO search.",
                        searchTerm
                    );
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "TMDb search failed for query=\"{Query}\". Falling back to AIO.", searchTerm);
            }
        }

        if (!await cfg.Stremio.IsReady())
        {
            return [];
        }

        results = await SearchStremioAsync(
            searchTerm,
            requestedTypes,
            cfg,
            movieFolder is not null,
            seriesFolder is not null
        ).ConfigureAwait(false);

        return ApplyReleaseFilter(results, cfg);
    }

    private async Task<List<RemoteLookupHit>> SearchTmdbAsync(
        string searchTerm,
        HashSet<BaseItemKind> requestedTypes,
        PluginConfiguration cfg,
        bool hasMovieFolder,
        bool hasSeriesFolder
    )
    {
        var tasks = new List<Task<IReadOnlyList<TmdbSearchResult>>>();
        var taskKinds = new List<StremioMediaType>();

        if (requestedTypes.Contains(BaseItemKind.Movie) && hasMovieFolder)
        {
            tasks.Add(tmdb.SearchMoviesAsync(cfg.TmdbAccessToken, searchTerm));
            taskKinds.Add(StremioMediaType.Movie);
        }
        else if (requestedTypes.Contains(BaseItemKind.Movie))
        {
            log.LogWarning(
                "No movie folder found, please add your gelato path to a library and rescan. skipping TMDb movie search"
            );
        }

        if (requestedTypes.Contains(BaseItemKind.Series) && hasSeriesFolder)
        {
            tasks.Add(tmdb.SearchSeriesAsync(cfg.TmdbAccessToken, searchTerm));
            taskKinds.Add(StremioMediaType.Series);
        }
        else if (requestedTypes.Contains(BaseItemKind.Series))
        {
            log.LogWarning(
                "No series folder found, please add your gelato path to a library and rescan. skipping TMDb series search"
            );
        }

        var searchStopwatch = Stopwatch.StartNew();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        searchStopwatch.Stop();

        var hits = new List<RemoteLookupHit>();
        for (var i = 0; i < results.Length; i++)
        {
            hits.AddRange(results[i].Select(r => tmdb.ToLookupHit(r, taskKinds[i])));
        }

        if (GelatoRuntime.EnableWorkerLogging())
        {
            log.LogInformation(
                "TMDb upstream search query=\"{Query}\" kinds=[{Kinds}] durationMs={DurationMs} results={Results}",
                searchTerm,
                string.Join(",", taskKinds),
                searchStopwatch.ElapsedMilliseconds,
                hits.Count
            );
        }

        return hits;
    }

    private async Task<List<RemoteLookupHit>> SearchStremioAsync(
        string searchTerm,
        HashSet<BaseItemKind> requestedTypes,
        PluginConfiguration cfg,
        bool hasMovieFolder,
        bool hasSeriesFolder
    )
    {
        var tasks = new List<Task<IReadOnlyList<StremioMeta>>>();
        var taskKinds = new List<string>();

        if (requestedTypes.Contains(BaseItemKind.Movie) && hasMovieFolder)
        {
            tasks.Add(cfg.Stremio.SearchAsync(searchTerm, StremioMediaType.Movie));
            taskKinds.Add("movie");
        }
        else if (requestedTypes.Contains(BaseItemKind.Movie))
        {
            log.LogWarning(
                "No movie folder found, please add your gelato path to a library and rescan. skipping search"
            );
        }

        if (requestedTypes.Contains(BaseItemKind.Series) && hasSeriesFolder)
        {
            tasks.Add(cfg.Stremio.SearchAsync(searchTerm, StremioMediaType.Series));
            taskKinds.Add("series");
        }
        else if (requestedTypes.Contains(BaseItemKind.Series))
        {
            log.LogWarning(
                "No series folder found, please add your gelato path to a library and rescan. skipping search"
            );
        }

        var searchStopwatch = Stopwatch.StartNew();
        var results = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(r => r).ToList();
        searchStopwatch.Stop();

        if (GelatoRuntime.EnableWorkerLogging())
        {
            log.LogInformation(
                "Gelato upstream AIO search query=\"{Query}\" kinds=[{Kinds}] durationMs={DurationMs} results={Results}",
                searchTerm,
                string.Join(",", taskKinds),
                searchStopwatch.ElapsedMilliseconds,
                results.Count
            );
        }

        return results
            .Select(meta => new RemoteLookupHit
            {
                Source = "stremio",
                MediaType = meta.Type,
                LookupId = meta.ImdbId ?? meta.Id,
                TmdbId = meta.Id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase)
                    ? meta.Id["tmdb:".Length..]
                    : null,
                ImdbId = meta.ImdbId,
                PreviewMeta = meta,
            })
            .ToList();
    }

    private static List<RemoteLookupHit> ApplyReleaseFilter(
        List<RemoteLookupHit> results,
        PluginConfiguration cfg
    )
    {
        if (!cfg.FilterUnreleased)
        {
            return results;
        }

        var bufferDays = cfg.FilterUnreleasedBufferDays;
        return results
            .Where(x =>
                x.PreviewMeta.IsReleased(
                    x.MediaType == StremioMediaType.Movie ? bufferDays : 0
                )
            )
            .ToList();
    }

    private List<BaseItemDto> ConvertHitsToDtos(List<RemoteLookupHit> hits)
    {
        var options = new DtoOptions { EnableImages = true, EnableUserData = false };
        var dtos = new List<BaseItemDto>(hits.Count);

        foreach (var hit in hits)
        {
            var baseItem = manager.IntoBaseItem(hit.PreviewMeta);
            if (baseItem is null)
                continue;

            var dto = dtoService.GetBaseItemDto(baseItem, options);
            dto.Id = RemoteLookupKey.ToGuid(hit);
            dtos.Add(dto);

            manager.SaveLookupHit(dto.Id, hit);
        }

        return dtos;
    }
}
