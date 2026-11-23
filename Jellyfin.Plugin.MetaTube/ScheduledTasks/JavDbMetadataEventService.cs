#if __EMBY__
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

public sealed class JavDbMetadataEventService : IServerEntryPoint, IDisposable
{
    private const int MaxQueueRetryAttempts = 3;
    private static readonly TimeSpan WorkerPollInterval = TimeSpan.FromSeconds(15);
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;
    private readonly JavDbMetadataCompleteTask _processor;
    private readonly SemaphoreSlim _queueGate = new(1, 1);
    private readonly string _queueFilePath;
    private CancellationTokenSource _cancellationTokenSource;
    private Task _workerTask;
    private bool _started;

    public JavDbMetadataEventService(
        ILibraryManager libraryManager,
        IApplicationPaths applicationPaths,
        ILogManager logManager)
    {
        _libraryManager = libraryManager;
        _logger = logManager.CreateLogger<JavDbMetadataEventService>();
        _processor = new JavDbMetadataCompleteTask(logManager, libraryManager, applicationPaths);
        _queueFilePath = Path.Combine(
            applicationPaths.PluginConfigurationsPath,
            JavDbMetadataEventQueueStore.FileName);
    }

    public void Run()
    {
        if (_started)
            return;

        _started = true;
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _cancellationTokenSource = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoopAsync(_cancellationTokenSource.Token));
        _logger.Info("JAVDB 事件协调器已启动 | 队列文件={0}", _queueFilePath);
    }

    public void Dispose()
    {
        if (!_started)
            return;

        _started = false;
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
    }

    private void OnItemAdded(object sender, ItemChangeEventArgs args)
    {
        var item = args?.Item;
        if (item == null)
            return;

        var delaySeconds = 60 + (int)(item.Id.GetHashCode() & 0x3F);
        QueueLibraryItemSafely(item, "added", false, TimeSpan.FromSeconds(delaySeconds));
    }

    private void OnItemUpdated(object sender, ItemChangeEventArgs args)
    {
        var item = args?.Item;
        if (item == null || JavDbMetadataEventSuppressor.IsSuppressed(item.Id))
            return;

        QueueLibraryItemSafely(item, "updated", false, TimeSpan.FromSeconds(5));
    }

    private void OnItemRemoved(object sender, ItemChangeEventArgs args)
    {
        var item = args?.Item;
        var cancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
        if (item == null)
            return;

        _ = ObserveBackgroundTaskAsync(
            RemoveLibraryItemAsync(item, cancellationToken),
            "移除 JAVDB 事件队列项目");
    }

    private void QueueLibraryItemSafely(
        BaseItem item,
        string reason,
        bool force,
        TimeSpan delay)
    {
        var cancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
        _ = ObserveBackgroundTaskAsync(
            QueueLibraryItemAsync(item, reason, force, delay, cancellationToken),
            "写入 JAVDB 事件队列");
    }

    private async Task QueueLibraryItemAsync(
        BaseItem item,
        string reason,
        bool force,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = Plugin.Instance?.Configuration;
        if (configuration?.EnableJavDbMetadataEventQueue != true)
            return;

        var selection = JavDbReplacementSelection.FromConfiguration(configuration);
        if (item is not Movie ||
            string.IsNullOrWhiteSpace(item.Name) ||
            !MetadataOrganizer.IsMetaTubeItem(item))
            return;
        var libraryScope = JavDbLibraryScope.Filter(
            _libraryManager,
            new[] { item },
            configuration.GetLibraryProviderFilters());
        if (libraryScope.Items.Count == 0)
            return;

        var state = await _processor.LoadStateForEventAsync(cancellationToken).ConfigureAwait(false);
        var isFailure = IsFailureRecord(state, item);
        var isSuccess = IsSuccessRecord(state, item);
        var needsGenreRepair = selection.Genres && !HasGenres(item);

        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queue = await JavDbMetadataEventQueueStore.LoadAsync(_queueFilePath, cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var itemId = item.Id.ToString("N");
            var existing = queue.Items.FirstOrDefault(entry =>
                string.Equals(entry.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
            var pathChanged = existing != null &&
                              !string.Equals(existing.Path, item.Path, StringComparison.OrdinalIgnoreCase);
            var shouldQueue = force || existing != null || pathChanged || isFailure || !isSuccess ||
                              needsGenreRepair || !selection.HasAny;
            if (!shouldQueue)
                return;

            if (existing == null)
            {
                existing = new JavDbMetadataEventQueueItem
                {
                    ItemId = itemId,
                    EnqueuedAtUtc = now
                };
                queue.Items.Add(existing);
            }

            existing.Name = item.Name;
            existing.Path = item.Path ?? string.Empty;
            existing.Reason = pathChanged ? "path_changed" : reason;
            existing.Force |= force || pathChanged;
            existing.UpdatedAtUtc = now;
            existing.NextAttemptAtUtc = now.Add(delay);
            queue.UpdatedAtUtc = now;
            await JavDbMetadataEventQueueStore.SaveAsync(_queueFilePath, queue, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _queueGate.Release();
        }
    }

    private Task RemoveLibraryItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        return JavDbMetadataTaskBase.RunWorkflowExclusiveAsync(
            () => RemoveLibraryItemCoreAsync(item, cancellationToken),
            cancellationToken);
    }

    private async Task RemoveLibraryItemCoreAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var itemId = item.Id.ToString("N");
        var keywords = JavDbMetadataTaskBase.GetJavDbKeywords(item)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queue = await JavDbMetadataEventQueueStore.LoadAsync(_queueFilePath, cancellationToken)
                .ConfigureAwait(false);
            if (queue.Items.RemoveAll(entry =>
                    string.Equals(entry.ItemId, itemId, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                queue.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await JavDbMetadataEventQueueStore.SaveAsync(_queueFilePath, queue, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _queueGate.Release();
        }

        var state = await _processor.LoadStateForEventAsync(cancellationToken).ConfigureAwait(false);
        var removed = state.Successes.RemoveAll(record =>
                IsSameRemovedItem(record, itemId, keywords)) +
            state.Failures.RemoveAll(record => IsSameRemovedItem(record, itemId, keywords));
        if (removed > 0)
        {
            await _processor.SaveStateForEventAsync(state, cancellationToken).ConfigureAwait(false);
            _logger.Info("JAVDB 已移除项目记录 | 项目ID={0} | 移除记录={1}", itemId, removed);
        }
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextDueQueueItemAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.Error("JAVDB 事件协调器循环失败 | 错误={0}", exception.Message);
            }

            try
            {
                await Task.Delay(WorkerPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ProcessNextDueQueueItemAsync(CancellationToken cancellationToken)
    {
        JavDbMetadataEventQueueItem dueItem = null;
        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queue = await JavDbMetadataEventQueueStore.LoadAsync(_queueFilePath, cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            dueItem = queue.Items
                .Where(entry => entry.NextAttemptAtUtc <= now)
                .OrderBy(entry => entry.NextAttemptAtUtc)
                .FirstOrDefault();
        }
        finally
        {
            _queueGate.Release();
        }

        if (dueItem == null || !Guid.TryParseExact(dueItem.ItemId, "N", out var itemId))
            return;

        var configuration = Plugin.Instance?.Configuration;
        if (configuration?.EnableJavDbMetadataEventQueue != true)
        {
            await RemoveQueueItemAsync(dueItem.ItemId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var item = _libraryManager.GetItemById(itemId) as Movie;
        if (item == null)
        {
            await RemoveQueueItemAsync(dueItem.ItemId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var libraryScope = JavDbLibraryScope.Filter(
            _libraryManager,
            new[] { item },
            configuration.GetLibraryProviderFilters());
        if (libraryScope.Items.Count == 0)
        {
            await RemoveQueueItemAsync(dueItem.ItemId, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await JavDbMetadataTaskBase.RunWorkflowExclusiveAsync(
                    () => ProcessQueueItemAsync(item, dueItem, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await MarkQueueAttemptFailedAsync(
                    dueItem,
                    "unknown",
                    exception.Message,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ProcessQueueItemAsync(
        Movie item,
        JavDbMetadataEventQueueItem queueItem,
        CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        var selection = JavDbReplacementSelection.FromConfiguration(configuration);
        if (!selection.HasAny)
        {
            await MetadataOrganizer.OrganizeItemAsync(item, _libraryManager, _logger, cancellationToken)
                .ConfigureAwait(false);
            await RemoveQueueItemAsync(queueItem.ItemId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var state = await _processor.LoadStateForEventAsync(cancellationToken).ConfigureAwait(false);
        var isFailure = IsFailureRecord(state, item);
        var isSuccess = IsSuccessRecord(state, item);
        if (!queueItem.Force && isSuccess && (!selection.Genres || HasGenres(item)) && !isFailure)
        {
            await MetadataOrganizer.OrganizeItemAsync(item, _libraryManager, _logger, cancellationToken)
                .ConfigureAwait(false);
            await RemoveQueueItemAsync(queueItem.ItemId, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queue = await JavDbMetadataEventQueueStore.LoadAsync(_queueFilePath, cancellationToken)
                .ConfigureAwait(false);
            var current = queue.Items.FirstOrDefault(entry =>
                string.Equals(entry.ItemId, queueItem.ItemId, StringComparison.OrdinalIgnoreCase));
            if (current == null)
                return;

            current.AttemptCount++;
            current.LastAttemptAtUtc = DateTimeOffset.UtcNow;
            current.UpdatedAtUtc = current.LastAttemptAtUtc;
            current.NextAttemptAtUtc = current.LastAttemptAtUtc.AddHours(1);
            queue.UpdatedAtUtc = current.UpdatedAtUtc;
            await JavDbMetadataEventQueueStore.SaveAsync(_queueFilePath, queue, cancellationToken)
                .ConfigureAwait(false);
            queueItem.AttemptCount = current.AttemptCount;
        }
        finally
        {
            _queueGate.Release();
        }

        _logger.Info(
            "JAVDB 事件队列处理 | 项目ID={0} | 标题={1} | 原因={2} | 尝试={3}",
            item.Id.ToString("N"), item.Name, queueItem.Reason, queueItem.AttemptCount);

        var outcome = await _processor.ProcessSingleItemAsync(item, state, selection, cancellationToken)
            .ConfigureAwait(false);
        state.LastRunAtUtc = DateTimeOffset.UtcNow;
        state.LastRunMode = "EventQueue";
        await _processor.SaveStateForEventAsync(state, cancellationToken).ConfigureAwait(false);

        if (outcome.Kind is JavDbMetadataItemOutcomeKind.Updated
            or JavDbMetadataItemOutcomeKind.Unchanged
            or JavDbMetadataItemOutcomeKind.NotFound
            or JavDbMetadataItemOutcomeKind.Skipped)
        {
            await MetadataOrganizer.OrganizeItemAsync(item, _libraryManager, _logger, cancellationToken)
                .ConfigureAwait(false);
        }

        switch (outcome.Kind)
        {
            case JavDbMetadataItemOutcomeKind.Updated:
            case JavDbMetadataItemOutcomeKind.Unchanged:
            case JavDbMetadataItemOutcomeKind.NotFound:
                await RemoveQueueItemAsync(queueItem.ItemId, cancellationToken).ConfigureAwait(false);
                break;
            case JavDbMetadataItemOutcomeKind.Failed:
                await MarkQueueAttemptFailedAsync(
                        queueItem,
                        outcome.FailureKind,
                        outcome.Message,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                await RemoveQueueItemAsync(queueItem.ItemId, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task MarkQueueAttemptFailedAsync(
        JavDbMetadataEventQueueItem queueItem,
        string failureKind,
        string message,
        CancellationToken cancellationToken)
    {
        var keepForRetry = failureKind is "network" or "rate_limit" or "auth" or "server" or "unknown" &&
                           queueItem.AttemptCount < MaxQueueRetryAttempts;
        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queue = await JavDbMetadataEventQueueStore.LoadAsync(_queueFilePath, cancellationToken)
                .ConfigureAwait(false);
            var current = queue.Items.FirstOrDefault(entry =>
                string.Equals(entry.ItemId, queueItem.ItemId, StringComparison.OrdinalIgnoreCase));
            if (current == null)
                return;

            current.LastError = message ?? string.Empty;
            current.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (keepForRetry)
            {
                var retryDelay = current.AttemptCount switch
                {
                    <= 1 => TimeSpan.FromMinutes(5),
                    _ => TimeSpan.FromMinutes(15)
                };
                current.NextAttemptAtUtc = current.UpdatedAtUtc.Add(retryDelay);
            }
            else
            {
                queue.Items.Remove(current);
            }

            queue.UpdatedAtUtc = current.UpdatedAtUtc;
            await JavDbMetadataEventQueueStore.SaveAsync(_queueFilePath, queue, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _queueGate.Release();
        }
    }

    private async Task RemoveQueueItemAsync(string itemId, CancellationToken cancellationToken)
    {
        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queue = await JavDbMetadataEventQueueStore.LoadAsync(_queueFilePath, cancellationToken)
                .ConfigureAwait(false);
            if (queue.Items.RemoveAll(entry =>
                    string.Equals(entry.ItemId, itemId, StringComparison.OrdinalIgnoreCase)) == 0)
                return;

            queue.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await JavDbMetadataEventQueueStore.SaveAsync(_queueFilePath, queue, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _queueGate.Release();
        }
    }

    private async Task ObserveBackgroundTaskAsync(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancellation is expected.
        }
        catch (Exception exception)
        {
            _logger.Error("JAVDB 事件协调器失败 | 操作={0} | 错误={1}", operation, exception.Message);
        }
    }

    private static bool HasGenres(BaseItem item)
    {
        return item?.Genres?.Any(genre => !string.IsNullOrWhiteSpace(genre)) == true;
    }

    private static bool IsFailureRecord(JavDbMetadataState state, BaseItem item)
    {
        var itemId = item.Id.ToString("N");
        var keywords = JavDbMetadataTaskBase.GetJavDbKeywords(item)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (state?.Failures ?? Enumerable.Empty<JavDbMetadataRecord>())
            .Any(record => IsSameRemovedItem(record, itemId, keywords));
    }

    private static bool IsSuccessRecord(JavDbMetadataState state, BaseItem item)
    {
        var itemId = item.Id.ToString("N");
        var keywords = JavDbMetadataTaskBase.GetJavDbKeywords(item)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (state?.Successes ?? Enumerable.Empty<JavDbMetadataRecord>())
            .Any(record => IsSameRemovedItem(record, itemId, keywords));
    }

    private static bool IsSameRemovedItem(
        JavDbMetadataRecord record,
        string itemId,
        IReadOnlySet<string> keywords)
    {
        return record != null &&
               (string.Equals(record.ItemId, itemId, StringComparison.OrdinalIgnoreCase) ||
                keywords.Contains(record.Keyword ?? string.Empty) ||
                keywords.Contains(record.SourceKeyword ?? string.Empty));
    }
}
#endif
