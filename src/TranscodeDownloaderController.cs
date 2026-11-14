using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeDownloader
{
    /// <summary>
    /// Serves the embedded Transcode Downloader client script, its string bundles, and the
    /// server's codec capabilities.
    /// The script and strings are anonymous — the browser loads them via a plain script/fetch
    /// with no API token. The codec endpoint exposes server encoding configuration and requires
    /// a login, so <c>[AllowAnonymous]</c> is applied per action rather than to the class: a
    /// class-level <c>[AllowAnonymous]</c> would override the action-level <c>[Authorize]</c>.
    /// </summary>
    [ApiController]
    [Route("TranscodeDownloader")]
    public class TranscodeDownloaderController : ControllerBase
    {
        private const string ResourcePrefix = "Jellyfin.Plugin.TranscodeDownloader.web.";

        private static readonly HashSet<string> SupportedLocales = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "en-us", "de", "fr", "es", "zh-cn", "nl"
        };

        /// <summary>
        /// Video codecs the download UI offers, in display order: the value sent as
        /// <c>VideoCodec</c>, the software encoder Jellyfin falls back to, and the prefix of the
        /// hardware encoder name (<c>{prefix}_{accel}</c>, e.g. <c>hevc_nvenc</c>).
        /// </summary>
        private static readonly (string Codec, string SoftwareEncoder, string HardwarePrefix)[] VideoCodecs =
        {
            ("h264", "libx264", "h264"),
            ("hevc", "libx265", "hevc"),
            ("av1", "libsvtav1", "av1"),
        };

        /// <summary>Audio codecs the download UI offers, in display order.</summary>
        private static readonly string[] AudioCodecs = { "aac", "opus" };

        /// <summary>Guards the one-shot capability log line (0 = not yet logged).</summary>
        private static int _capabilitiesLogged;

        /// <summary>Jellyfin.Api's InternalClaimTypes.UserId (that assembly is not a package).</summary>
        private const string UserIdClaim = "Jellyfin-UserId";

        /// <summary>Audio is always encoded at 256 kbps (Jellyfin's -ab for stereo AAC/Opus).</summary>
        private const long AudioBitrate = 256_000;

        /// <summary>VBR outputs land within ±10–15% of target; size checks use the upper end.</summary>
        private const double SizeMargin = 1.15;

        private const int RetryAfterSeconds = 20;

        private readonly IMediaEncoder _mediaEncoder;
        private readonly IConfigurationManager _configurationManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly INetworkManager _networkManager;
        private readonly DownloadLimiter _limiter;
        private readonly ILogger<TranscodeDownloaderController> _logger;

        public TranscodeDownloaderController(
            IMediaEncoder mediaEncoder,
            IConfigurationManager configurationManager,
            ILibraryManager libraryManager,
            IUserManager userManager,
            INetworkManager networkManager,
            DownloadLimiter limiter,
            ILogger<TranscodeDownloaderController> logger)
        {
            _mediaEncoder = mediaEncoder;
            _configurationManager = configurationManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _networkManager = networkManager;
            _limiter = limiter;
            _logger = logger;
        }

        /// <summary>
        /// POST /TranscodeDownloader/Tickets — asks for a download slot. The returned ticket goes
        /// on the stream URL as <c>TdTicket</c>; <see cref="DownloadPacingFilter"/> then paces that
        /// response and frees the slot when it ends. 429 when the user or server is at its limit,
        /// 507 when the transcode folder has no room for the estimated output.
        /// </summary>
        [HttpPost("Tickets")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        [ProducesResponseType(StatusCodes.Status507InsufficientStorage)]
        public ActionResult<TicketResponseDto> CreateTicket([FromBody] TicketRequestDto request)
        {
            if (!Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out var userId))
            {
                return Forbid();
            }

            var user = _userManager.GetUserById(userId);
            var item = request.ItemId == Guid.Empty ? null : _libraryManager.GetItemById(request.ItemId);
            if (user is null || item is null || !item.IsVisibleStandalone(user))
            {
                return NotFound();
            }

            var videoBitrate = Math.Clamp(request.VideoBitrate, 100_000, 200_000_000);
            var seconds = (item.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
            var estimatedBytes = (long)((videoBitrate + AudioBitrate) * seconds / 8);

            var result = _limiter.TryIssue(userId, item.Id, (long)(estimatedBytes * SizeMargin), item.RunTimeTicks ?? 0, _configurationManager.GetTranscodePath());
            switch (result.Outcome)
            {
                case TicketOutcome.Granted:
                    var rate = CurrentClientRateBps();
                    _logger.LogInformation(
                        "[TranscodeDownloader] Ticket for {User}: {Item} at {Bitrate} bps, ~{Megabytes:F0} MB, paced={Paced}",
                        user.Username,
                        item.Name,
                        videoBitrate,
                        estimatedBytes / 1_000_000.0,
                        rate > 0);
                    return new TicketResponseDto
                    {
                        Ticket = result.Slot!.Ticket,
                        EstimatedBytes = estimatedBytes,
                        Paced = rate > 0,
                        RateBps = rate,
                        EtaSeconds = rate > 0 ? (long)(estimatedBytes * 8 / (double)rate) : 0,
                    };

                case TicketOutcome.NoSpace:
                    _logger.LogWarning(
                        "[TranscodeDownloader] Refused {User}'s download of {Item}: needs {Needed} bytes, {Free} free in the transcode folder",
                        user.Username,
                        item.Name,
                        result.NeededBytes,
                        result.FreeBytes);
                    return StatusCode(StatusCodes.Status507InsufficientStorage, new TicketRefusalDto
                    {
                        Reason = "no_space",
                        NeededBytes = result.NeededBytes,
                        FreeBytes = result.FreeBytes,
                    });

                default:
                    Response.Headers["Retry-After"] = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return StatusCode(StatusCodes.Status429TooManyRequests, new TicketRefusalDto
                    {
                        Reason = result.Outcome == TicketOutcome.UserBusy ? "user_busy" : "server_busy",
                        RetryAfterSeconds = RetryAfterSeconds,
                    });
            }
        }

        /// <summary>
        /// GET /TranscodeDownloader/Tickets/{ticket} — how a finished download ended: whether the
        /// encode reached the end and the client was sent all of it. A transcode stopped early still
        /// ends its HTTP response cleanly, so without this a truncated file looks like a success.
        /// 404 until the download has ended (or for another user's ticket).
        /// </summary>
        [HttpGet("Tickets/{ticket}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<DownloadResult> GetTicketResult([FromRoute] string ticket)
        {
            if (!Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out var userId))
            {
                return Forbid();
            }

            var result = _limiter.GetResult(ticket);
            if (result is null || result.UserId != userId)
            {
                return NotFound();
            }

            return result;
        }

        /// <summary>The rate this client's download would get on its own, in bps; 0 when it is not paced.</summary>
        private long CurrentClientRateBps()
        {
            var config = DownloadLimiter.Config;
            var address = HttpContext.Connection.RemoteIpAddress;
            if (config.ExemptLan && address is not null && _networkManager.IsInLocalNetwork(address))
            {
                return 0;
            }

            return (long)(DownloadLimiter.EffectiveRateBytesPerSecond() * 8);
        }

        /// <summary>
        /// GET /TranscodeDownloader/ClientScript — returns the embedded plugin.js.
        /// </summary>
        [HttpGet("ClientScript")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [Produces("application/javascript")]
        public ActionResult GetClientScript()
        {
            var stream = typeof(TranscodeDownloaderController).Assembly
                .GetManifestResourceStream(ResourcePrefix + "plugin.js");

            if (stream is null) return NotFound();

            return File(stream, "application/javascript");
        }

        /// <summary>
        /// GET /TranscodeDownloader/strings/{locale}.json — returns the translation bundle for the given locale.
        /// Supported locales: en-us, de, fr, es, zh-cn, nl.
        /// </summary>
        [HttpGet("strings/{locale}.json")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [Produces("application/json")]
        public ActionResult GetStrings(string locale)
        {
            if (!SupportedLocales.Contains(locale)) return NotFound();

            var stream = typeof(TranscodeDownloaderController).Assembly
                .GetManifestResourceStream(ResourcePrefix + "strings." + locale + ".json");

            if (stream is null) return NotFound();

            return File(stream, "application/json");
        }

        /// <summary>
        /// GET /TranscodeDownloader/Codecs — reports which video/audio codecs this server can
        /// actually encode with, and which the administrator permits.
        /// </summary>
        [HttpGet("Codecs")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [Produces("application/json")]
        public ActionResult<CodecCapabilitiesDto> GetCodecs()
        {
            var options = _configurationManager.GetEncodingOptions();

            var result = new CodecCapabilitiesDto
            {
                VideoCodecs = VideoCodecs
                    .Select(c => ResolveVideoCodec(c.Codec, c.SoftwareEncoder, c.HardwarePrefix, options))
                    .ToList(),
                AudioCodecs = AudioCodecs.Select(ResolveAudioCodec).ToList(),
            };

            var limitsConfig = DownloadLimiter.Config;
            var clientRate = CurrentClientRateBps();
            result.Limits = new DownloadLimitsDto
            {
                Paced = clientRate > 0,
                RateBps = clientRate,
                MaxPerUser = limitsConfig.MaxPerUser,
                MaxGlobal = limitsConfig.MaxGlobal,
            };

            // Logged once per process so support tickets can be diagnosed from the server log.
            if (Interlocked.Exchange(ref _capabilitiesLogged, 1) == 0)
            {
                _logger.LogInformation(
                    "[TranscodeDownloader] Codec capabilities (hwaccel={HardwareAcceleration}, hwEncoding={HardwareEncoding}): {Capabilities}",
                    options.HardwareAccelerationType,
                    options.EnableHardwareEncoding,
                    Describe(result));
            }

            return result;
        }

        /// <summary>
        /// Mirrors <c>EncodingHelper.GetH26xOrAv1Encoder</c>: a hardware encoder is used only when
        /// hardware acceleration is configured, hardware encoding is enabled, and FFmpeg has it;
        /// otherwise the software encoder is used. Jellyfin returns that software encoder
        /// *without* probing it, which is why the probe happens here — an unprobed missing encoder
        /// means the request is accepted and the transcode then dies mid-stream.
        /// </summary>
        private CodecCapabilityDto ResolveVideoCodec(string codec, string softwareEncoder, string hardwarePrefix, EncodingOptions options)
        {
            var allowedByAdmin = IsAllowedByAdmin(codec, options);
            var accel = options.HardwareAccelerationType;

            if (accel != HardwareAccelerationType.none && options.EnableHardwareEncoding)
            {
                var hardwareEncoder = hardwarePrefix + "_" + accel.ToString().ToLowerInvariant();
                if (SupportsEncoder(hardwareEncoder))
                {
                    return new CodecCapabilityDto
                    {
                        Codec = codec,
                        Encoder = hardwareEncoder,
                        IsHardware = true,
                        IsSupported = true,
                        IsAllowedByAdmin = allowedByAdmin,
                    };
                }
            }

            return new CodecCapabilityDto
            {
                Codec = codec,
                Encoder = softwareEncoder,
                IsHardware = false,
                IsSupported = SupportsEncoder(softwareEncoder),
                IsAllowedByAdmin = allowedByAdmin,
            };
        }

        /// <summary>
        /// Mirrors <c>EncodingHelper.GetAudioEncoder</c>. There is no administrator toggle for
        /// audio codecs, so availability is purely an FFmpeg question.
        /// </summary>
        private CodecCapabilityDto ResolveAudioCodec(string codec)
        {
            var encoder = codec switch
            {
                "aac" => SupportsEncoder("libfdk_aac") ? "libfdk_aac" : "aac",
                "opus" => "libopus",
                _ => codec,
            };

            return new CodecCapabilityDto
            {
                Codec = codec,
                Encoder = encoder,
                IsHardware = false,
                IsSupported = SupportsEncoder(encoder),
                IsAllowedByAdmin = true,
            };
        }

        /// <summary>
        /// Probes FFmpeg for an encoder. Treats a probe failure (e.g. FFmpeg not yet validated)
        /// as "unavailable" rather than failing the whole request.
        /// </summary>
        private bool SupportsEncoder(string encoder)
        {
            try
            {
                return _mediaEncoder.SupportsEncoder(encoder);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TranscodeDownloader] Could not probe FFmpeg encoder {Encoder}", encoder);
                return false;
            }
        }

        private static bool IsAllowedByAdmin(string codec, EncodingOptions options) => codec switch
        {
            "hevc" => options.AllowHevcEncoding,
            "av1" => options.AllowAv1Encoding,
            _ => true,
        };

        private static string Describe(CodecCapabilitiesDto capabilities) => string.Join(
            ", ",
            capabilities.VideoCodecs.Concat(capabilities.AudioCodecs).Select(c => string.Concat(
                c.Codec,
                "=",
                c.Encoder,
                c.IsHardware ? " (hw" : " (sw",
                c.IsSupported ? ", available" : ", missing",
                c.IsAllowedByAdmin ? ")" : ", disabled by admin)")));
    }
}
