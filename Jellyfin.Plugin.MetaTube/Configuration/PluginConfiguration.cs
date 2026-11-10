using System.Text;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.ScheduledTasks;
using Jellyfin.Plugin.MetaTube.Translation;
#if __EMBY__
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;

#else
using MediaBrowser.Model.Plugins;
#endif

namespace Jellyfin.Plugin.MetaTube.Configuration;

#if __EMBY__
public class PluginConfiguration : EditableOptionsBase
{
    public override string EditorTitle => $"{Plugin.ProviderName} 设置";
#else
public class PluginConfiguration : BasePluginConfiguration
{
#endif

#if __EMBY__
    [DisplayName("服务器")]
    [Description("MetaTube 服务器的完整地址，建议使用 HTTPS。")]
    [Required]
#endif
    public string Server { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("访问令牌")]
    [Description("MetaTube 服务器的访问令牌；如果服务器未设置令牌，请留空。")]
#endif
    public string Token { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("FlareSolverr 地址")]
    [Description("FlareSolverr API 地址，例如 http://flaresolverr:8191/v1。留空表示禁用。")]
#endif
    public string FlareSolverrEndpoint { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("允许自动更新")]
    [Description("通过计划任务自动更新插件。")]
    public bool EnableAutoUpdate { get; set; } = true;
#endif

#if __EMBY__
    [DisplayName("自动创建合集")]
    [Description("按系列自动创建合集。")]
#endif
    public bool EnableCollections { get; set; } = false;

#if __EMBY__
    [DisplayName("添加导演信息")]
    [Description("为对应视频的元数据添加导演信息。")]
#endif
    public bool EnableDirectors { get; set; } = true;

#if __EMBY__
    [DisplayName("评分系统")]
    [Description("显示原始网站的用户评分。")]
#endif
    public bool EnableRatings { get; set; } = true;

#if __EMBY__
    [DisplayName("刮削预告片")]
    [Description("生成 strm 格式的在线视频预告片。")]
#endif
    public bool EnableTrailers { get; set; } = false;

#if __EMBY__
    [DisplayName("使用演员真实信息")]
    [Description("从 AVBASE 搜索并替换为真实演员姓名。")]
#endif
    public bool EnableRealActorNames { get; set; } = false;

#if __EMBY__
    [DisplayName("从来源刮削流派、标签和工作室数据")]
    [Description("开启后，普通刮削会将来源标签写入流派、系列写入标签，并将片商和发行商写入工作室。不同站点的数据往往较混乱，语言和格式难以统一，建议关闭，并定期运行“从JAVDB替换数据”统一整理。")]
#endif
    public bool EnableGenreAndTagScraping { get; set; } = true;

#if __EMBY__
    [DisplayName("流派(标签)")]
    [Description("使用 JAVDB 的标签替换影片流派。")]
#endif
    public bool ReplaceJavDbGenres { get; set; } = true;

#if __EMBY__
    [DisplayName("标签(系列)")]
    [Description("使用 JAVDB 的系列替换影片标签。")]
#endif
    public bool ReplaceJavDbTags { get; set; } = true;

#if __EMBY__
    [DisplayName("工作室(厂牌,发行商)")]
    [Description("使用 JAVDB 的片商和发行商替换影片工作室。")]
#endif
    public bool ReplaceJavDbStudios { get; set; } = true;

#if __EMBY__
    [DisplayName("演员名字")]
    [Description("使用 JAVDB 的演员列表替换影片演员，同时保留导演等其他人员信息。")]
#endif
    public bool ReplaceJavDbActors { get; set; } = false;

#if __EMBY__
    [DisplayName("JAVDB 成功记录")]
    [Description("只读统计。成功记录保存在插件配置目录的 JSON 状态文件中，最多保留 10000 条。")]
    [ReadOnly(true)]
#endif
    public int JavDbMetadataSuccessCount => GetJavDbMetadataSummary().SuccessCount;

#if __EMBY__
    [DisplayName("JAVDB 失败记录")]
    [Description("只读统计。失败记录会在下次任务运行时自动重试，最多保留 10000 条。")]
    [ReadOnly(true)]
#endif
    public int JavDbMetadataFailureCount => GetJavDbMetadataSummary().FailureCount;

#if __EMBY__
    [DisplayName("JAVDB 上次运行")]
    [Description("只读统计。时间按 Emby 服务器本地时区显示。")]
    [ReadOnly(true)]
#endif
    public string JavDbMetadataLastRun => FormatJavDbMetadataLastRun(GetJavDbMetadataSummary().LastRunAtUtc);

#if __EMBY__
    [DisplayName("JAVDB 上次模式")]
    [Description("只读统计。显示上一次任务执行的是全量替换还是增量补全。")]
    [ReadOnly(true)]
#endif
    public string JavDbMetadataLastMode => FormatJavDbMetadataLastMode(GetJavDbMetadataSummary().LastRunMode);

#if __EMBY__
    [Browsable(false)]
#endif
    public string JavDbMetadataSuccessRecords { get; set; } = string.Empty;

#if __EMBY__
    [Browsable(false)]
#endif
    public string JavDbMetadataFailureRecords { get; set; } = string.Empty;

#if __EMBY__
    [Browsable(false)]
#endif
    public bool JavDbMetadataRecordsInitialized { get; set; } = false;

