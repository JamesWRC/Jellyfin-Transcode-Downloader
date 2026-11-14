using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.TranscodeDownloader
{
    /// <summary>
    /// Response of <c>GET /TranscodeDownloader/Codecs</c>. Deliberately small and stable so the
    /// client script can evolve independently of the server DLL; additive changes bump
    /// <see cref="SchemaVersion"/>.
    /// </summary>
    public class CodecCapabilitiesDto
    {
        /// <summary>Contract version. Bumped only on breaking changes.</summary>
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Video codecs offered by the download UI, in display order.</summary>
        public IReadOnlyList<CodecCapabilityDto> VideoCodecs { get; set; } = new List<CodecCapabilityDto>();

        /// <summary>Audio codecs offered by the download UI, in display order.</summary>
        public IReadOnlyList<CodecCapabilityDto> AudioCodecs { get; set; } = new List<CodecCapabilityDto>();

        /// <summary>Download limits as they apply to the requesting client (additive).</summary>
        public DownloadLimitsDto? Limits { get; set; }
    }

    /// <summary>The download limits, so the sheet can show how long each tier will take.</summary>
    public class DownloadLimitsDto
    {
        /// <summary>False when this client is exempt (on the local network) or no rate limit is set.</summary>
        public bool Paced { get; set; }

        /// <summary>Rate one download gets on its own, in bits per second (0 when not paced).</summary>
        public long RateBps { get; set; }

        public int MaxPerUser { get; set; }

        public int MaxGlobal { get; set; }
    }

    public class TicketRequestDto
    {
        public Guid ItemId { get; set; }

        /// <summary>Requested video bitrate in bits per second.</summary>
        public long VideoBitrate { get; set; }
    }

    public class TicketResponseDto
    {
        public string Ticket { get; set; } = string.Empty;

        public long EstimatedBytes { get; set; }

        public bool Paced { get; set; }

        public long RateBps { get; set; }

        /// <summary>Seconds the transfer takes at <see cref="RateBps"/> on its own; 0 when not paced.</summary>
        public long EtaSeconds { get; set; }
    }

    public class TicketRefusalDto
    {
        /// <summary>user_busy, server_busy or no_space.</summary>
        public string Reason { get; set; } = string.Empty;

        public int RetryAfterSeconds { get; set; }
    }

    /// <summary>
    /// One codec as the download UI sees it: what it would be encoded with, and whether that
    /// is actually going to work on this server.
    /// </summary>
    public class CodecCapabilityDto
    {
        /// <summary>
        /// The value sent as <c>VideoCodec</c>/<c>AudioCodec</c> on the stream URL
        /// (<c>h264</c>, <c>hevc</c>, <c>av1</c>, <c>aac</c>, <c>opus</c>).
        /// </summary>
        public string Codec { get; set; } = string.Empty;

        /// <summary>The FFmpeg encoder Jellyfin would resolve this codec to, e.g. <c>libsvtav1</c>.</summary>
        public string Encoder { get; set; } = string.Empty;

        /// <summary>True when <see cref="Encoder"/> is a hardware encoder.</summary>
        public bool IsHardware { get; set; }

        /// <summary>True when this server's FFmpeg actually has <see cref="Encoder"/>.</summary>
        public bool IsSupported { get; set; }

        /// <summary>
        /// False when the administrator turned the codec off in Dashboard → Playback
        /// (<c>AllowAv1Encoding</c> / <c>AllowHevcEncoding</c>). Always true for codecs with
        /// no such toggle.
        /// </summary>
        public bool IsAllowedByAdmin { get; set; }
    }
}
