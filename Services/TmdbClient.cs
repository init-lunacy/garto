using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

public sealed class TmdbClient(IHttpClientFactory http, ILogger<TmdbClient> log)
{
    private const string BaseUrl = "https://api.themoviedb.org/3/";
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/original";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private HttpClient CreateClient(string accessToken)
    {
        var client = http.CreateClient(nameof(TmdbClient));
        client.BaseAddress = new Uri(BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            accessToken
        );
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
        return client;
    }

    public bool IsEnabled(Config.PluginConfiguration cfg)
    {
        return cfg.UseTmdbSearch && !string.IsNullOrWhiteSpace(cfg.TmdbAccessToken);
    }

    public async Task<IReadOnlyList<TmdbSearchResult>> SearchMoviesAsync(
        string accessToken,
        string query,
        int page = 1,
        CancellationToken ct = default
    )
    {
        var response = await GetAsync<TmdbSearchResponse>(
            accessToken,
            $"search/movie?query={Uri.EscapeDataString(query)}&page={page}&include_adult=false",
            ct
        ).ConfigureAwait(false);

        return response?.Results ?? [];
    }

    public async Task<IReadOnlyList<TmdbSearchResult>> SearchSeriesAsync(
        string accessToken,
        string query,
        int page = 1,
        CancellationToken ct = default
    )
    {
        var response = await GetAsync<TmdbSearchResponse>(
            accessToken,
            $"search/tv?query={Uri.EscapeDataString(query)}&page={page}&include_adult=false",
            ct
        ).ConfigureAwait(false);

        return response?.Results ?? [];
    }

    public Task<TmdbDetail?> GetMovieDetailAsync(
        string accessToken,
        int tmdbId,
        CancellationToken ct = default
    )
    {
        return GetAsync<TmdbDetail>(
            accessToken,
            $"movie/{tmdbId}?append_to_response=credits,videos,external_ids,images",
            ct
        );
    }

    public Task<TmdbDetail?> GetSeriesDetailAsync(
        string accessToken,
        int tmdbId,
        CancellationToken ct = default
    )
    {
        return GetAsync<TmdbDetail>(
            accessToken,
            $"tv/{tmdbId}?append_to_response=credits,videos,external_ids,images",
            ct
        );
    }

    public RemoteLookupHit ToLookupHit(TmdbSearchResult result, StremioMediaType mediaType)
    {
        var preview = new StremioMeta
        {
            Id = $"tmdb:{result.Id}",
            Type = mediaType,
            Name = GetName(result, mediaType),
            Title = GetName(result, mediaType),
            Description = result.Overview,
            Overview = result.Overview,
            Poster = ToImageUrl(result.PosterPath),
            Background = ToImageUrl(result.BackdropPath),
            Released = ParseDate(result.ReleaseDate ?? result.FirstAirDate),
            Year = GetYear(result.ReleaseDate ?? result.FirstAirDate),
        };

        return new RemoteLookupHit
        {
            Source = "tmdb",
            MediaType = mediaType,
            LookupId = result.Id.ToString(CultureInfo.InvariantCulture),
            TmdbId = result.Id.ToString(CultureInfo.InvariantCulture),
            PreviewMeta = preview,
        };
    }

    public StremioMeta ToMeta(TmdbDetail detail, StremioMediaType mediaType)
    {
        var runtime = detail.Runtime;
        if ((!runtime.HasValue || runtime <= 0) && detail.EpisodeRunTime is { Count: > 0 })
        {
            runtime = detail.EpisodeRunTime.FirstOrDefault(r => r > 0);
        }

        return new StremioMeta
        {
            Id = $"tmdb:{detail.Id}",
            ImdbId = detail.ExternalIds?.ImdbId ?? detail.ImdbId,
            Type = mediaType,
            Name = GetName(detail, mediaType),
            Title = GetName(detail, mediaType),
            Description = detail.Overview,
            Overview = detail.Overview,
            Poster = ToImageUrl(detail.PosterPath),
            Thumbnail = ToImageUrl(detail.PosterPath),
            Background = ToImageUrl(detail.BackdropPath),
            Released = ParseDate(detail.ReleaseDate ?? detail.FirstAirDate),
            Year = GetYear(detail.ReleaseDate ?? detail.FirstAirDate),
            Runtime = runtime?.ToString(CultureInfo.InvariantCulture),
            Genres = detail.Genres?.Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
            Genre = detail.Genres?.Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
            Trailers = detail.Videos?.Results?
                .Where(v =>
                    string.Equals(v.Site, "YouTube", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(v.Key)
                )
                .Select(v => new StremioTrailer { Source = v.Key, Type = v.Type })
                .ToList(),
            App_Extras = new StremioAppExtras
            {
                Cast = detail.Credits?.Cast?
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Take(20)
                    .Select(c => new StremioCast
                    {
                        Name = c.Name,
                        Character = c.Character,
                        Photo = ToImageUrl(c.ProfilePath),
                    })
                    .ToList(),
            },
        };
    }

    private async Task<T?> GetAsync<T>(string accessToken, string path, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = CreateClient(accessToken);
            using var response = await client.GetAsync(path, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct)
                .ConfigureAwait(false);

            if (GelatoRuntime.EnableWorkerLogging())
            {
                stopwatch.Stop();
                log.LogInformation(
                    "TMDb HTTP {Path} completed in {ElapsedMs}ms",
                    path,
                    stopwatch.ElapsedMilliseconds
                );
            }

            return payload;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            log.LogWarning(ex, "TMDb request failed for {Path}", path);
            return default;
        }
    }