    private JavDbMetadataSummary GetJavDbMetadataSummary()
    {
        return JavDbMetadataStateStore.GetSummary(
            Plugin.Instance?.JavDbMetadataStatePath,
            () => new JavDbMetadataState
            {
                Successes = JavDbMetadataStateStore.ParseLegacyRecords(JavDbMetadataSuccessRecords),
                Failures = JavDbMetadataStateStore.ParseLegacyRecords(JavDbMetadataFailureRecords)
            });
    }

    private static string FormatJavDbMetadataLastRun(DateTimeOffset lastRunAtUtc)
    {
        return lastRunAtUtc == default
            ? "尚未运行"
            : lastRunAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static string FormatJavDbMetadataLastMode(string lastRunMode)
    {
        return lastRunMode switch
        {
            nameof(JavDbMetadataSyncMode.Full) => "全量替换",
            nameof(JavDbMetadataSyncMode.Incremental) => "增量补全",
            nameof(JavDbMetadataSyncMode.Skipped) => "未选择替换项目",
            _ => "尚未运行"
        };
    }

#if __EMBY__
    [DisplayName("给封面使用角标")]
    [Description("为主图添加中文字幕角标。")]
#endif
    public bool EnableBadges { get; set; } = false;

#if __EMBY__
    [DisplayName("角标地址")]
    [Description("自定义角标地址，建议使用 PNG 格式。（默认值：zimu.png）")]
#endif
    public string BadgeUrl { get; set; } = "zimu.png";

#if __EMBY__
    [DisplayName("主图宽高比")]
    [Description("主图的宽高比，设置为负数时使用默认值。")]
#endif
    public double PrimaryImageRatio { get; set; } = -1;

#if __EMBY__
    [DisplayName("默认图片质量")]
    [Description("JPEG 图片的默认压缩质量，范围为 0 到 100。（默认值：90）")]
    [MinValue(0)]
    [MaxValue(100)]
    [Required]
#endif
    public int DefaultImageQuality { get; set; } = 90;

#if __EMBY__
    [DisplayName("启用影片来源过滤")]
    [Description("过滤并重新排序影片来源的搜索结果。")]
    [Browsable(false)]
#endif
    public bool EnableMovieProviderFilter
    {
        get => true;
        set { }
    }

#if __EMBY__
    [DisplayName("影片刮削源过滤")]
    [Description("勾选需要启用的影片来源；未选择时保留全部来源。")]
    [EditMultilSelect]
    [SelectItemsSource(nameof(MovieProviderOptions))]
#endif
    public string RawMovieProviderFilter
    {
        get => _movieProviderFilter?.Any() == true ? string.Join(',', _movieProviderFilter) : string.Empty;
        set => _movieProviderFilter = value?.Split(',').Select(s => s.Trim()).Where(s => s.Any())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

#if __EMBY__
    [DisplayName("启用媒体库自定义刮削源")]
    [Description(
        "启用后，匹配媒体库使用对应来源；媒体库名称匹配会忽略空格和标点。无法定位媒体库时，仅保留自定义规则中配置的来源。自定义来源无结果时，自动使用全部已启用的刮削源兜底。")]
#endif
    public bool EnableLibraryProviderFilter { get; set; } = false;

#if __EMBY__
    [DisplayName("媒体库自定义刮削源")]
    [Description(
        "每行一个媒体库，格式为“媒体库名称:来源1,来源2”。例如：\n骑兵:FANZA,MGS\n步兵:JAVBUS,JAVDB\n当指定来源没有搜索结果时，会自动使用全部已启用的刮削源兜底。")]
    [EditMultiline(8)]
#endif
    public string LibraryProviderFilterRules { get; set; } = string.Empty;

#if __EMBY__
    [Browsable(false)]
    public List<EditorSelectOption> MovieProviderOptions
    {
        get
        {
            var providers = new List<string>();
            AddMovieProviders(providers, _movieProviderFilter);
            AddMovieProviders(providers, KnownMovieProviders);

            return providers.Select(provider => new EditorSelectOption
            {
                Name = provider,
                Value = provider,
                IsEnabled = true
            }).ToList();
        }
    }

    private static readonly string[] KnownMovieProviders =
    {
        "AVBASE",
        "FANZA",
        "DUGA",
        "MGS",
        "Getchu",
        "SOD",
        "FALENO",
        "DAHLIA",
        "AV-LEAGUE",
        "Pcolle",
        "AVE",
        "Caribbeancom",
        "CaribbeancomPR",
        "10musume",
        "1Pondo",
        "HEYZO",
        "TOKYO-HOT",
        "H0930",
        "C0930",
        "H4610",
        "KIN8",
        "PACOPACOMAMA",
        "MURAMURA",
        "MYWIFE",
        "HeyDouga",
        "Gcolle",
        "FC2",
        "FC2PPVDB",
        "fc2hub",
        "Fantia",
        "FantiaPost",
        "MadouQu",
        "7MMTV",
        "MissAV",
        "JavVerse",
        "JavLibrary",
        "JavBus",
        "JAVFREE",
        "123AV",
        "AVMOO",
        "AVSOX",
        "Manko",
        "ThePornDBScene",
        "ThePornDBMovie",
        "JavDB"
    };

    private static void AddMovieProviders(ICollection<string> target, IEnumerable<string> providers)
    {
        foreach (var provider in providers ?? Enumerable.Empty<string>())
        {
            var normalized = provider?.Trim();
            if (!string.IsNullOrEmpty(normalized) &&
                !target.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(normalized);
            }
        }
    }
#endif

    public List<string> GetMovieProviderFilter()
    {
        return _movieProviderFilter;
    }

    public IReadOnlyList<LibraryProviderFilterRule> GetLibraryProviderFilters()
    {
        var rules = new List<LibraryProviderFilterRule>();
        var libraryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in (LibraryProviderFilterRules ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.Any() || line.StartsWith('#'))
                continue;

            var separatorIndex = line.IndexOfAny(new[] { ':', '：' });
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
                continue;

            var libraryName = line[..separatorIndex].Trim();
            var normalizedLibraryName = NormalizeLibraryName(libraryName);
            if (!normalizedLibraryName.Any() || !libraryNames.Add(normalizedLibraryName))
                continue;

            var providers = line[(separatorIndex + 1)..]
                .Split(new[] { ',', '，', ';', '；' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(provider => provider.Any())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!providers.Any())
                continue;

            rules.Add(new LibraryProviderFilterRule(libraryName, providers));
        }

        return rules;
    }

    public static string NormalizeLibraryName(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var previousWasWhitespace = false;

        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                    builder.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private List<string> _movieProviderFilter;

#if __EMBY__
    [DisplayName("JavDB 备用域名")]
    [Description("主域名不可用时按顺序尝试。多个域名使用英文逗号分隔，最多 4 个备用域名。")]
#endif
    public string JavDbBackupDomains { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("MissAV 备用域名")]
    [Description("主域名不可用时按顺序尝试。多个域名使用英文逗号分隔，最多 4 个备用域名。")]
#endif
    public string MissAvBackupDomains { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("启用模板")]
#endif
    public bool EnableTemplate { get; set; } = false;

#if __EMBY__
    [DisplayName("名称模板")]
#endif
    public string NameTemplate { get; set; } = DefaultNameTemplate;

#if __EMBY__
    [DisplayName("标语模板")]
#endif
    public string TaglineTemplate { get; set; } = DefaultTaglineTemplate;

    public static string DefaultNameTemplate => "{number} {title}";

    public static string DefaultTaglineTemplate => "配信開始日 {date}";

#if __EMBY__
    [DisplayName("翻译模式")]
#endif
    public TranslationMode TranslationMode { get; set; } = TranslationMode.Disabled;

#if __EMBY__
    [DisplayName("翻译引擎")]
#endif
    public TranslationEngine TranslationEngine { get; set; } = TranslationEngine.Baidu;

#if __EMBY__
    [DisplayName("百度应用 ID")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.Baidu)]
#endif
    public string BaiduAppId { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("百度应用密钥")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.Baidu)]
#endif
    public string BaiduAppKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("Google API 密钥")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.Google)]
#endif
    public string GoogleApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("Google API 地址")]
    [Description("自定义 Google 翻译 API 地址。（可选）")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.Google)]
