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

    public List<BaseItem> CompletedMetadataItems { get; set; } = new();

    public int SkippedWithoutNumberCount { get; set; }

    public int SkippedByStateCount { get; set; }

    public int SkippedByCompletedMetadataCount { get; set; }

    public int PendingByMissingMetadataCount { get; set; }

    public int SkippedItemCount =>
        SkippedWithoutNumberCount + SkippedByStateCount + SkippedByCompletedMetadataCount;

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
        JavDbReplacementSelection selection)
    {
        state ??= new JavDbMetadataState();
        selection ??= new JavDbReplacementSelection();
        var allItems = (items ?? Enumerable.Empty<BaseItem>())
            .Where(item => item != null)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        var index = new StateIndex(state);
        var candidates = allItems
            .Select(item => new Candidate
            {
                Item = item,
                Keyword = keywordSelector(item)?.Trim() ?? string.Empty
            })
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Keyword))
            .ToList();

        JavDbMetadataSyncMode mode;
        string modeReason;
        List<Candidate> completedMetadataCandidates;
        List<Candidate> pendingCandidates;

        if (selection.Genres)
        {
            completedMetadataCandidates = candidates
                .Where(candidate =>
                    !index.IsManuallyCompleted(candidate.Item, candidate.Keyword) &&
                    HasValues(candidate.Item.Genres))
                .ToList();
            var completedMetadataIds = completedMetadataCandidates
                .Select(candidate => candidate.Item.Id)
                .ToHashSet();
            pendingCandidates = candidates
                .Where(candidate =>
                    !completedMetadataIds.Contains(candidate.Item.Id) &&
                    !index.IsManuallyCompleted(candidate.Item, candidate.Keyword))
                .ToList();
            mode = completedMetadataCandidates.Count == 0 && pendingCandidates.Count > 0
                ? JavDbMetadataSyncMode.Full
                : JavDbMetadataSyncMode.Incremental;
            modeReason = mode == JavDbMetadataSyncMode.Full
                ? "范围内可识别影片的流派均为空，执行全量补全"
                : "按流派字段查漏补缺，流派为空处理，流派非空跳过";
        }
        else
        {
            mode = state.Successes.Count == 0
                ? JavDbMetadataSyncMode.Full
                : JavDbMetadataSyncMode.Incremental;
            completedMetadataCandidates = mode == JavDbMetadataSyncMode.Full
                ? new List<Candidate>()
                : candidates
                    .Where(candidate =>
                        !index.IsManuallyCompleted(candidate.Item, candidate.Keyword) &&
                        !index.IsFailed(candidate.Item, candidate.Keyword) &&
                        index.IsSuccessful(candidate.Item, candidate.Keyword) &&
                        IsMetadataComplete(candidate.Item, selection))
                    .ToList();
            var completedMetadataIds = completedMetadataCandidates
                .Select(candidate => candidate.Item.Id)
                .ToHashSet();
            pendingCandidates = mode == JavDbMetadataSyncMode.Full
                ? candidates
                : candidates
                    .Where(candidate =>
                        !completedMetadataIds.Contains(candidate.Item.Id) &&
                        !index.IsManuallyCompleted(candidate.Item, candidate.Keyword))
                    .ToList();
            modeReason = mode == JavDbMetadataSyncMode.Full
                ? "没有成功记录，执行全量替换"
                : "存在成功记录，按所选元数据是否完整执行增量补全";
        }

        return new JavDbMetadataSyncPlan
        {
            Mode = mode,
            ModeReason = modeReason,
            TotalItemCount = allItems.Count,
            PendingItems = pendingCandidates
                .Select(candidate => candidate.Item)
                .ToList(),
            CompletedMetadataItems = completedMetadataCandidates
                .Select(candidate => candidate.Item)
                .ToList(),
            SkippedWithoutNumberCount = allItems.Count - candidates.Count,
            SkippedByCompletedMetadataCount = completedMetadataCandidates.Count,
            SkippedByStateCount = Math.Max(
                0,
                candidates.Count - pendingCandidates.Count - completedMetadataCandidates.Count),
            PendingByMissingMetadataCount =
                pendingCandidates.Count(candidate => !IsMetadataComplete(candidate.Item, selection))
        };
    }

    internal static bool IsMetadataComplete(BaseItem item, JavDbReplacementSelection selection)
    {
        if (item == null || selection?.HasAny != true)
            return true;

        if (selection.Genres)
            return HasValues(item.Genres);

        return (!selection.Tags || HasValues(item.Tags)) &&
               (!selection.Studios || HasValues(item.Studios)) &&
               (!selection.Rating || item.CommunityRating.HasValue);
    }

    private static bool HasValues(IEnumerable<string> values)
    {
        return values?.Any(value => !string.IsNullOrWhiteSpace(value)) == true;
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