    private static string GetName(TmdbSearchResult result, StremioMediaType mediaType)
    {
        return mediaType == StremioMediaType.Movie
            ? result.Title ?? result.OriginalTitle ?? string.Empty
            : result.Name ?? result.OriginalName ?? string.Empty;
    }

    private static string GetName(TmdbDetail result, StremioMediaType mediaType)
    {
        return mediaType == StremioMediaType.Movie
            ? result.Title ?? result.OriginalTitle ?? string.Empty
            : result.Name ?? result.OriginalName ?? string.Empty;
    }

    private static string? ToImageUrl(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : $"{ImageBaseUrl}{path}";
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed
        )
            ? parsed
            : null;
    }

    private static int? GetYear(string? value)
    {
        return ParseDate(value)?.Year;
    }
}

public sealed class TmdbSearchResponse
{
    public List<TmdbSearchResult>? Results { get; set; }
}

public sealed class TmdbSearchResult
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? OriginalTitle { get; set; }
    public string? OriginalName { get; set; }
    public string? Overview { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? ReleaseDate { get; set; }
    public string? FirstAirDate { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPathRaw
    {
        get => PosterPath;
        set => PosterPath = value;
    }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPathRaw
    {
        get => BackdropPath;
        set => BackdropPath = value;
    }

    [JsonPropertyName("release_date")]
    public string? ReleaseDateRaw
    {
        get => ReleaseDate;
        set => ReleaseDate = value;
    }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDateRaw
    {
        get => FirstAirDate;
        set => FirstAirDate = value;
    }

    [JsonPropertyName("original_title")]
    public string? OriginalTitleRaw
    {
        get => OriginalTitle;
        set => OriginalTitle = value;
    }

    [JsonPropertyName("original_name")]
    public string? OriginalNameRaw
    {
        get => OriginalName;
        set => OriginalName = value;
    }
}

public sealed class TmdbDetail
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? OriginalTitle { get; set; }
    public string? OriginalName { get; set; }
    public string? Overview { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? ReleaseDate { get; set; }
    public string? FirstAirDate { get; set; }
    public int? Runtime { get; set; }
    public List<int>? EpisodeRunTime { get; set; }
    public string? ImdbId { get; set; }
    public List<TmdbGenre>? Genres { get; set; }
    public TmdbCredits? Credits { get; set; }
    public TmdbVideoResponse? Videos { get; set; }
    public TmdbExternalIds? ExternalIds { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPathRaw
    {
        get => PosterPath;
        set => PosterPath = value;
    }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPathRaw
    {
        get => BackdropPath;
        set => BackdropPath = value;
    }

    [JsonPropertyName("release_date")]
    public string? ReleaseDateRaw
    {
        get => ReleaseDate;
        set => ReleaseDate = value;
    }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDateRaw
    {
        get => FirstAirDate;
        set => FirstAirDate = value;
    }

    [JsonPropertyName("original_title")]
    public string? OriginalTitleRaw
    {
        get => OriginalTitle;
        set => OriginalTitle = value;
    }

    [JsonPropertyName("original_name")]
    public string? OriginalNameRaw
    {
        get => OriginalName;
        set => OriginalName = value;
    }

    [JsonPropertyName("episode_run_time")]
    public List<int>? EpisodeRunTimeRaw
    {
        get => EpisodeRunTime;
        set => EpisodeRunTime = value;
    }

    [JsonPropertyName("imdb_id")]
    public string? ImdbIdRaw
    {
        get => ImdbId;
        set => ImdbId = value;
    }
}

public sealed class TmdbGenre
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class TmdbCredits
{
    public List<TmdbCastMember>? Cast { get; set; }
}

public sealed class TmdbCastMember
{
    public string? Name { get; set; }
    public string? Character { get; set; }

    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }
}

public sealed class TmdbVideoResponse
{
    public List<TmdbVideo>? Results { get; set; }
}

public sealed class TmdbVideo
{
    public string? Site { get; set; }
    public string? Type { get; set; }
    public string? Key { get; set; }
}

public sealed class TmdbExternalIds
{
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }
}