#endif
    public string GoogleApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("DeepL API 密钥")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.DeepL)]
#endif
    public string DeepLApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("DeepL API 地址")]
    [Description("自定义兼容 DeepL 的 API 地址。（可选）")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.DeepL)]
#endif
    public string DeepLApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("API 密钥")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("API 地址")]
    [Description("第三方模型服务的 API 地址。（可选）")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("模型")]
    [Description("第三方模型服务的模型名称。（可选）")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiModel { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("自定义提示词")]
    [Description("用于翻译的自定义系统提示词。留空时使用服务器默认提示词。")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
    [EditMultiline(8)]
#endif
    public string OpenAiPrompt { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("启用标题替换")]
#endif
    public bool EnableTitleSubstitution { get; set; } = false;

#if __EMBY__
    [DisplayName("标题替换表")]
    [Description(
        "每行一条记录，以等号分隔。目标子串留空时将删除来源子串。")]
    [EditMultiline(5)]
#endif
    public string TitleRawSubstitutionTable
    {
        get => _titleSubstitutionTable?.ToString();
        set => _titleSubstitutionTable = SubstitutionTable.Parse(value);
    }

    public SubstitutionTable GetTitleSubstitutionTable()
    {
        return _titleSubstitutionTable;
    }

    private SubstitutionTable _titleSubstitutionTable;

#if __EMBY__
    [DisplayName("启用演员替换")]
#endif
    public bool EnableActorSubstitution { get; set; } = false;

#if __EMBY__
    [DisplayName("演员替换表")]
    [Description(
        "每行一条记录，以等号分隔。目标演员留空时将删除来源演员。")]
    [EditMultiline(5)]
#endif
    public string ActorRawSubstitutionTable
    {
        get => _actorSubstitutionTable?.ToString();
        set => _actorSubstitutionTable = SubstitutionTable.Parse(value);
    }

    public SubstitutionTable GetActorSubstitutionTable()
    {
        return _actorSubstitutionTable;
    }

    private SubstitutionTable _actorSubstitutionTable;

#if __EMBY__
    [DisplayName("启用类型替换")]
#endif
    public bool EnableGenreSubstitution { get; set; } = false;

#if __EMBY__
    [DisplayName("类型替换表")]
    [Description(
        "每行一条记录，以等号分隔。目标类型留空时将删除来源类型。")]
    [EditMultiline(5)]
#endif
    public string GenreRawSubstitutionTable
    {
        get => _genreSubstitutionTable?.ToString();
        set => _genreSubstitutionTable = SubstitutionTable.Parse(value);
    }

    public SubstitutionTable GetGenreSubstitutionTable()
    {
        return _genreSubstitutionTable;
    }

    private SubstitutionTable _genreSubstitutionTable;
}

public sealed class LibraryProviderFilterRule
{
    public LibraryProviderFilterRule(string libraryName, IReadOnlyList<string> providers)
    {
        LibraryName = libraryName;
        Providers = providers;
    }

    public string LibraryName { get; }

    public IReadOnlyList<string> Providers { get; }
}
