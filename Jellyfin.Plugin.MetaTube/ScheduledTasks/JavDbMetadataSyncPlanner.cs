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

    public int SkippedWithoutNumberCount { get; set; }

    public int SkippedByStateCount { get; set; }

    public int SkippedItemCount => SkippedWithoutNumberCount + SkippedByStateCount;

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
        Func<BaseItem, string> keywordSelector)
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
            .Select(item => new
            {
                Item = item,
                Keyword = keywordSelector(item)?.Trim() ?? string.Empty
            })
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Keyword))
            .ToList();
        var pendingItems = candidates
            .Where(candidate => index.IsFailed(candidate.Item, candidate.Keyword) ||
                                !index.IsSuccessful(candidate.Item, candidate.Keyword))
            .Select(candidate => candidate.Item)
            .ToList();

        return new JavDbMetadataSyncPlan
        {
            Mode = mode,
            ModeReason = mode == JavDbMetadataSyncMode.Full
                ? "没有成功记录，执行全量替换"
                : "存在成功记录，仅处理新影片和失败记录",
            TotalItemCount = allItems.Count,
            PendingItems = pendingItems,
            SkippedWithoutNumberCount = allItems.Count - candidates.Count,
            SkippedByStateCount = candidates.Count - pendingItems.Count
        };
    }

    private sealed class StateIndex
    {
        private readonly HashSet<string> _failedItemIds;
        private readonly HashSet<string> _failedKeywords;
        private readonly HashSet<string> _successfulItemIds;
        private readonly HashSet<string> _successfulKeywords;

        public StateIndex(JavDbMetadataState state)
        {
            _failedItemIds = BuildSet(state?.Failures, record => record.ItemId);
            _failedKeywords = BuildSet(state?.Failures, record => record.Keyword);
            _successfulItemIds = BuildSet(state?.Successes, record => record.ItemId);
            _successfulKeywords = BuildSet(state?.Successes, record => record.Keyword);
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
