using Jellyfin.Plugin.MetaTube.Configuration;

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

internal sealed class JavDbReplacementSelection
{
    public const string LegacySignature = "genres,tags,studios";

    public bool Genres { get; init; }

    public bool Tags { get; init; }

    public bool Studios { get; init; }

    public bool Rating { get; init; }

    public bool HasAny => Genres || Tags || Studios || Rating;

    public string Signature => string.Join(',', new[]
    {
        Genres ? "genres" : null,
        Tags ? "tags" : null,
        Studios ? "studios" : null,
        Rating ? "rating" : null
    }.Where(value => value != null));

    public static JavDbReplacementSelection FromConfiguration(PluginConfiguration configuration)
    {
        return new JavDbReplacementSelection
        {
            Genres = configuration?.ReplaceJavDbGenres ?? true,
            Tags = configuration?.ReplaceJavDbTags ?? true,
            Studios = configuration?.ReplaceJavDbStudios ?? true,
            Rating = configuration?.ReplaceJavDbRating ?? false
        };
    }
}
