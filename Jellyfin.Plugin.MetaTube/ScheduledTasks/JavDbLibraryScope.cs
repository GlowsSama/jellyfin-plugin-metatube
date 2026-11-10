using Jellyfin.Plugin.MetaTube.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

internal sealed class JavDbLibraryScopeResult
{
    public IReadOnlyList<BaseItem> Items { get; init; } = Array.Empty<BaseItem>();

    public int SkippedItemCount { get; init; }

    public string Description { get; init; } = string.Empty;
}

internal static class JavDbLibraryScope
{
    public static JavDbLibraryScopeResult Filter(
        ILibraryManager libraryManager,
        IEnumerable<BaseItem> items,
        IReadOnlyList<LibraryProviderFilterRule> rules)
    {
        var allItems = (items ?? Enumerable.Empty<BaseItem>())
            .Where(item => item != null)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        var configuredRules = (rules ?? Array.Empty<LibraryProviderFilterRule>())
            .Where(rule => rule != null)
            .ToList();
        var libraryKeys = configuredRules
            .Select(rule => PluginConfiguration.NormalizeLibraryLookupName(rule.LibraryName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (libraryKeys.Count == 0)
        {
            return new JavDbLibraryScopeResult
            {
                Items = allItems,
                Description = "未配置媒体库规则（全库）"
            };
        }

        var scopedItems = new List<BaseItem>();
        foreach (var item in allItems)
        {
            if (BelongsToConfiguredLibrary(libraryManager, item, libraryKeys))
            {
                scopedItems.Add(item);
            }
        }

        return new JavDbLibraryScopeResult
        {
            Items = scopedItems,
            SkippedItemCount = allItems.Count - scopedItems.Count,
            Description = string.Join(", ", configuredRules
                .Select(rule => rule.LibraryName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase))
        };
    }

    private static bool BelongsToConfiguredLibrary(
        ILibraryManager libraryManager,
        BaseItem item,
        IReadOnlySet<string> libraryKeys)
    {
        try
        {
            var collectionFolders = libraryManager.GetCollectionFolders(item);
            return collectionFolders != null &&
                   collectionFolders.Any(folder =>
                       libraryKeys.Contains(PluginConfiguration.NormalizeLibraryLookupName(folder?.Name)));
        }
        catch
        {
            // An unresolved item is outside the configured scope. Maintenance tasks must
            // never fall back to all libraries when a scope rule exists.
            return false;
        }
    }
}
