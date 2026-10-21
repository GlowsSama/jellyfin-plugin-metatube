using System.ComponentModel;

namespace Jellyfin.Plugin.MetaTube.Translation;

public enum TranslationEngine
{
    [Description("百度")]
    Baidu,

    [Description("Google")]
    Google,

    [Description("Google（免费）")]
    GoogleFree,

    [Description("DeepL")]
    DeepL,

    [Description("OpenAI")]
    OpenAi
}
