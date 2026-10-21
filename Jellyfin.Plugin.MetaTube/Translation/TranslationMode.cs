using System.ComponentModel;

namespace Jellyfin.Plugin.MetaTube.Translation;

public enum TranslationMode
{
    [Description("禁用")]
    Disabled,

    [Description("标题")]
    Title,

    [Description("简介")]
    Summary,

    [Description("标题和简介")]
    Both
}
