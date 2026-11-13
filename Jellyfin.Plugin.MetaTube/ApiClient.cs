using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Web;
using Jellyfin.Plugin.MetaTube.Metadata;
#if __EMBY__
using MediaBrowser.Common.Net;
#endif

namespace Jellyfin.Plugin.MetaTube;

public sealed class ApiRequestException : Exception
{
    public ApiRequestException(HttpStatusCode statusCode, int apiErrorCode, string message)
        : base($"API request error: {apiErrorCode} ({message})")
    {
        StatusCode = statusCode;
        ApiErrorCode = apiErrorCode;
    }

    public HttpStatusCode StatusCode { get; }

    public int ApiErrorCode { get; }

    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound || ApiErrorCode == 404;
}

public static class ApiClient
{
    private const string ActorInfoApi = "/v1/actors";
    private const string MovieInfoApi = "/v1/movies";
    private const string ActorSearchApi = "/v1/actors/search";
    private const string MovieSearchApi = "/v1/movies/search";
    private const string PrimaryImageApi = "/v1/images/primary";
    private const string ThumbImageApi = "/v1/images/thumb";
    private const string BackdropImageApi = "/v1/images/backdrop";
    private const string TranslateApi = "/v1/translate";
    private const string FlareSolverrConfigApi = "/v1/config/flaresolverr";
    private const string DomainsConfigApi = "/v1/config/domains";
    private const string JavDbSessionConfigApi = "/v1/config/javdb-session";

    private static string ComposeUrl(string path, NameValueCollection nv)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        foreach (string key in nv) query.Add(key, nv.Get(key));

        // Build URL
        var uriBuilder = new UriBuilder(Plugin.Instance.Configuration.Server)
        {
            Path = path,
            Query = query.ToString() ?? string.Empty
        };
        return uriBuilder.ToString();
    }

    private static string ComposeImageApiUrl(string path, string provider, string id, string url = default,
        double ratio = -1, double position = -1, bool auto = false, string badge = default)
    {
        return ComposeUrl(Path.Combine(path, provider, id), new NameValueCollection
        {
            { "url", url },
            { "ratio", ratio.ToString("R") },
            { "pos", position.ToString("R") },
            { "auto", auto.ToString() },
            { "badge", badge },
            { "quality", Plugin.Instance.Configuration.DefaultImageQuality.ToString() }
        });
    }

    private static string ComposeInfoApiUrl(string path, string provider, string id, bool lazy)
    {
        return ComposeUrl(Path.Combine(path, provider, id), new NameValueCollection
        {
            { "lazy", lazy.ToString() }
        });
    }

    private static string ComposeSearchApiUrl(string path, string q, string provider, bool fallback)
    {
        return ComposeUrl(path, new NameValueCollection
        {
            { "q", q },
            { "provider", provider },
            { "fallback", fallback.ToString() }
        });
    }

    private static string ComposeTranslateApiUrl(string path, string q, string from, string to, string engine,
        NameValueCollection nv = null)
    {
        return ComposeUrl(path, new NameValueCollection
        {
            { "q", q },
            { "from", from },
            { "to", to },
            { "engine", engine },
            nv ?? new NameValueCollection()
        });
    }

    public static string GetPrimaryImageApiUrl(string provider, string id, double position = -1, string badge = default)
    {
        return ComposeImageApiUrl(PrimaryImageApi, provider, id,
            ratio: Plugin.Instance.Configuration.PrimaryImageRatio, position: position, badge: badge);
    }

    public static string GetPrimaryImageApiUrl(string provider, string id, string url, double position = -1,
        bool auto = false, string badge = default)
    {
        return ComposeImageApiUrl(PrimaryImageApi, provider, id, url,
            Plugin.Instance.Configuration.PrimaryImageRatio, position, auto, badge);
    }

    public static string GetThumbImageApiUrl(string provider, string id)
    {
        return ComposeImageApiUrl(ThumbImageApi, provider, id);
    }

    public static string GetThumbImageApiUrl(string provider, string id, string url, double position = -1,
        bool auto = false)
    {
        return ComposeImageApiUrl(ThumbImageApi, provider, id, url, position: position, auto: auto);
    }

    public static string GetBackdropImageApiUrl(string provider, string id)
    {
        return ComposeImageApiUrl(BackdropImageApi, provider, id);
    }

    public static string GetBackdropImageApiUrl(string provider, string id, string url, double position = -1,
        bool auto = false)
    {
        return ComposeImageApiUrl(BackdropImageApi, provider, id, url, position: position, auto: auto);
    }

