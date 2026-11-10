using Jellyfin.Plugin.MetaTube.Configuration;

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

internal sealed class JavDbReplacementSelection
{
    public const string LegacySignature = "genres,tags,studios";

    public bool Genres { get; init; }

    public bool Tags { get; init; }

    public bool Studios { get; init; }

    public bool Actors { get; init; }

    public bool Directors { get; init; }

    public bool Rating { get; init; }

    public bool ReleaseDate { get; init; }

    public bool HasAny => Genres || Tags || Studios || Actors || Directors || Rating || ReleaseDate;

    public bool HasMetadata => Genres || Tags || Studios || Rating || ReleaseDate;

    public bool HasPeople => Actors || Directors;

    public string Signature => string.Join(',', new[]
    {
        Genres ? "genres" : null,
        Tags ? "tags" : null,
        Studios ? "studios" : null,
        Actors ? "actors" : null,
        Directors ? "directors" : null,
        Rating ? "rating" : null,
        ReleaseDate ? "releaseDate" : null
    }.Where(value => value != null));

    public static JavDbReplacementSelection FromConfiguration(PluginConfiguration configuration)
    {
        return new JavDbReplacementSelection
        {
            Genres = configuration?.ReplaceJavDbGenres ?? true,
            Tags = configuration?.ReplaceJavDbTags ?? true,
            Studios = configuration?.ReplaceJavDbStudios ?? true,
            Actors = configuration?.ReplaceJavDbActors ?? false,
            Directors = configuration?.ReplaceJavDbDirectors ?? false,
            Rating = configuration?.ReplaceJavDbRating ?? false,
            ReleaseDate = configuration?.ReplaceJavDbReleaseDate ?? false
        };
    }
}
