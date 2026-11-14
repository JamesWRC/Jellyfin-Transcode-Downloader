using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeDownloader
{
    /// <summary>
    /// Paces ticketed transcoded downloads (and, optionally, Jellyfin's own Download button).
    /// Only requests carrying <c>TdTicket</c> are touched; ordinary playback never is.
    /// </summary>
    public class DownloadPacingFilter : IStartupFilter
    {
        public const string TicketParameter = "TdTicket";

        private static readonly Regex StreamPath = new Regex(
            @"/Videos/(?<id>[0-9a-f]{32}|[0-9a-f-]{36})/stream(\.[a-z0-9]+)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex NativeDownloadPath = new Regex(
            @"/Items/(?<id>[0-9a-f]{32}|[0-9a-f-]{36})/Download$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly DownloadLimiter _limiter;
        private readonly INetworkManager _networkManager;
        private readonly ILogger<DownloadPacingFilter> _logger;

        public DownloadPacingFilter(DownloadLimiter limiter, INetworkManager networkManager, ILogger<DownloadPacingFilter> logger)
        {
            _limiter = limiter;
            _networkManager = networkManager;
            _logger = logger;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(InvokeAsync);
                next(app);
            };
        }

        private async Task InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var path = context.Request.Path.Value ?? string.Empty;

            if (context.Request.Query.TryGetValue(TicketParameter, out var ticketValues))
            {
                var match = StreamPath.Match(path);
                if (match.Success && Guid.TryParse(match.Groups["id"].Value, out var itemId))
                {
                    var slot = _limiter.TryStart(ticketValues.ToString(), itemId);
                    if (slot is null)
                    {
                        _logger.LogInformation("[TranscodeDownloader] Rejected a stream request with an unknown or expired ticket for item {ItemId}", itemId);
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsync("Download ticket is unknown or has expired.", context.RequestAborted).ConfigureAwait(false);
                        return;
                    }

                    try
                    {
                        await PacedAsync(context, next, slot.Bucket, slot, "transcode").ConfigureAwait(false);
                    }
                    finally
                    {
                        if (_limiter.Release(slot))
                        {
                            await FinishTranscodeAsync(context, slot).ConfigureAwait(false);
                        }
                    }

                    return;
                }
            }

            if (DownloadLimiter.Config.PaceNativeDownloads && NativeDownloadPath.IsMatch(path))
            {
                await PacedAsync(context, next, _limiter.NewDownloadBucket(), null, "native").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs when a ticketed download's last request ends, however it ended. Records whether it was
        /// complete, then kills the transcoding job and deletes its output. Jellyfin never deletes a
        /// progressive job's file once ffmpeg has exited, and the browser's own stop request cannot be
        /// relied on: a phone may freeze or kill the page first (seen with the iOS app, 2026-11-14).
        /// </summary>
        private async Task FinishTranscodeAsync(HttpContext context, DownloadSlot slot)
        {
            var playSessionId = context.Request.Query["PlaySessionId"].ToString();
            var deviceId = context.Request.Query["DeviceId"].ToString();
            if (string.IsNullOrEmpty(playSessionId) || string.IsNullOrEmpty(deviceId))
            {
                return;
            }

            try
            {
                var transcodeManager = context.RequestServices.GetService<ITranscodeManager>();
                if (transcodeManager is null) return;

                var job = transcodeManager.GetTranscodingJob(playSessionId);
                var runtime = slot.RuntimeTicks / (double)TimeSpan.TicksPerSecond;
                var encoded = (job?.TranscodingPositionTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
                long outputBytes = 0;
                if (job?.Path is not null && System.IO.File.Exists(job.Path))
                {
                    outputBytes = new System.IO.FileInfo(job.Path).Length;
                }

                // ffmpeg reports progress about twice a second, so at 30x the last report can trail
                // the real end by ~15 s of content; a stop part-way is minutes short.
                var encodedToEnd = job is not null && job.HasExited && (runtime <= 0 || encoded >= runtime - 60);
                var allSent = outputBytes > 0 && slot.BytesSent >= outputBytes;
                var result = new DownloadResult
                {
                    UserId = slot.UserId,
                    Known = job is not null,
                    Complete = encodedToEnd && allSent,
                    EncodedSeconds = Math.Round(encoded),
                    RuntimeSeconds = Math.Round(runtime),
                    BytesSent = slot.BytesSent,
                    OutputBytes = outputBytes,
                };
                _limiter.RecordResult(slot.Ticket, result);

                await transcodeManager.KillTranscodingJobs(deviceId, playSessionId, _ => true).ConfigureAwait(false);
                _logger.LogInformation(
                    "[TranscodeDownloader] Cleaned up transcode for ticket {Ticket}: complete={Complete} (encoded {Encoded:F0}/{Runtime:F0} s, sent {Sent}/{Output} bytes)",
                    slot.Ticket[..8],
                    result.Complete,
                    encoded,
                    runtime,
                    slot.BytesSent,
                    outputBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TranscodeDownloader] Cleanup after download {Ticket} failed", slot.Ticket[..8]);
            }
        }

        private async Task<PacedResponseStream> PacedAsync(HttpContext context, RequestDelegate next, TokenBucket own, DownloadSlot? slot, string kind)
        {
            var original = context.Response.Body;
            var paced = new PacedResponseStream(original, own, _limiter.TotalBucket, () => IsExempt(context), slot, context.RequestAborted);
            context.Response.Body = paced;
            var clock = Stopwatch.StartNew();
            var outcome = "completed";
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
                throw;
            }
            catch (Exception)
            {
                outcome = "failed";
                throw;
            }
            finally
            {
                context.Response.Body = original;
                var seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                _logger.LogInformation(
                    "[TranscodeDownloader] {Kind} download {Outcome}: {Megabytes:F1} MB in {Seconds:F0} s ({Mbps:F1} Mbps), paced={Paced}, client={Client}",
                    kind,
                    context.RequestAborted.IsCancellationRequested ? "cancelled" : outcome,
                    paced.Written / 1_000_000.0,
                    seconds,
                    paced.Written * 8 / seconds / 1_000_000,
                    paced.Exempt != true,
                    context.Connection.RemoteIpAddress);
            }

            return paced;
        }

        private bool IsExempt(HttpContext context)
        {
            if (!DownloadLimiter.Config.ExemptLan) return false;
            var address = context.Connection.RemoteIpAddress;
            if (address is null) return false;
            try
            {
                return _networkManager.IsInLocalNetwork(address);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TranscodeDownloader] Could not classify {Address}; pacing it", address);
                return false;
            }
        }
    }
}
