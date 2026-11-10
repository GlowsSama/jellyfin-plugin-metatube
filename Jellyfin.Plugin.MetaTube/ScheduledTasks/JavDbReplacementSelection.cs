using Jellyfin.Plugin.MetaTube.Configuration;

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

internal sealed class JavDbReplacementSelection
{
    public const string LegacySignature = "genres,tags,studios";

    public bool Genres { get; init; }

    public bool Tags { get; init; }

    public bool Studios { get; init; }

    public bool Actors { get; init; }

    public bool HasAny => Genres || Tags || Studios || Actors;

    public bool HasMetadata => Genres || Tags || Studios;

    public string Signature => string.Join(',', new[]
    {
        Genres ? "genres" : null,
        Tags ? "tags" : null,
        Studios ? "studios" : null,
        Actors ? "actors" : null
    }.Where(value => value != null));

    public static JavDbReplacementSelection FromConfiguration(PluginConfiguration configuration)
    {
        return new JavDbReplacementSelection
        {
            Genres = configuration?.ReplaceJavDbGenres ?? true,
            Tags = configuration?.ReplaceJavDbTags ?? true,
            Studios = configuration?.ReplaceJavDbStudios ?? true,
            Actors = configuration?.ReplaceJavDbActors ?? false
        };
    }
}
