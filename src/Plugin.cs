using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Letterbox.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Letterbox;

/// <summary>
/// Letterbox: finds black bars baked into video files and tags items "bars:&lt;ratio&gt;"
/// so clients (Wholphin NoBars) can zoom past them. Never modifies media files.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Letterbox";

    public override Guid Id => Guid.Parse("6b1c3a52-7f4e-4d0b-9a61-2f0f8c5e9b17");

    public override string Description => "Detects black bars baked into videos and tags items bars:<ratio>.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
            },
        ];
    }
}