#if __EMBY__
    public static async Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
#else
    public static async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
#endif
    {
        await ConfigureServerAsync(cancellationToken).ConfigureAwait(false);

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", DefaultUserAgent);
        var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
#if __EMBY__
        return new HttpResponseInfo
        {
            Content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            ContentLength = response.Content.Headers.ContentLength,
            ContentType = response.Content.Headers.ContentType?.ToString(),
            StatusCode = response.StatusCode,
            Headers = response.Content.Headers.ToDictionary(kvp => kvp.Key, kvp => string.Join(", ", kvp.Value))
        };
#else
        return response;
#endif
    }

    public static async Task<ActorInfo> GetActorInfoAsync(string provider, string id,
        CancellationToken cancellationToken)
    {
        return await GetActorInfoAsync(provider, id, true /* default */, cancellationToken);
    }

    public static async Task<ActorInfo> GetActorInfoAsync(string provider, string id, bool lazy,
        CancellationToken cancellationToken)
    {
        var apiUrl = ComposeInfoApiUrl(ActorInfoApi, provider, id, lazy);
        return await GetDataAsync<ActorInfo>(apiUrl, true, cancellationToken);
    }

    public static async Task<MovieInfo> GetMovieInfoAsync(string provider, string id,
        CancellationToken cancellationToken)
    {
        return await GetMovieInfoAsync(provider, id, true /* default */, cancellationToken);
    }

    public static async Task<MovieInfo> GetMovieInfoAsync(string provider, string id, bool lazy,
        CancellationToken cancellationToken)
    {
        var apiUrl = ComposeInfoApiUrl(MovieInfoApi, provider, id, lazy);
        return await GetDataAsync<MovieInfo>(apiUrl, true, cancellationToken);
    }

    public static async Task<List<ActorSearchResult>> SearchActorAsync(string q,
        CancellationToken cancellationToken)
    {
        return await SearchActorAsync(q, string.Empty, cancellationToken);
    }

    public static async Task<List<ActorSearchResult>> SearchActorAsync(string q, string provider,
        CancellationToken cancellationToken)
    {
        return await SearchActorAsync(q, provider, true /* default */, cancellationToken);
    }

    public static async Task<List<ActorSearchResult>> SearchActorAsync(string q, string provider,
        bool fallback, CancellationToken cancellationToken)
    {
        var apiUrl = ComposeSearchApiUrl(ActorSearchApi, q, provider, fallback);
        return await GetDataAsync<List<ActorSearchResult>>(apiUrl, true, cancellationToken);
    }

    public static async Task<List<MovieSearchResult>> SearchMovieAsync(string q,
        CancellationToken cancellationToken)
    {
        return await SearchMovieAsync(q, string.Empty, cancellationToken);
    }

    public static async Task<List<MovieSearchResult>> SearchMovieAsync(string q, string provider,
        CancellationToken cancellationToken)
    {
        return await SearchMovieAsync(q, provider, true /* default */, cancellationToken);
    }

    public static async Task<List<MovieSearchResult>> SearchMovieAsync(string q, string provider,
        bool fallback, CancellationToken cancellationToken)
    {
        var apiUrl = ComposeSearchApiUrl(MovieSearchApi, q, provider, fallback);
        return await GetDataAsync<List<MovieSearchResult>>(apiUrl, true, cancellationToken);
    }

    public static async Task<TranslationInfo> TranslateAsync(string q, string from, string to, string engine,
        NameValueCollection nv, CancellationToken cancellationToken)
    {
        var apiUrl = ComposeTranslateApiUrl(TranslateApi, q, from, to, engine, nv);
        return await GetDataAsync<TranslationInfo>(apiUrl, false, cancellationToken);
    }

    private static async Task ConfigureFlareSolverrAsync(CancellationToken cancellationToken)
    {
        var server = Plugin.Instance.Configuration.Server;
        var token = Plugin.Instance.Configuration.Token;
        var endpoint = Plugin.Instance.Configuration.FlareSolverrEndpoint?.Trim() ?? string.Empty;

        if (IsFlareSolverrSyncCurrent(server, token, endpoint))
            return;

        await FlareSolverrSyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            server = Plugin.Instance.Configuration.Server;
            token = Plugin.Instance.Configuration.Token;
            endpoint = Plugin.Instance.Configuration.FlareSolverrEndpoint?.Trim() ?? string.Empty;

            if (IsFlareSolverrSyncCurrent(server, token, endpoint))
                return;

            var request = new HttpRequestMessage(HttpMethod.Put, ComposeUrl(FlareSolverrConfigApi,
                new NameValueCollection()));
            request.Content = JsonContent.Create(new FlareSolverrConfigRequest
            {
                Endpoint = endpoint
            });

            AddGeneralHeaders(request);

            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

            using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _ = await ReadDataAsync<FlareSolverrConfigResult>(response, cancellationToken)
                .ConfigureAwait(false);

            lock (FlareSolverrSyncStateLock)
            {
                _lastFlareSolverrServer = server;
                _lastFlareSolverrToken = token;
                _lastFlareSolverrEndpoint = endpoint;
                _lastFlareSolverrSync = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            FlareSolverrSyncLock.Release();
        }
    }

    private static async Task ConfigureDomainsAsync(CancellationToken cancellationToken)
    {
        var server = Plugin.Instance.Configuration.Server;
        var token = Plugin.Instance.Configuration.Token;
        var (signature, providers, primaryDomains) = GetDomainsConfiguration();

        if (IsDomainsSyncCurrent(server, token, signature))
            return;

        await DomainsSyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            server = Plugin.Instance.Configuration.Server;
            token = Plugin.Instance.Configuration.Token;
            (signature, providers, primaryDomains) = GetDomainsConfiguration();

            if (IsDomainsSyncCurrent(server, token, signature))
                return;

            // Avoid an unnecessary migration request when no custom domains are used.
            if (!IsDomainsSyncInitialized &&
                providers.Values.All(domains => domains.Length == 0) &&
                primaryDomains.Values.All(string.IsNullOrWhiteSpace))
            {
                MarkDomainsSyncComplete(server, token, signature);
                return;
            }

            var request = new HttpRequestMessage(HttpMethod.Put, ComposeUrl(DomainsConfigApi,
                new NameValueCollection()));
            request.Content = JsonContent.Create(new DomainsConfigRequest
            {
                Providers = providers,
                PrimaryDomains = primaryDomains
            });

            AddGeneralHeaders(request);

            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

            using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _ = await ReadDataAsync<DomainsConfigResult>(response, cancellationToken).ConfigureAwait(false);

            MarkDomainsSyncComplete(server, token, signature);
        }
        finally
        {
            DomainsSyncLock.Release();
        }
    }

    private static async Task ConfigureJavDbSessionAsync(CancellationToken cancellationToken)
    {
        var server = Plugin.Instance.Configuration.Server;
        var token = Plugin.Instance.Configuration.Token;
        var username = Plugin.Instance.Configuration.JavDbUsername?.Trim() ?? string.Empty;
        var password = Plugin.Instance.Configuration.JavDbPassword ?? string.Empty;
        var cookie = Plugin.Instance.Configuration.JavDbCookie?.Trim() ?? string.Empty;
        var authorization = Plugin.Instance.Configuration.JavDbAuthorization?.Trim() ?? string.Empty;
        var apiUrl = Plugin.Instance.Configuration.JavDbApiUrl?.Trim() ?? string.Empty;
        var primaryDomain = Plugin.Instance.Configuration.JavDbPrimaryDomain?.Trim() ?? string.Empty;
        var signature = GetJavDbSessionSignature(primaryDomain, apiUrl, username, password, cookie, authorization);

        if (IsJavDbSessionSyncCurrent(server, token, signature))
            return;

        await JavDbSessionSyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            server = Plugin.Instance.Configuration.Server;
            token = Plugin.Instance.Configuration.Token;
            username = Plugin.Instance.Configuration.JavDbUsername?.Trim() ?? string.Empty;
            password = Plugin.Instance.Configuration.JavDbPassword ?? string.Empty;
            cookie = Plugin.Instance.Configuration.JavDbCookie?.Trim() ?? string.Empty;
            authorization = Plugin.Instance.Configuration.JavDbAuthorization?.Trim() ?? string.Empty;
            apiUrl = Plugin.Instance.Configuration.JavDbApiUrl?.Trim() ?? string.Empty;
            primaryDomain = Plugin.Instance.Configuration.JavDbPrimaryDomain?.Trim() ?? string.Empty;
            signature = GetJavDbSessionSignature(primaryDomain, apiUrl, username, password, cookie, authorization);

            if (IsJavDbSessionSyncCurrent(server, token, signature))
                return;

            var hasCredentials = username.Length > 0 || password.Length > 0 || cookie.Length > 0 ||
                                 authorization.Length > 0;
            if (!IsJavDbSessionSyncInitialized && !hasCredentials)
            {
                MarkJavDbSessionSyncComplete(server, token, signature, DateTimeOffset.MaxValue);
                return;
            }

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Put, ComposeUrl(JavDbSessionConfigApi,
                    new NameValueCollection()));
                request.Content = JsonContent.Create(new JavDbSessionConfigRequest
                {
                    Username = username,
                    Password = password,
                    Cookie = cookie,
                    Authorization = authorization,
                    ApiUrl = apiUrl
                });

                AddGeneralHeaders(request);

                if (!string.IsNullOrWhiteSpace(token))
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", token);

                using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var result = await ReadDataAsync<JavDbSessionConfigResult>(response, cancellationToken)
                    .ConfigureAwait(false);

                var nextSync = result.Authenticated
                    ? DateTimeOffset.UtcNow.Add(JavDbSessionAuthenticatedSyncInterval)
                    : DateTimeOffset.UtcNow.Add(JavDbSessionRetryInterval);
                if (!hasCredentials && !result.Configured)
                    nextSync = DateTimeOffset.MaxValue;

                MarkJavDbSessionSyncComplete(server, token, signature, nextSync);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Login configuration must not block anonymous metadata requests.
                MarkJavDbSessionSyncComplete(server, token, signature,
                    DateTimeOffset.UtcNow.Add(JavDbSessionRetryInterval));
            }
        }
        finally
        {
            JavDbSessionSyncLock.Release();
        }
    }

    private static async Task ConfigureServerAsync(CancellationToken cancellationToken)
    {
        await ConfigureFlareSolverrAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureDomainsAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureJavDbSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (string Signature, Dictionary<string, string[]> Providers,
        Dictionary<string, string> PrimaryDomains) GetDomainsConfiguration()
    {
        var providers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["JavDB"] = SplitBackupDomains(Plugin.Instance.Configuration.JavDbBackupDomains),
            ["MissAV"] = SplitBackupDomains(Plugin.Instance.Configuration.MissAvBackupDomains)
        };
        var primaryDomains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["JavDB"] = Plugin.Instance.Configuration.JavDbPrimaryDomain?.Trim() ?? string.Empty
        };
        var signature = string.Join(";", providers.Select(provider =>
                              $"{provider.Key}={string.Join(",", provider.Value)}"))
                          + ";primary="
                          + string.Join(";", primaryDomains.Select(domain => $"{domain.Key}={domain.Value}"));
        return (signature, providers, primaryDomains);
    }

    private static string[] SplitBackupDomains(string value)
    {
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsDomainsSyncCurrent(string server, string token, string signature)
    {
        lock (DomainsSyncStateLock)
        {
            return _domainsSyncInitialized &&
                   _lastDomainsServer == server &&
                   _lastDomainsToken == token &&
                   _lastDomainsSignature == signature &&
                   DateTimeOffset.UtcNow - _lastDomainsSync < DomainsSyncInterval;
        }
    }

    private static void MarkDomainsSyncComplete(string server, string token, string signature)
    {
        lock (DomainsSyncStateLock)
        {
            _domainsSyncInitialized = true;
            _lastDomainsServer = server;
            _lastDomainsToken = token;
            _lastDomainsSignature = signature;
            _lastDomainsSync = DateTimeOffset.UtcNow;
        }
    }

    private static bool IsDomainsSyncInitialized
    {
        get
        {
            lock (DomainsSyncStateLock)
                return _domainsSyncInitialized;
        }
    }

    private static bool IsFlareSolverrSyncCurrent(string server, string token, string endpoint)
    {
        lock (FlareSolverrSyncStateLock)
        {
            return _lastFlareSolverrServer == server &&
                   _lastFlareSolverrToken == token &&
                   _lastFlareSolverrEndpoint == endpoint &&
                   DateTimeOffset.UtcNow - _lastFlareSolverrSync < FlareSolverrSyncInterval;
        }
    }

    private static string GetJavDbSessionSignature(string primaryDomain, string apiUrl, string username,
        string password, string cookie, string authorization)
    {
        var material = string.Join("\n", primaryDomain, apiUrl, username, password, cookie, authorization);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static bool IsJavDbSessionSyncCurrent(string server, string token, string signature)
    {
        lock (JavDbSessionSyncStateLock)
        {
            return _javDbSessionSyncInitialized &&
                   _lastJavDbSessionServer == server &&
                   _lastJavDbSessionToken == token &&
                   _lastJavDbSessionSignature == signature &&
                   DateTimeOffset.UtcNow < _javDbSessionNextSync;
        }
    }

    private static void MarkJavDbSessionSyncComplete(string server, string token, string signature,
        DateTimeOffset nextSync)
    {
        lock (JavDbSessionSyncStateLock)
        {
            _javDbSessionSyncInitialized = true;
            _lastJavDbSessionServer = server;
            _lastJavDbSessionToken = token;
            _lastJavDbSessionSignature = signature;
            _javDbSessionNextSync = nextSync;
        }
    }

    private static bool IsJavDbSessionSyncInitialized
    {
        get
        {
            lock (JavDbSessionSyncStateLock)
                return _javDbSessionSyncInitialized;
        }
    }

    private static async Task<T> GetDataAsync<T>(string url, bool requireAuth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (requireAuth)
            await ConfigureServerAsync(cancellationToken).ConfigureAwait(false);

        var request = new HttpRequestMessage(HttpMethod.Get, url);

        AddGeneralHeaders(request);

        // Set API Authorization Token.
        if (requireAuth && !string.IsNullOrWhiteSpace(Plugin.Instance.Configuration.Token))
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", Plugin.Instance.Configuration.Token);

        using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadDataAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {

        ResponseInfo<T> apiResponse;
        try
        {
            apiResponse = await response.Content!
                .ReadFromJsonAsync<ResponseInfo<T>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException) when (!response.IsSuccessStatusCode)
        {
            throw new ApiRequestException(
                response.StatusCode,
                (int)response.StatusCode,
                response.ReasonPhrase ?? "HTTP request failed");
        }

        // When the status is unsuccessful, the API response contains error details.
        if (!response.IsSuccessStatusCode)
        {
            if (apiResponse?.Error == null)
            {
                throw new ApiRequestException(
                    response.StatusCode,
                    (int)response.StatusCode,
                    response.ReasonPhrase ?? "HTTP request failed");
            }

            throw new ApiRequestException(
                response.StatusCode,
                apiResponse.Error.Code,
                apiResponse.Error.Message);
        }

        // Note: data field must not be null if there are no errors.
        if (apiResponse is null || apiResponse.Data is null)
            throw new Exception("Response data field is null");

        return apiResponse.Data;
    }

    private static void AddGeneralHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("User-Agent", DefaultUserAgent);
    }

    #region Http

    private static readonly HttpClient HttpClient;
    private static readonly SemaphoreSlim FlareSolverrSyncLock = new(1, 1);
    private static readonly SemaphoreSlim DomainsSyncLock = new(1, 1);
    private static readonly SemaphoreSlim JavDbSessionSyncLock = new(1, 1);
    private static readonly object FlareSolverrSyncStateLock = new();
    private static readonly object DomainsSyncStateLock = new();
    private static readonly object JavDbSessionSyncStateLock = new();
    private static readonly TimeSpan FlareSolverrSyncInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DomainsSyncInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan JavDbSessionAuthenticatedSyncInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan JavDbSessionRetryInterval = TimeSpan.FromMinutes(5);
    private static string _lastFlareSolverrServer;
    private static string _lastFlareSolverrToken;
    private static string _lastFlareSolverrEndpoint;
    private static DateTimeOffset _lastFlareSolverrSync = DateTimeOffset.MinValue;
    private static bool _domainsSyncInitialized;
    private static string _lastDomainsServer;
    private static string _lastDomainsToken;
    private static string _lastDomainsSignature;
    private static DateTimeOffset _lastDomainsSync = DateTimeOffset.MinValue;
    private static bool _javDbSessionSyncInitialized;
    private static string _lastJavDbSessionServer;
    private static string _lastJavDbSessionToken;
    private static string _lastJavDbSessionSignature;
    private static DateTimeOffset _javDbSessionNextSync = DateTimeOffset.MinValue;
    private static string DefaultUserAgent => $"{Plugin.ProviderName}/{Plugin.Instance.Version}";

    static ApiClient()
    {
        HttpClient = new HttpClient(new SocketsHttpHandler
        {
            // Connect Timeout.
            ConnectTimeout = TimeSpan.FromSeconds(30),

            // TCP Keep Alive.
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),

            // Connection Pooling.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90)
        });
    }

    #endregion

    private sealed class FlareSolverrConfigRequest
    {
        [JsonPropertyName("endpoint")]
        public string Endpoint { get; set; }
    }

    private sealed class FlareSolverrConfigResult
    {
        [JsonPropertyName("endpoint")]
        public string Endpoint { get; set; }

        [JsonPropertyName("providers")]
        public string[] Providers { get; set; }
    }

    private sealed class DomainsConfigRequest
    {
        [JsonPropertyName("providers")]
        public Dictionary<string, string[]> Providers { get; set; }

        [JsonPropertyName("primary_domains")]
        public Dictionary<string, string> PrimaryDomains { get; set; }
    }

    private sealed class DomainsConfigResult
    {
        [JsonPropertyName("providers")]
        public Dictionary<string, string[]> Providers { get; set; }

        [JsonPropertyName("primary_domains")]
        public Dictionary<string, string> PrimaryDomains { get; set; }
    }

    private sealed class JavDbSessionConfigRequest
    {
        [JsonPropertyName("username")]
        public string Username { get; set; }

        [JsonPropertyName("password")]
        public string Password { get; set; }

        [JsonPropertyName("cookie")]
        public string Cookie { get; set; }

        [JsonPropertyName("authorization")]
        public string Authorization { get; set; }

        [JsonPropertyName("api_url")]
        public string ApiUrl { get; set; }
    }

    private sealed class JavDbSessionConfigResult
    {
        [JsonPropertyName("configured")]
        public bool Configured { get; set; }

        [JsonPropertyName("authenticated")]
        public bool Authenticated { get; set; }

        [JsonPropertyName("mode")]
        public string Mode { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }
    }
}
