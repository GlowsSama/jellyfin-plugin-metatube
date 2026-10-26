using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using MovieInfo = MediaBrowser.Controller.Providers.MovieInfo;
#if __EMBY__
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

#else
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.Providers;

#if __EMBY__
public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder, IHasMetadataFeatures
#else
public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
#endif
{
    private const string AvBase = "AVBASE";
    private const string Gfriends = "Gfriends";
    private const string Rating = "JP-18+";

    private static readonly string[] AvBaseSupportedProviderNames = { "DUGA", "FANZA", "Getchu", "MGS" };
    private static readonly Regex MediaCodePattern = new(
        @"(?<![A-Z0-9])(?<prefix>[A-Z][A-Z0-9]{1,15})[\s_-]*(?<number>\d{2,8})(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly ILibraryManager _libraryManager;

#if __EMBY__
    public MetadataFeatures[] Features => new[]
        { MetadataFeatures.Collections, MetadataFeatures.Adult, MetadataFeatures.RequiredSetup };

    public MovieProvider(ILogManager logManager, ILibraryManager libraryManager)
        : base(logManager.CreateLogger<MovieProvider>())
#else
    public MovieProvider(ILogger<MovieProvider> logger, ILibraryManager libraryManager) : base(logger)
#endif
    {
        _libraryManager = libraryManager;
    }

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info,
        CancellationToken cancellationToken)
    {
        var pid = info.GetPid(Plugin.ProviderId);
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            // Search movies and pick the first result.
            var firstResult = (await GetSearchResults(info, cancellationToken)).FirstOrDefault();
            if (firstResult != null) pid = firstResult.GetPid(Plugin.ProviderId);
        }

        Logger.Info("Get movie info: {0}", pid.ToString());

        var m = await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id, cancellationToken);

        // Preserve original title.
        var originalTitle = m.Title;

        // Convert to real actor names.
        if (Configuration.EnableRealActorNames)
            await ConvertToRealActorNames(m, cancellationToken);

        // Substitute title.
        if (Configuration.EnableTitleSubstitution)
            m.Title = Configuration.GetTitleSubstitutionTable().Substitute(m.Title);

        // Substitute actors.
        if (Configuration.EnableActorSubstitution)
            m.Actors = Configuration.GetActorSubstitutionTable().Substitute(m.Actors).ToArray();

        // Substitute genres.
        if (Configuration.EnableGenreSubstitution)
            m.Genres = Configuration.GetGenreSubstitutionTable().Substitute(m.Genres).ToArray();

        // Translate movie info.
        if (Configuration.TranslationMode != TranslationMode.Disabled)
            await TranslateMovieInfo(m, info.MetadataLanguage, cancellationToken);

        // Distinct and clean blank list
        m.Genres = m.Genres?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ?? Array.Empty<string>();
        m.Actors = m.Actors?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ?? Array.Empty<string>();
        m.PreviewImages = m.PreviewImages?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ??
                          Array.Empty<string>();

        // Build parameters.
        var parameters = new Dictionary<string, string>
        {
            { @"{provider}", m.Provider },
            { @"{id}", m.Id },
            { @"{number}", m.Number },
            { @"{title}", m.Title },
            { @"{series}", m.Series },
            { @"{maker}", m.Maker },
            { @"{label}", m.Label },
            { @"{director}", m.Director },
            { @"{actors}", m.Actors?.Any() == true ? string.Join(' ', m.Actors) : string.Empty },
            { @"{first_actor}", m.Actors?.FirstOrDefault() },
            { @"{year}", $"{m.ReleaseDate:yyyy}" },
            { @"{month}", $"{m.ReleaseDate:MM}" },
            { @"{date}", $"{m.ReleaseDate:yyyy-MM-dd}" }
        };

        var result = new MetadataResult<Movie>
        {
            Item = new Movie
            {
                Name = RenderTemplate(
                    Configuration.EnableTemplate
                        ? Configuration.NameTemplate
                        : PluginConfiguration.DefaultNameTemplate, parameters),
                Tagline = RenderTemplate(
                    Configuration.EnableTemplate
                        ? Configuration.TaglineTemplate
                        : PluginConfiguration.DefaultTaglineTemplate, parameters),
                OriginalTitle = originalTitle,
                Overview = m.Summary,
                OfficialRating = Rating,
                PremiereDate = m.ReleaseDate.GetValidDateTime(),
                ProductionYear = m.ReleaseDate.GetValidYear(),
                Genres = m.Genres?.Any() == true ? m.Genres : Array.Empty<string>()
            },
            HasMetadata = true
        };

        // Set provider id.
        result.Item.SetPid(Name, m.Provider, m.Id, pid.Position);

        // Set trailer url.
        var trailerUrl = !string.IsNullOrWhiteSpace(m.PreviewVideoUrl)
            ? m.PreviewVideoUrl
            : m.PreviewVideoHlsUrl;
        if (!string.IsNullOrWhiteSpace(trailerUrl))
            result.Item.SetTrailerUrl(trailerUrl);

        // Set community rating.
        if (Configuration.EnableRatings)
            result.Item.CommunityRating = m.Score > 0 ? (float)Math.Round(m.Score * 2, 1) : null;

        // Add collection.
        if (Configuration.EnableCollections && !string.IsNullOrWhiteSpace(m.Series))
        {
            result.Item.AddCollection(m.Series);
            Logger.Info("Add Collection for movie {0} [{1}]", pid.ToString(), m.Series);
        }

        // Add studio.
        if (!string.IsNullOrWhiteSpace(m.Maker))
            result.Item.AddStudio(m.Maker);

        // Add tag (series).
        if (!string.IsNullOrWhiteSpace(m.Series))
            result.Item.AddTag(m.Series);

        // Add tag (maker).
        if (!string.IsNullOrWhiteSpace(m.Maker))
            result.Item.AddTag(m.Maker);

        // Add tag (label).
        if (!string.IsNullOrWhiteSpace(m.Label))
            result.Item.AddTag(m.Label);

        // Add director.
        if (Configuration.EnableDirectors && !string.IsNullOrWhiteSpace(m.Director))
            result.AddPerson(new PersonInfo
            {
                Name = m.Director,
#if __EMBY__
                Type = PersonType.Director
#else
                Type = PersonKind.Director
#endif
            });

        // Add actors.
        foreach (var name in m.Actors ?? Enumerable.Empty<string>())
        {
            var actor = new PersonInfo
            {
                Name = name,
#if __EMBY__
                Type = PersonType.Actor,
#else
                Type = PersonKind.Actor,
#endif
            };
            await SetActorImageUrl(actor, cancellationToken);
            result.AddPerson(actor);
        }

        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo info,
        CancellationToken cancellationToken)
    {
        var pid = info.GetPid(Plugin.ProviderId);

        var searchResults = new List<MovieSearchResult>();
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            // Search movie by name.
            Logger.Info("Search for movie: {0}", info.Name);
            searchResults.AddRange(await ApiClient.SearchMovieAsync(info.Name, pid.Provider, cancellationToken));
        }
        else
        {
            // Exact search.
            Logger.Info("Search for movie: {0}", pid.ToString());
            searchResults.Add(await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id,
                pid.Update != true, cancellationToken));
        }

        var providerFilter = GetMovieProviderFilter(info);
        if (providerFilter.Any())
        {
            // Filter out mismatched results.
            searchResults.RemoveAll(m => !providerFilter.Contains(m.Provider, StringComparer.OrdinalIgnoreCase));
            // Reorder results by stable sort.
            var indexedProviderFilter = providerFilter.ToList();
            searchResults = searchResults.OrderBy(m =>
                indexedProviderFilter.FindIndex(s =>
                    s.Equals(m.Provider, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        else if (Configuration.EnableMovieProviderFilter || Configuration.EnableLibraryProviderFilter)
        {
            Logger.Warn("Movie provider filter enabled but no effective providers were configured");
        }

        var results = new List<RemoteSearchResult>();
        if (!searchResults.Any())
        {
            Logger.Warn("Movie not found or has been filtered: {0}", pid.Id);
            return results;
        }

        foreach (var m in searchResults)
        {
            var result = new RemoteSearchResult
            {
                Name = $"[{m.Provider}] {m.Number} {m.Title}",
                SearchProviderName = Name,
                PremiereDate = m.ReleaseDate.GetValidDateTime(),
                ProductionYear = m.ReleaseDate.GetValidYear(),
                ImageUrl = ApiClient.GetPrimaryImageApiUrl(m.Provider, m.Id, m.ThumbUrl, 1.0, true)
            };
            result.SetPid(Name, m.Provider, m.Id, pid.Position);
            results.Add(result);
        }

        return results;
    }

    private IReadOnlyList<string> GetMovieProviderFilter(MovieInfo info)
    {
        if (Configuration.EnableLibraryProviderFilter)
        {
            var rules = Configuration.GetLibraryProviderFilters();
            if (rules.Any())
            {
                var libraryName = GetContainingLibraryName(info, rules);
                var matchedRule = rules.FirstOrDefault(rule =>
                    LibraryNamesMatch(rule.LibraryName, libraryName));

                if (matchedRule != null)
                {
                    Logger.Info("Use movie provider filter for library {0}: {1}", libraryName,
                        string.Join(',', matchedRule.Providers));
                    return matchedRule.Providers;
                }

                if (!string.IsNullOrWhiteSpace(libraryName))
                {
                    Logger.Debug("No custom movie provider filter matched library {0}", libraryName);
                }
                else
                {
                    var configuredProviders = rules
                        .SelectMany(rule => rule.Providers)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (configuredProviders.Any())
                    {
                        Logger.Warn(
                            "Unable to resolve the media library for movie {0}; restricting results to configured library providers: {1}",
                            info.Name, string.Join(',', configuredProviders));
                        return RestrictToGlobalMovieProviders(configuredProviders);
                    }
                }
            }
        }

        return GetGlobalMovieProviderFilter();
    }

    private string GetContainingLibraryName(MovieInfo info,
        IReadOnlyList<LibraryProviderFilterRule> configuredRules)
    {
        try
        {
            var candidates = FindLibraryCandidates(info);
            if (!candidates.Any())
            {
                Logger.Warn("Unable to resolve library for movie {0} across {1} configured rules; using global provider filter",
                    info.Name, configuredRules.Count);
                return null;
            }

            var matches = new List<(string LibraryName, int Score, BaseItem Candidate)>();
            foreach (var candidate in candidates)
            {
                var score = GetLibraryCandidateMatchScore(candidate, info);
                foreach (var folder in _libraryManager.GetCollectionFolders(candidate))
                {
                    if (!string.IsNullOrWhiteSpace(folder.Name))
                        matches.Add((folder.Name, score, candidate));
                }
            }

            if (!matches.Any())
            {
                Logger.Warn("Unable to resolve library for movie {0} across {1} configured rules; using global provider filter",
                    info.Name, configuredRules.Count);
                return null;
            }

            // Provider IDs identify the exact existing item even when it is present in multiple libraries.
            if (info.ProviderIds?.Any() == true)
            {
                var providerMatches = matches
                    .Where(match => ProviderIdsMatch(match.Candidate, info))
                    .Select(match => match.LibraryName)
                    .GroupBy(GetLibraryNameKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

                if (providerMatches.Count == 1)
                {
                    Logger.Debug("Resolved library {0} for movie {1} by provider ID", providerMatches[0],
                        info.Name);
                    return providerMatches[0];
                }
            }

            var bestScore = matches.Max(match => match.Score);
            var bestMatches = matches.Where(match => match.Score == bestScore).ToList();
            var bestLibraryNames = bestMatches
                .Select(match => match.LibraryName)
                .GroupBy(GetLibraryNameKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (bestLibraryNames.Count == 1)
            {
                Logger.Debug("Resolved library {0} for movie {1} with match score {2}", bestLibraryNames[0],
                    info.Name, bestScore);
                return bestLibraryNames[0];
            }

            var configuredLibraryNames = configuredRules
                .Select(rule => GetLibraryNameKey(rule.LibraryName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var configuredBestMatches = bestMatches
                .Where(match => configuredLibraryNames.Contains(GetLibraryNameKey(match.LibraryName)))
                .Select(match => match.LibraryName)
                .GroupBy(GetLibraryNameKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (configuredBestMatches.Count == 1)
            {
                Logger.Debug("Resolved configured library {0} for movie {1} with match score {2}",
                    configuredBestMatches[0], info.Name, bestScore);
                return configuredBestMatches[0];
            }

            var allLibraryNames = matches
                .Select(match => match.LibraryName)
                .GroupBy(GetLibraryNameKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (allLibraryNames.Count == 1)
                return allLibraryNames[0];

            Logger.Warn("Movie {0} matches multiple libraries ({1}); using global provider filter",
                info.Name, string.Join(", ", allLibraryNames));
        }
        catch (Exception exception)
        {
            Logger.Warn("Unable to resolve media library for movie {0}: {1}", info.Name, exception.Message);
        }

        return null;
    }

    private List<BaseItem> FindLibraryCandidates(MovieInfo info)
    {
        var candidates = new Dictionary<Guid, BaseItem>();
        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            AddLibraryCandidates(candidates, new InternalItemsQuery
            {
                Name = info.Name.Trim(),
                Recursive = true,
                MediaTypes = new[] { MediaType.Video },
#if __EMBY__
                IncludeItemTypes = new[] { nameof(Movie) }
#else
                IncludeItemTypes = new[] { BaseItemKind.Movie }
#endif
            }, info);
        }

        foreach (var searchTerm in GetLibrarySearchTerms(info.Name))
        {
            AddLibraryCandidates(candidates, new InternalItemsQuery
            {
                SearchTerm = searchTerm,
                Recursive = true,
                MediaTypes = new[] { MediaType.Video },
#if __EMBY__
                IncludeItemTypes = new[] { nameof(Movie) }
#else
                IncludeItemTypes = new[] { BaseItemKind.Movie }
#endif
            }, info);
        }

        return candidates.Values.ToList();
    }

    private void AddLibraryCandidates(IDictionary<Guid, BaseItem> candidates, InternalItemsQuery query,
        MovieInfo info)
    {
        foreach (var candidate in _libraryManager.GetItemList(query))
        {
            if (GetLibraryCandidateMatchScore(candidate, info) > 0)
                candidates[candidate.Id] = candidate;
        }
    }

    private static IEnumerable<string> GetLibrarySearchTerms(string value)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(value))
            terms.Add(value.Trim());

        foreach (Match match in MediaCodePattern.Matches(value ?? string.Empty))
        {
            terms.Add(match.Value);
            terms.Add($"{match.Groups["prefix"].Value}{match.Groups["number"].Value}");
        }

        return terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static int GetLibraryCandidateMatchScore(BaseItem candidate, MovieInfo info)
    {
        var score = 0;
        var lookupCodes = ExtractMediaCodes(info.Name);
        var candidateCodes = ExtractMediaCodes(candidate.Name)
            .Concat(ExtractMediaCodes(candidate.OriginalTitle))
            .Concat(ExtractMediaCodes(candidate.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (lookupCodes.Overlaps(candidateCodes))
            score += 100;

        var lookupName = NormalizeLookupText(info.Name);
        var candidateNames = new[]
            {
                candidate.Name,
                candidate.OriginalTitle,
                candidate.Path == null ? null : System.IO.Path.GetFileNameWithoutExtension(candidate.Path)
            }
            .Select(NormalizeLookupText)
            .Where(name => name.Any())
            .ToList();

        if (candidateNames.Any(name => name.Equals(lookupName, StringComparison.OrdinalIgnoreCase)))
            score += 80;
        else if (lookupName.Length >= 4 &&
                 candidateNames.Any(name => name.Contains(lookupName, StringComparison.OrdinalIgnoreCase) ||
                                            lookupName.Contains(name, StringComparison.OrdinalIgnoreCase)))
            score += 40;

        if (ProviderIdsMatch(candidate, info))
            score += 60;

        if (info.Year.HasValue && candidate.ProductionYear == info.Year)
            score += 10;

        return score;
    }

    private static HashSet<string> ExtractMediaCodes(string value)
    {
        return MediaCodePattern.Matches(value ?? string.Empty)
            .Cast<Match>()
            .Select(match => $"{match.Groups["prefix"].Value}{match.Groups["number"].Value}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeLookupText(string value)
    {
        var normalized = PluginConfiguration.NormalizeLibraryName(value);
        return new string(normalized
            .Where(character => char.IsLetterOrDigit(character))
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static bool ProviderIdsMatch(BaseItem candidate, MovieInfo info)
    {
        if (info.ProviderIds?.Any() != true)
            return false;

        return info.ProviderIds.Any(lookupProvider =>
            !string.IsNullOrWhiteSpace(lookupProvider.Value) &&
            candidate.ProviderIds?.TryGetValue(lookupProvider.Key, out var candidateProviderId) == true &&
            candidateProviderId.Equals(lookupProvider.Value, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<string> GetGlobalMovieProviderFilter()
    {
        return Configuration.EnableMovieProviderFilter
            ? Configuration.GetMovieProviderFilter() ?? new List<string>()
            : Array.Empty<string>();
    }

    private IReadOnlyList<string> RestrictToGlobalMovieProviders(IReadOnlyList<string> libraryProviders)
    {
        if (!Configuration.EnableMovieProviderFilter)
            return libraryProviders;

        var globalProviders = GetGlobalMovieProviderFilter();
        if (!globalProviders.Any())
            return libraryProviders;

        return libraryProviders
            .Where(provider => globalProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool LibraryNamesMatch(string left, string right)
    {
        var leftKey = GetLibraryNameKey(left);
        var rightKey = GetLibraryNameKey(right);
        return leftKey.Any() && rightKey.Any() &&
               leftKey.Equals(rightKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetLibraryNameKey(string value)
    {
        var key = NormalizeLookupText(value);
        return key.Any() ? key : PluginConfiguration.NormalizeLibraryName(value);
    }

    private async Task SetActorImageUrl(PersonInfo actor, CancellationToken cancellationToken)
    {
        try
        {
            var results = await ApiClient.SearchActorAsync(actor.Name, cancellationToken);
            if (results?.Any() != true)
            {
                Logger.Warn("Actor not found: {0}", actor.Name);
                return;
            }

            // Use the first result as the primary actor selection.
            var firstResult = results.First();
            if (firstResult.Images?.Any() == true)
            {
                actor.ImageUrl = ApiClient.GetPrimaryImageApiUrl(
                    firstResult.Provider, firstResult.Id, firstResult.Images.First(), 0.5, true);
                actor.SetPid(Name, firstResult.Provider, firstResult.Id);
            }

            // Use the Gfriends to update the actor profile image, if any.
            foreach (var result in results.Where(result => result.Provider == Gfriends && result.Images?.Any() == true))
            {
                actor.ImageUrl = ApiClient.GetPrimaryImageApiUrl(
                    result.Provider, result.Id, result.Images.First(), 0.5, true);
            }
        }
        catch (Exception e)
        {
            Logger.Error("Get actor image error: {0} ({1})", actor.Name, e.Message);
        }
    }

    private async Task ConvertToRealActorNames(MovieSearchResult m, CancellationToken cancellationToken)
    {
        if (!AvBaseSupportedProviderNames.Contains(m.Provider, StringComparer.OrdinalIgnoreCase)) return;

        try
        {
            var searchResults = await ApiClient.SearchMovieAsync(m.Id, AvBase, cancellationToken);
            if (searchResults?.Any() != true)
            {
                Logger.Warn("Movie not found on AVBASE: {0}", m.Id);
                return;
            }

            foreach (var result in searchResults)
            {
                var similarity = CalculateTitleSimilarity(m, result);

                Logger.Info("Calculate movie title similarity for {0} ({1}) and {2} ({3}): {4:0.00%}",
                    m.Id, m.Provider, result.Id, result.Provider, similarity);

                if (similarity >= 0.8)
                {
                    if (result.Actors?.Any() == true)
                        m.Actors = result.Actors;
                    return;
                }
            }

            Logger.Warn("No matching movie found on AVBASE for {0}", m.Id);
        }
        catch (Exception e)
        {
            Logger.Error("Convert to real actor names error: {0} ({1})", m.Number, e.Message);
        }
    }

    private static double CalculateTitleSimilarity(MovieSearchResult source, MovieSearchResult target)
    {
        var sourceKey = Normalize(source.Number + source.Title);
        var targetKey = Normalize(target.Number + target.Title);

        if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(targetKey))
            return 0.0;

        var distance = Levenshtein.Distance(sourceKey, targetKey);
        var avgLength = (sourceKey.Length + targetKey.Length) / 2.0;
        var similarity = 1.0 - distance / avgLength;

        return Math.Clamp(similarity, 0.0, 1.0);

        string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;

            s = s.ToLowerInvariant();
            s = Regex.Replace(s, @"[\s\[\]\(\)【】（）]", "");
            return s.Trim();
        }
    }

    private async Task TranslateMovieInfo(Metadata.MovieInfo m, string language, CancellationToken cancellationToken)
    {
        try
        {
            Logger.Info("Translate movie info language: {0} => {1}", m.Number, language);
            await TranslationHelper.TranslateAsync(m, language, cancellationToken);
        }
        catch (Exception e)
        {
            Logger.Error("Translate error: {0}", e.Message);
        }
    }

    private static string RenderTemplate(string template, Dictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var sb = parameters.Where(kvp => template.Contains(kvp.Key))
            .Aggregate(new StringBuilder(template),
                (sb, kvp) => sb.Replace(kvp.Key, kvp.Value));

        return sb.ToString().Trim();
    }
}
