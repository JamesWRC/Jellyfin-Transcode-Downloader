using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TranscodeDownloader
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static readonly Guid PluginId = new Guid("a4b5c6d7-e8f9-0a1b-2c3d-4e5f6a7b8c9d");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <summary>The loaded plugin, so non-DI code (middleware, limiter) can read its configuration.</summary>
        public static Plugin? Instance { get; private set; }

        public override string Name => "Transcode Downloader";

        public override Guid Id => PluginId;

        public override string Description => "Quality-selection transcoded downloads, with a server-side speed limit.";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = "Jellyfin.Plugin.TranscodeDownloader.web.config.html",
                },
            };
        }
    }
}
