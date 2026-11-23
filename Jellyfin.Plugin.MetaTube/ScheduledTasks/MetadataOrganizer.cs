using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
#if __EMBY__
using MediaBrowser.Model.Logging;
#else
using MediaBrowser.Controller.Sorting;
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

internal static class MetadataOrganizer
{
    private const string ChineseSubtitle = "中文字幕";

    public static async Task<int> OrganizeAsync(
        IEnumerable<BaseItem> items,
        ILibraryManager libraryManager,
        ILogger logger,
        CancellationToken cancellationToken,
        IProgress<double> progress = null,
        double progressStart = 0,
        double progressEnd = 100)
    {
        var itemList = (items ?? Enumerable.Empty<BaseItem>())
            .Where(IsMetaTubeItem)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        var updated = 0;

        for (var index = 0; index < itemList.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(itemList.Count == 0
                ? progressEnd
                : progressStart + (progressEnd - progressStart) * index / itemList.Count);

            if (await OrganizeItemAsync(itemList[index], libraryManager, logger, cancellationToken)
                    .ConfigureAwait(false))
            {
                updated++;
            }
        }

        progress?.Report(progressEnd);
        if (itemList.Count > 0)
        {
            logger.Info("元数据整理完成 | 扫描={0} | 更新={1}", itemList.Count, updated);
        }

        return updated;
    }

    public static async Task<bool> OrganizeItemAsync(
        BaseItem item,
        ILibraryManager libraryManager,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!IsMetaTubeItem(item))
            return false;

        var genres = item.Genres?.ToList() ?? new List<string>();

        try
        {
            switch (HasEmbeddedChineseSubtitle(item.FileNameWithoutExtension) ||
                    HasExternalChineseSubtitle(item.Path))
            {
                case true when !genres.Contains(ChineseSubtitle):
                {
                    genres.Add(ChineseSubtitle);
                    if (Plugin.Instance.Configuration.EnableBadges)
                    {
                        await SetPrimaryImage(item, Plugin.Instance.Configuration.BadgeUrl, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;
                }
                case false when genres.Contains(ChineseSubtitle):
                {
                    genres.RemoveAll(value => value.Equals(ChineseSubtitle));
                    if (Plugin.Instance.Configuration.EnableBadges)
                    {
                        await SetPrimaryImage(item, string.Empty, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }
            }
        }
        catch (Exception exception)
        {
            logger.Error("更新中文字幕流派失败 | 标题={0} | 错误={1}", item.Name, exception.Message);
        }

        var orderedGenres =
            (Plugin.Instance.Configuration.EnableGenreSubstitution
                ? Plugin.Instance.Configuration.GetGenreSubstitutionTable().Substitute(genres)
                : genres).Distinct().OrderByString(genre => genre).ToList();

        if (!orderedGenres.Any() ||
            (item.Genres?.SequenceEqual(orderedGenres, StringComparer.OrdinalIgnoreCase))
            .GetValueOrDefault(false))
        {
            return false;
        }

        var originalGenres = item.Genres;
        try
        {
            item.Genres = orderedGenres.ToArray();
            JavDbMetadataEventSuppressor.Suppress(item.Id, TimeSpan.FromSeconds(45));

#if __EMBY__
            libraryManager.UpdateItem(item, item, ItemUpdateType.MetadataEdit, null);
            await Task.CompletedTask.ConfigureAwait(false);
#else
            await libraryManager
                .UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);
#endif

            logger.Info("整理影片元数据 | 标题={0} | 流派={1}", item.Name, string.Join(", ", orderedGenres));
            return true;
        }
        catch (Exception exception)
        {
            item.Genres = originalGenres;
            logger.Error("整理影片元数据失败 | 标题={0} | 错误={1}", item.Name, exception.Message);
            return false;
        }
    }

    internal static bool IsMetaTubeItem(BaseItem item)
    {
        return item != null &&
               !string.IsNullOrWhiteSpace(item.GetPid(Plugin.ProviderId).Id);
    }

    private static bool HasTag(string filename, string tag)
    {
        var separator = new Regex(@"[-_\s]", RegexOptions.Compiled);
        return separator.Split(filename).Contains(tag, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasTag(string filename, params string[] tags)
    {
        return tags.Any(tag => HasTag(filename, tag));
    }

    private static bool HasEmbeddedChineseSubtitle(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return false;

        return filename.Contains(ChineseSubtitle) || HasTag(filename, "C", "UC", "ch");
    }

    private static bool HasExternalChineseSubtitle(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return HasExternalChineseSubtitle(
            Path.GetFileNameWithoutExtension(path),
            Directory.GetParent(path)?.GetFiles().Select(info => info.Name));
    }

    private static bool HasExternalChineseSubtitle(string basename, IEnumerable<string> files)
    {
        if (string.IsNullOrWhiteSpace(basename) || files == null)
            return false;

        var subtitlePattern = new Regex(
            @"\.(ch[ist]|zho?(-(cn|hk|sg|tw))?)\.(ass|srt|ssa|smi|sub|idx|psb|vtt)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        return files.Any(name => subtitlePattern.IsMatch(name) &&
                                 subtitlePattern.Replace(name, string.Empty)
                                     .Equals(basename, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task SetPrimaryImage(
        BaseItem item,
        string badge,
        CancellationToken cancellationToken)
    {
        var pid = item.GetPid(Plugin.ProviderId);
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
            return;
        if (PluginConfiguration.IsJavDbProvider(pid.Provider))
            return;

        var movieInfo = await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id, cancellationToken)
            .ConfigureAwait(false);
        item.SetImage(new ItemImageInfo
        {
            Path = ApiClient.GetPrimaryImageApiUrl(
                movieInfo.Provider, movieInfo.Id, pid.Position ?? -1, badge),
            Type = ImageType.Primary
        }, 0);
    }
}
