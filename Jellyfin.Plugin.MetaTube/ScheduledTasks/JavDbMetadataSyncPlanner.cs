using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

internal enum JavDbMetadataSyncMode
{
    Full,
    Incremental,
    Skipped
}

internal sealed class JavDbMetadataSyncPlan
{
    public JavDbMetadataSyncMode Mode { get; set; }

    public string ModeReason { get; set; } = string.Empty;

    public int TotalItemCount { get; set; }

    public List<BaseItem> PendingItems { get; set; } = new();

    public List<BaseItem> GenreCompletedItems { get; set; } = new();

    public int SkippedWithoutNumberCount { get; set; }

    public int SkippedByStateCount { get; set; }

    public int SkippedByGenreCount { get; set; }

    public int PendingByEmptyGenreCount { get; set; }

    public int SkippedItemCount =>
        SkippedWithoutNumberCount + SkippedByStateCount + SkippedByGenreCount;

    public string ModeLabel => Mode switch
    {
        JavDbMetadataSyncMode.Full => "全量",
        JavDbMetadataSyncMode.Incremental => "增量",
        _ => "跳过"
    };
}

internal static class JavDbMetadataSyncPlanner
{
    public static JavDbMetadataSyncPlan Create(
        IEnumerable<BaseItem> items,
        JavDbMetadataState state,
        Func<BaseItem, string> keywordSelector,
        bool useGenreCompletionMarker)
    {
        state ??= new JavDbMetadataState();
        var allItems = (items ?? Enumerable.Empty<BaseItem>())
            .Where(item => item != null)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        var index = new StateIndex(state);
        var mode = state.Successes.Count == 0
            ? JavDbMetadataSyncMode.Full
            : JavDbMetadataSyncMode.Incremental;
        var candidates = allItems
            .Select(item => new Candidate
            {
                Item = item,
                Keyword = keywordSelector(item)?.Trim() ?? string.Empty
            })
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Keyword))
            .ToList();
        var useGenreMarker = mode == JavDbMetadataSyncMode.Incremental &&
                             useGenreCompletionMarker;
        var genreCompletedCandidates = useGenreMarker
            ? candidates
                .Where(candidate => HasGenres(candidate.Item))
                .ToList()
            : new List<Candidate>();
        var genreCompletedIds = genreCompletedCandidates
            .Select(candidate => candidate.Item.Id)
            .ToHashSet();
        var pendingCandidates = mode == JavDbMetadataSyncMode.Full
            ? candidates
            : useGenreMarker
                ? candidates
                    .Where(candidate =>
                        !genreCompletedIds.Contains(candidate.Item.Id) &&
                        !index.IsManuallyCompleted(candidate.Item, candidate.Keyword))
                    .ToList()
                : candidates
                    .Where(candidate => index.IsFailed(candidate.Item, candidate.Keyword) ||
                                        !index.IsSuccessful(candidate.Item, candidate.Keyword))
                    .ToList();

        return new JavDbMetadataSyncPlan
        {
            Mode = mode,
            ModeReason = mode == JavDbMetadataSyncMode.Full
                ? "没有成功记录，执行全量替换"
                : useGenreMarker
                    ? "存在成功记录，按流派空值扫描待补影片"
                    : "存在成功记录，仅处理新影片和失败记录",
            TotalItemCount = allItems.Count,
            PendingItems = pendingCandidates
                .Select(candidate => candidate.Item)
                .ToList(),
            GenreCompletedItems = genreCompletedCandidates
                .Select(candidate => candidate.Item)
                .ToList(),
            SkippedWithoutNumberCount = allItems.Count - candidates.Count,
            SkippedByGenreCount = genreCompletedCandidates.Count,
            SkippedByStateCount = Math.Max(
                0,
                candidates.Count - pendingCandidates.Count - genreCompletedCandidates.Count),
            PendingByEmptyGenreCount = useGenreMarker
                ? pendingCandidates.Count(candidate => !HasGenres(candidate.Item))
                : 0
        };
    }

    private static bool HasGenres(BaseItem item)
    {
        return item?.Genres?.Any(genre => !string.IsNullOrWhiteSpace(genre)) == true;
    }

    private sealed class Candidate
    {
        public BaseItem Item { get; init; }

        public string Keyword { get; init; } = string.Empty;
    }

    private sealed class StateIndex
    {
        private readonly HashSet<string> _failedItemIds;
        private readonly HashSet<string> _failedKeywords;
        private readonly HashSet<string> _successfulItemIds;
        private readonly HashSet<string> _successfulKeywords;
        private readonly HashSet<string> _manuallyCompletedItemIds;
        private readonly HashSet<string> _manuallyCompletedKeywords;

        public StateIndex(JavDbMetadataState state)
        {
            _failedItemIds = BuildSet(state?.Failures, record => record.ItemId);
            _failedKeywords = BuildSet(state?.Failures, record => record.Keyword)
                .Concat(BuildSet(state?.Failures, record => record.SourceKeyword))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _successfulItemIds = BuildSet(state?.Successes, record => record.ItemId);
            _successfulKeywords = BuildSet(state?.Successes, record => record.Keyword)
                .Concat(BuildSet(state?.Successes, record => record.SourceKeyword))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var manualCompletions = (state?.Successes ?? Enumerable.Empty<JavDbMetadataRecord>())
                .Where(record => string.Equals(
                    record.FailureKind, "manual_complete", StringComparison.OrdinalIgnoreCase))
                .ToList();
            _manuallyCompletedItemIds = BuildSet(manualCompletions, record => record.ItemId);
            _manuallyCompletedKeywords = BuildSet(manualCompletions, record => record.Keyword)
                .Concat(BuildSet(manualCompletions, record => record.SourceKeyword))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public bool IsFailed(BaseItem item, string keyword)
        {
            return _failedItemIds.Contains(item.Id.ToString("N")) ||
                   (!string.IsNullOrWhiteSpace(keyword) && _failedKeywords.Contains(keyword));
        }

        public bool IsSuccessful(BaseItem item, string keyword)
        {
            return _successfulItemIds.Contains(item.Id.ToString("N")) ||
                   (!string.IsNullOrWhiteSpace(keyword) && _successfulKeywords.Contains(keyword));
        }

        public bool IsManuallyCompleted(BaseItem item, string keyword)
        {
            return _manuallyCompletedItemIds.Contains(item.Id.ToString("N")) ||
                   (!string.IsNullOrWhiteSpace(keyword) && _manuallyCompletedKeywords.Contains(keyword));
        }

        private static HashSet<string> BuildSet(
            IEnumerable<JavDbMetadataRecord> records,
            Func<JavDbMetadataRecord, string> selector)
        {
            return (records ?? Enumerable.Empty<JavDbMetadataRecord>())
                .Select(selector)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}
