#if __EMBY__
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using HttpRequestOptions = MediaBrowser.Common.Net.HttpRequestOptions;

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

public class UpdatePluginTask : IScheduledTask
{
#if __EMBY49__
    private const string UpdateAssetPrefix = "EMBY4.9.";
#else
    private const string UpdateAssetPrefix = "EMBY4.8.";
#endif

    private readonly IApplicationHost _applicationHost;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IHttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly IZipClient _zipClient;

    public UpdatePluginTask(IApplicationHost applicationHost, IApplicationPaths applicationPaths,
        IHttpClient httpClient, ILogManager logManager, IZipClient zipClient)
    {
        _applicationHost = applicationHost;
        _applicationPaths = applicationPaths;
        _httpClient = httpClient;
        _logger = logManager.CreateLogger<UpdatePluginTask>();
        _zipClient = zipClient;
    }

    private static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString();

    public string Key => $"{Plugin.ProviderName}UpdatePlugin";

    // Emby orders scheduled tasks by Name. Leading spaces control display order without changing the visible title.
    public string Name => "\u0020更新插件";

    public string Description => $"将 {Plugin.ProviderName} 插件更新至最新版本。";

    public string Category => Plugin.ProviderName;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(5).Ticks
        };
    }

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        await Task.Yield();

        if (!Plugin.Instance.Configuration.EnableAutoUpdate)
        {
            _logger.Info("Auto update is disabled");
            return;
        }

        progress?.Report(0);

        try
        {
            var apiResult = JsonSerializer.Deserialize<ApiResponseInfo>(await _httpClient.Get(new HttpRequestOptions
            {
                Url = "https://api.github.com/repos/GlowsSama/jellyfin-plugin-metatube/releases/latest",
                CancellationToken = cancellationToken,
                AcceptHeader = "application/json",
                EnableDefaultUserAgent = true
            }).ConfigureAwait(false));

            var currentVersion = ParseVersion(CurrentVersion);
            var remoteVersion = ParseVersion(apiResult?.TagName);

            if (currentVersion.CompareTo(remoteVersion) < 0)
            {
                _logger.Info("Found new plugin version: {0}", remoteVersion);

                var url = apiResult?.Assets
                    .Where(asset =>
                        asset.Name.StartsWith(UpdateAssetPrefix, StringComparison.OrdinalIgnoreCase) &&
                        asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray()
                    .FirstOrDefault()
                    ?.BrowserDownloadUrl;
                if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                    throw new Exception("Invalid download url");

                var tempRoot = CreateUpdateTempRoot();
                var packageDirectory = Path.Combine(tempRoot, "package");
                var downloadPath = Path.Combine(tempRoot, "update.zip");
                try
                {
                    using (var zipStream = await _httpClient.Get(new HttpRequestOptions
                           {
                               Url = url,
                               CancellationToken = cancellationToken,
                               EnableDefaultUserAgent = true,
                               Progress = progress
                           }).ConfigureAwait(false))
                    {
                        if (zipStream == null)
                            throw new Exception("Plugin update download returned no data");

                        using var zipFile = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write,
                            FileShare.None, 81920, FileOptions.Asynchronous);
                        await zipStream.CopyToAsync(zipFile, 81920, cancellationToken).ConfigureAwait(false);
                    }

                    Directory.CreateDirectory(packageDirectory);
                    using (var zipStream = File.OpenRead(downloadPath))
                    {
                        _zipClient.ExtractAllFromZip(zipStream, packageDirectory, true);
                    }

                    var stagedAssemblyPath = Directory
                        .GetFiles(packageDirectory, $"{Plugin.ProviderName}.dll", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(stagedAssemblyPath))
                        throw new Exception($"{Plugin.ProviderName}.dll was not found in the update package");

                    var packageRoot = Path.GetDirectoryName(stagedAssemblyPath)
                                      ?? throw new Exception("Unable to locate the staged plugin package root");
                    var pluginDirectory = GetCurrentPluginDirectory();

                    ReplacePluginDirectory(packageRoot, pluginDirectory);

                    _logger.Info("Plugin update complete");

                    _applicationHost.NotifyPendingRestart();
                }
                finally
                {
                    DeleteDirectoryIfExists(tempRoot);
                }
            }
            else
            {
                _logger.Info("No need to update");
            }
        }
        catch (Exception e)
        {
            _logger.Error("Update error: {0}", e.Message);
        }

        progress?.Report(100);
    }

    private string CreateUpdateTempRoot()
    {
        var tempPath = string.IsNullOrWhiteSpace(_applicationPaths.TempDirectory)
            ? Path.GetTempPath()
            : _applicationPaths.TempDirectory;
        Directory.CreateDirectory(tempPath);

        var tempRoot = Path.Combine(tempPath, $"MetaTubeUpdate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private string GetCurrentPluginDirectory()
    {
        var pluginsPath = Path.GetFullPath(_applicationPaths.PluginsPath);
        var assemblyDirectory = Path.GetFullPath(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty);
        if (IsUnderDirectory(assemblyDirectory, pluginsPath))
            return assemblyDirectory;

        var conventionalDirectory = Path.Combine(pluginsPath, Plugin.ProviderName);
        if (Directory.Exists(conventionalDirectory))
            return conventionalDirectory;

        return Directory.EnumerateDirectories(pluginsPath)
                   .Where(directory => string.Equals(Path.GetFileName(directory), Plugin.ProviderName,
                       StringComparison.OrdinalIgnoreCase))
                   .FirstOrDefault(directory =>
                       File.Exists(Path.Combine(directory, $"{Plugin.ProviderName}.dll")))
               ?? conventionalDirectory;
    }

    private void ReplacePluginDirectory(string packageRoot, string pluginDirectory)
    {
        var pluginParent = Path.GetDirectoryName(pluginDirectory)
                           ?? throw new Exception("Unable to locate the plugin parent directory");
        Directory.CreateDirectory(pluginParent);

        var hadExistingPlugin = Directory.Exists(pluginDirectory);
        var backupDirectory = Path.Combine(pluginParent,
            $".{Path.GetFileName(pluginDirectory)}.backup-{Guid.NewGuid():N}");

        try
        {
            if (hadExistingPlugin)
                Directory.Move(pluginDirectory, backupDirectory);

            Directory.CreateDirectory(pluginDirectory);
            CopyDirectory(packageRoot, pluginDirectory);

            if (!File.Exists(Path.Combine(pluginDirectory, $"{Plugin.ProviderName}.dll")))
                throw new Exception("The installed plugin package is missing MetaTube.dll");
        }
        catch (Exception updateException)
        {
            try
            {
                DeleteDirectoryIfExists(pluginDirectory);
                if (hadExistingPlugin && Directory.Exists(backupDirectory))
                    Directory.Move(backupDirectory, pluginDirectory);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException("Plugin update failed and rollback also failed.",
                    updateException, rollbackException);
            }

            throw;
        }

        if (Directory.Exists(backupDirectory))
        {
            try
            {
                Directory.Delete(backupDirectory, true);
            }
            catch (Exception cleanupException)
            {
                _logger.Warn("Unable to remove plugin backup directory {0}: {1}",
                    backupDirectory, cleanupException.Message);
            }
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var targetPath = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? targetDirectory);
            File.Copy(file, targetPath, true);
        }
    }

    private void DeleteDirectoryIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception exception)
        {
            _logger.Warn("Unable to remove temporary plugin update directory {0}: {1}",
                path, exception.Message);
        }
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var relativePath = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relativePath) &&
               !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static Version ParseVersion(string v)
    {
        return new Version(v.StartsWith("v") ? v[1..] : v);
    }

    private class ApiResponseInfo
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; }

        [JsonPropertyName("assets")]
        public ApiAssetInfo[] Assets { get; set; }
    }

    private class ApiAssetInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; }
    }
}

#endif
