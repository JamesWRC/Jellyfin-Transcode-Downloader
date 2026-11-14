using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TranscodeDownloader
{
    /// <summary>
    /// Download limits. A transcoded download is otherwise served as fast as the GPU can encode
    /// it (measured: an "8 Mbps" 1080p download at 27.7x realtime, ~210 Mbps), which competes
    /// with every stream for the server's upload.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Transfer-rate ceiling for one download, in Mbps. 0 = unlimited.</summary>
        public double PerDownloadMbps { get; set; } = 30;

        /// <summary>Transfer-rate ceiling shared by every paced download at once, in Mbps. 0 = unlimited.</summary>
        public double TotalMbps { get; set; } = 60;

        /// <summary>
        /// Do not pace clients on the local network. Their traffic (including router hairpin)
        /// never touches the WAN upload. They still take a download slot.
        /// </summary>
        public bool ExemptLan { get; set; } = true;

        /// <summary>Downloads that may run (or hold a ticket) at once, server-wide. 0 = unlimited.</summary>
        public int MaxGlobal { get; set; } = 3;

        /// <summary>Downloads one user may run (or hold a ticket for) at once. 0 = unlimited.</summary>
        public int MaxPerUser { get; set; } = 1;

        /// <summary>
        /// Free space, in GB, that must remain in the transcode folder after a new download's
        /// estimated output (and the rest of every running download's) is accounted for.
        /// </summary>
        public double HeadroomGb { get; set; } = 2;

        /// <summary>
        /// Also pace Jellyfin's own original-file Download button (/Items/{id}/Download).
        /// Paced only (shares the total budget); it takes no slot and needs no ticket.
        /// </summary>
        public bool PaceNativeDownloads { get; set; }
    }
}
