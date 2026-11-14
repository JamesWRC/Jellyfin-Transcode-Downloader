using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeDownloader
{
    /// <summary>One download's claim on the server: issued as a ticket, running once its stream starts.</summary>
    public sealed class DownloadSlot
    {
        private long _bytesSent;

        public DownloadSlot(string ticket, Guid userId, Guid itemId, long estimatedBytes, long runtimeTicks, TokenBucket bucket)
        {
            Ticket = ticket;
            UserId = userId;
            ItemId = itemId;
            EstimatedBytes = estimatedBytes;
            RuntimeTicks = runtimeTicks;
            Bucket = bucket;
        }

        public string Ticket { get; }

        public Guid UserId { get; }

        public Guid ItemId { get; }

        /// <summary>Estimated output size including the VBR margin.</summary>
        public long EstimatedBytes { get; }

        /// <summary>The item's runtime, to judge whether the encode reached the end.</summary>
        public long RuntimeTicks { get; }

        /// <summary>This download's own rate limit.</summary>
        public TokenBucket Bucket { get; }

        public DateTime IssuedUtc { get; init; } = DateTime.UtcNow;

        /// <summary>Stream requests currently using this slot (a browser may retry or overlap one).</summary>
        public int InFlight { get; set; }

        public bool Started { get; set; }

        public long BytesSent => Interlocked.Read(ref _bytesSent);

        public void AddBytes(long count) => Interlocked.Add(ref _bytesSent, count);
    }

    /// <summary>How a ticketed download ended, as the server saw it.</summary>
    public sealed class DownloadResult
    {
        public Guid UserId { get; init; }

        /// <summary>The encode reached the end of the item and the client was sent all of it.</summary>
        public bool Complete { get; init; }

        public double EncodedSeconds { get; init; }

        public double RuntimeSeconds { get; init; }

        public long BytesSent { get; init; }

        public long OutputBytes { get; init; }

        public DateTime RecordedUtc { get; init; } = DateTime.UtcNow;
    }

    public enum TicketOutcome
    {
        Granted,
        UserBusy,
        ServerBusy,
        NoSpace,
    }

    public sealed class TicketResult
    {
        public TicketOutcome Outcome { get; init; }

        public DownloadSlot? Slot { get; init; }

        public long NeededBytes { get; init; }

        public long FreeBytes { get; init; }
    }

    /// <summary>
    /// Server-wide bookkeeping for transcoded downloads: who may start one (slots and free
    /// transcode space), and the shared transfer budget every paced download draws from.
    /// In memory only; a restart forgets every ticket, which only cancels downloads that the
    /// restart has already killed anyway.
    /// </summary>
    public sealed class DownloadLimiter
    {
        /// <summary>How long an issued ticket waits for its stream request before it is dropped.</summary>
        public static readonly TimeSpan TicketTtl = TimeSpan.FromMinutes(2);

        private readonly object _lock = new object();
        private readonly List<DownloadSlot> _slots = new List<DownloadSlot>();
        private readonly Dictionary<string, DownloadResult> _results = new Dictionary<string, DownloadResult>();
        private readonly ILogger<DownloadLimiter> _logger;
        private readonly Func<PluginConfiguration> _config;
        private readonly Func<string, long> _freeBytes;
        private readonly Func<DateTime> _utcNow;

        public DownloadLimiter(ILogger<DownloadLimiter> logger)
            : this(logger, null, null, null)
        {
        }

        /// <summary>
        /// Same limiter with its configuration, free-space probe and clock replaceable, so the slot
        /// and space rules can be tested without a server or a disk. A null argument means the real
        /// thing: the plugin's configuration, the transcode folder's drive, and the system clock.
        /// </summary>
        public DownloadLimiter(ILogger<DownloadLimiter> logger, Func<PluginConfiguration>? config, Func<string, long>? freeBytes, Func<DateTime>? utcNow)
        {
            _logger = logger;
            _config = config ?? (() => Config);
            _freeBytes = freeBytes ?? GetFreeBytes;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            TotalBucket = new TokenBucket(() => ToBytesPerSecond(_config().TotalMbps));
        }

        public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        /// <summary>Shared by every paced download (and paced native downloads).</summary>
        public TokenBucket TotalBucket { get; }

        public static double ToBytesPerSecond(double mbps) => mbps > 0 ? mbps * 1_000_000 / 8 : 0;

        /// <summary>The rate one download gets when it is alone: the tighter of the two limits.</summary>
        public static double EffectiveRateBytesPerSecond()
        {
            var config = Config;
            var per = ToBytesPerSecond(config.PerDownloadMbps);
            var total = ToBytesPerSecond(config.TotalMbps);
            if (per <= 0) return total;
            if (total <= 0) return per;
            return Math.Min(per, total);
        }

        public TokenBucket NewDownloadBucket() => new TokenBucket(() => ToBytesPerSecond(_config().PerDownloadMbps));

        public TicketResult TryIssue(Guid userId, Guid itemId, long estimatedBytes, long runtimeTicks, string transcodePath)
        {
            var config = _config();
            lock (_lock)
            {
                PurgeExpired();

                if (config.MaxPerUser > 0 && _slots.Count(s => s.UserId == userId) >= config.MaxPerUser)
                {
                    return new TicketResult { Outcome = TicketOutcome.UserBusy };
                }

                if (config.MaxGlobal > 0 && _slots.Count >= config.MaxGlobal)
                {
                    return new TicketResult { Outcome = TicketOutcome.ServerBusy };
                }

                // What the running downloads have still to write, plus this one, plus headroom.
                var free = _freeBytes(transcodePath);
                var pending = _slots.Sum(s => Math.Max(0, s.EstimatedBytes - s.BytesSent));
                var needed = estimatedBytes + pending + (long)(Math.Max(0, config.HeadroomGb) * 1_000_000_000);
                if (free >= 0 && free < needed)
                {
                    return new TicketResult { Outcome = TicketOutcome.NoSpace, NeededBytes = needed, FreeBytes = free };
                }

                var slot = new DownloadSlot(Guid.NewGuid().ToString("N"), userId, itemId, estimatedBytes, runtimeTicks, NewDownloadBucket())
                {
                    IssuedUtc = _utcNow(),
                };
                _slots.Add(slot);
                return new TicketResult { Outcome = TicketOutcome.Granted, Slot = slot, FreeBytes = free };
            }
        }

        /// <summary>Claims the slot for a stream request. Null when the ticket is unknown, expired or for another item.</summary>
        public DownloadSlot? TryStart(string ticket, Guid itemId)
        {
            lock (_lock)
            {
                PurgeExpired();
                var slot = _slots.FirstOrDefault(s => s.Ticket == ticket);
                if (slot is null || slot.ItemId != itemId) return null;
                slot.Started = true;
                slot.InFlight++;
                return slot;
            }
        }

        /// <summary>Ends one stream request. True when it was the slot's last one (the slot is then freed).</summary>
        public bool Release(DownloadSlot slot)
        {
            lock (_lock)
            {
                slot.InFlight--;
                if (slot.InFlight > 0) return false;
                _slots.Remove(slot);
                return true;
            }
        }

        public void RecordResult(string ticket, DownloadResult result)
        {
            lock (_lock)
            {
                var cutoff = _utcNow() - TimeSpan.FromMinutes(30);
                foreach (var stale in _results.Where(r => r.Value.RecordedUtc < cutoff).Select(r => r.Key).ToList())
                {
                    _results.Remove(stale);
                }

                _results[ticket] = result;
            }
        }

        public DownloadResult? GetResult(string ticket)
        {
            lock (_lock)
            {
                return _results.TryGetValue(ticket, out var result) ? result : null;
            }
        }

        public (int Slots, int Running) Snapshot()
        {
            lock (_lock)
            {
                return (_slots.Count, _slots.Count(s => s.InFlight > 0));
            }
        }

        private void PurgeExpired()
        {
            var cutoff = _utcNow() - TicketTtl;
            _slots.RemoveAll(s => !s.Started && s.IssuedUtc < cutoff);
        }

        /// <summary>Free bytes on the filesystem holding the transcode folder; -1 when unknown (the check is skipped).</summary>
        private long GetFreeBytes(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                return new DriveInfo(path).AvailableFreeSpace;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TranscodeDownloader] Could not read free space for {Path}; skipping the space check", path);
                return -1;
            }
        }
    }
}
