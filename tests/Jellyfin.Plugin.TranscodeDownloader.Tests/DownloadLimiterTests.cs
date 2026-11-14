using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TranscodeDownloader.Tests;

public class DownloadLimiterTests
{
    private const long Gb = 1_000_000_000;

    private sealed class Rig
    {
        public PluginConfiguration Config { get; } = new();

        public long Free { get; set; } = 1_000 * Gb;

        public DateTime Now { get; set; } = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        public DownloadLimiter Limiter { get; }

        public Rig()
        {
            Limiter = new DownloadLimiter(NullLogger<DownloadLimiter>.Instance, () => Config, _ => Free, () => Now);
        }

        public TicketResult Issue(Guid? user = null, long estimated = Gb, Guid? item = null)
            => Limiter.TryIssue(user ?? Guid.NewGuid(), item ?? Guid.NewGuid(), estimated, 0, "/transcodes");
    }

    // ---- defaults ----

    [Fact]
    public void Defaults_are_30_Mbps_each_60_total_lan_exempt_3_slots_1_per_user_2_GB_headroom()
    {
        var config = new PluginConfiguration();

        Assert.Equal(30, config.PerDownloadMbps);
        Assert.Equal(60, config.TotalMbps);
        Assert.True(config.ExemptLan);
        Assert.Equal(3, config.MaxGlobal);
        Assert.Equal(1, config.MaxPerUser);
        Assert.Equal(2, config.HeadroomGb);
        Assert.False(config.PaceNativeDownloads);
    }

    [Fact]
    public void Without_a_loaded_plugin_the_limiter_runs_on_those_defaults()
    {
        Assert.Null(Plugin.Instance);
        var config = DownloadLimiter.Config;

        Assert.Equal(30, config.PerDownloadMbps);
        Assert.Equal(60, config.TotalMbps);
        Assert.Equal(3, config.MaxGlobal);
        Assert.Equal(1, config.MaxPerUser);
    }

    [Fact]
    public void It_resolves_from_the_service_container_the_way_the_plugin_registers_it()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<DownloadLimiter>();

        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<DownloadLimiter>(), provider.GetRequiredService<DownloadLimiter>());
    }

    // ---- rate maths ----

    [Theory]
    [InlineData(8, 1_000_000)]
    [InlineData(30, 3_750_000)]
    [InlineData(60, 7_500_000)]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    public void Mbps_convert_to_bytes_per_second(double mbps, double expected)
        => Assert.Equal(expected, DownloadLimiter.ToBytesPerSecond(mbps));

    [Fact]
    public void A_lone_download_gets_the_tighter_of_the_two_default_limits()
        => Assert.Equal(3_750_000, DownloadLimiter.EffectiveRateBytesPerSecond());

    [Fact]
    public async Task Buckets_follow_the_configuration_while_a_download_runs()
    {
        var rig = new Rig();
        rig.Config.PerDownloadMbps = 8;
        var bucket = rig.Limiter.NewDownloadBucket();

        Assert.False(bucket.TakeAsync(1_000_000_000, CancellationToken.None).IsCompleted); // ~1000 s at 1 MB/s

        rig.Config.PerDownloadMbps = 0;                                                    // admin lifts the limit
        await bucket.TakeAsync(1_000_000_000, CancellationToken.None);
    }

    [Fact]
    public async Task The_shared_bucket_follows_the_total_limit()
    {
        var rig = new Rig();
        rig.Config.TotalMbps = 0;

        await rig.Limiter.TotalBucket.TakeAsync(1_000_000_000, CancellationToken.None);

        rig.Config.TotalMbps = 8;
        Assert.False(rig.Limiter.TotalBucket.TakeAsync(1_000_000_000, CancellationToken.None).IsCompleted);
    }

    // ---- slots ----

    [Fact]
    public void A_granted_ticket_carries_the_request_and_a_fresh_unguessable_id()
    {
        var rig = new Rig();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();

        var result = rig.Limiter.TryIssue(user, item, 5 * Gb, 123, "/transcodes");

        Assert.Equal(TicketOutcome.Granted, result.Outcome);
        var slot = result.Slot!;
        Assert.Equal(user, slot.UserId);
        Assert.Equal(item, slot.ItemId);
        Assert.Equal(5 * Gb, slot.EstimatedBytes);
        Assert.Equal(123, slot.RuntimeTicks);
        Assert.Equal(rig.Now, slot.IssuedUtc);
        Assert.Matches("^[0-9a-f]{32}$", slot.Ticket);
        Assert.NotEqual(slot.Ticket, rig.Issue().Slot!.Ticket);
    }

    [Fact]
    public void One_download_per_user_by_default()
    {
        var rig = new Rig();
        var user = Guid.NewGuid();

        Assert.Equal(TicketOutcome.Granted, rig.Issue(user).Outcome);
        Assert.Equal(TicketOutcome.UserBusy, rig.Issue(user).Outcome);
        Assert.Equal(TicketOutcome.Granted, rig.Issue(Guid.NewGuid()).Outcome);
    }

    [Fact]
    public void A_per_user_limit_of_two_allows_two()
    {
        var rig = new Rig();
        rig.Config.MaxPerUser = 2;
        rig.Config.MaxGlobal = 0;
        var user = Guid.NewGuid();

        Assert.Equal(TicketOutcome.Granted, rig.Issue(user).Outcome);
        Assert.Equal(TicketOutcome.Granted, rig.Issue(user).Outcome);
        Assert.Equal(TicketOutcome.UserBusy, rig.Issue(user).Outcome);
    }

    [Fact]
    public void Three_downloads_at_once_server_wide_by_default()
    {
        var rig = new Rig();

        for (var i = 0; i < 3; i++) Assert.Equal(TicketOutcome.Granted, rig.Issue().Outcome);

        Assert.Equal(TicketOutcome.ServerBusy, rig.Issue().Outcome);
    }

    [Fact]
    public void The_user_limit_is_reported_before_the_server_limit()
    {
        var rig = new Rig();
        var user = Guid.NewGuid();
        rig.Config.MaxGlobal = 1;

        rig.Issue(user);

        Assert.Equal(TicketOutcome.UserBusy, rig.Issue(user).Outcome);
        Assert.Equal(TicketOutcome.ServerBusy, rig.Issue(Guid.NewGuid()).Outcome);
    }

    [Fact]
    public void Zero_means_unlimited_for_both_slot_limits()
    {
        var rig = new Rig();
        rig.Config.MaxGlobal = 0;
        rig.Config.MaxPerUser = 0;
        var user = Guid.NewGuid();

        for (var i = 0; i < 25; i++) Assert.Equal(TicketOutcome.Granted, rig.Issue(user).Outcome);
    }

    [Fact]
    public void Limits_changed_in_the_settings_apply_to_the_next_request()
    {
        var rig = new Rig();
        var user = Guid.NewGuid();
        rig.Issue(user);
        Assert.Equal(TicketOutcome.UserBusy, rig.Issue(user).Outcome);

        rig.Config.MaxPerUser = 2;

        Assert.Equal(TicketOutcome.Granted, rig.Issue(user).Outcome);
    }

    [Fact]
    public void A_refusal_hands_out_no_slot()
    {
        var rig = new Rig();
        var user = Guid.NewGuid();
        rig.Issue(user);

        var refused = rig.Issue(user);

        Assert.Equal(TicketOutcome.UserBusy, refused.Outcome);
        Assert.Null(refused.Slot);
        Assert.Equal((1, 0), rig.Limiter.Snapshot());
    }

    // ---- starting and releasing ----

    [Fact]
    public void Starting_a_slot_marks_it_running()
    {
        var rig = new Rig();
        var slot = rig.Issue().Slot!;

        var started = rig.Limiter.TryStart(slot.Ticket, slot.ItemId);

        Assert.Same(slot, started);
        Assert.True(slot.Started);
        Assert.Equal(1, slot.InFlight);
        Assert.Equal((1, 1), rig.Limiter.Snapshot());
    }

    [Fact]
    public void An_unknown_ticket_or_one_for_another_item_does_not_start()
    {
        var rig = new Rig();
        var slot = rig.Issue().Slot!;

        Assert.Null(rig.Limiter.TryStart("not-a-ticket", slot.ItemId));
        Assert.Null(rig.Limiter.TryStart(slot.Ticket, Guid.NewGuid()));
        Assert.Null(rig.Limiter.TryStart(string.Empty, slot.ItemId));

        Assert.False(slot.Started);
        Assert.Equal(0, slot.InFlight);
    }

    [Fact]
    public void Releasing_the_last_request_frees_the_slot_for_the_user_again()
    {
        var rig = new Rig();
        var user = Guid.NewGuid();
        var slot = rig.Issue(user).Slot!;
        rig.Limiter.TryStart(slot.Ticket, slot.ItemId);
        Assert.Equal(TicketOutcome.UserBusy, rig.Issue(user).Outcome);

        var freed = rig.Limiter.Release(slot);

        Assert.True(freed);
        Assert.Equal((0, 0), rig.Limiter.Snapshot());
        Assert.Equal(TicketOutcome.Granted, rig.Issue(user).Outcome);
    }

    [Fact]
    public void A_released_ticket_cannot_be_used_again()
    {
        var rig = new Rig();
        var slot = rig.Issue().Slot!;
        rig.Limiter.TryStart(slot.Ticket, slot.ItemId);
        rig.Limiter.Release(slot);

        Assert.Null(rig.Limiter.TryStart(slot.Ticket, slot.ItemId));
    }

    [Fact]
    public void Overlapping_requests_share_one_slot_which_frees_only_when_the_last_ends()
    {
        var rig = new Rig();
        var slot = rig.Issue().Slot!;
        rig.Limiter.TryStart(slot.Ticket, slot.ItemId);
        rig.Limiter.TryStart(slot.Ticket, slot.ItemId); // a browser retry while the first is still open

        Assert.False(rig.Limiter.Release(slot));
        Assert.Equal((1, 1), rig.Limiter.Snapshot());
        Assert.True(rig.Limiter.Release(slot));
        Assert.Equal((0, 0), rig.Limiter.Snapshot());
    }

    [Fact]
    public void A_ticket_nobody_uses_expires_after_two_minutes_and_frees_its_slot()
    {
        var rig = new Rig();
        var user = Guid.NewGuid();
        var slot = rig.Issue(user).Slot!;

        rig.Now += DownloadLimiter.TicketTtl - TimeSpan.FromSeconds(1);
        Assert.Equal(TicketOutcome.UserBusy, rig.Issue(user).Outcome);

        rig.Now += TimeSpan.FromSeconds(2);
        Assert.Null(rig.Limiter.TryStart(slot.Ticket, slot.ItemId));
        Assert.Equal(TicketOutcome.Granted, rig.Issue(user).Outcome);
    }

    [Fact]
    public void The_ticket_lifetime_is_two_minutes()
        => Assert.Equal(TimeSpan.FromMinutes(2), DownloadLimiter.TicketTtl);

    [Fact]
    public void A_running_download_never_expires()
    {
        var rig = new Rig();
        var user = Guid.NewGuid();
        var slot = rig.Issue(user).Slot!;
        rig.Limiter.TryStart(slot.Ticket, slot.ItemId);

        rig.Now += TimeSpan.FromHours(6);

        Assert.Equal(TicketOutcome.UserBusy, rig.Issue(user).Outcome);
        Assert.Equal((1, 1), rig.Limiter.Snapshot());
    }

    // ---- space guard ----

    [Fact]
    public void A_download_fits_when_the_estimate_plus_headroom_is_free()
    {
        var rig = new Rig { Free = 10 * Gb };

        Assert.Equal(TicketOutcome.Granted, rig.Issue(estimated: 8 * Gb).Outcome); // 8 + 2 headroom = 10, exactly free
    }

    [Fact]
    public void A_download_is_refused_when_it_would_eat_into_the_headroom()
    {
        var rig = new Rig { Free = 10 * Gb };

        var result = rig.Issue(estimated: 8 * Gb + 1);

        Assert.Equal(TicketOutcome.NoSpace, result.Outcome);
        Assert.Null(result.Slot);
        Assert.Equal(10 * Gb + 1, result.NeededBytes);
        Assert.Equal(10 * Gb, result.FreeBytes);
    }

    [Fact]
    public void The_headroom_is_configurable_and_a_negative_one_counts_as_none()
    {
        var rig = new Rig { Free = 10 * Gb };
        rig.Config.HeadroomGb = 0.5;
        Assert.Equal(TicketOutcome.Granted, rig.Issue(estimated: 9 * Gb + Gb / 2).Outcome);

        var other = new Rig { Free = 10 * Gb };
        other.Config.HeadroomGb = -5;
        other.Config.MaxPerUser = 0;
        Assert.Equal(TicketOutcome.Granted, other.Issue(estimated: 10 * Gb).Outcome);
        Assert.Equal(TicketOutcome.NoSpace, other.Issue(estimated: 1).Outcome);
    }

    [Fact]
    public void What_running_downloads_still_have_to_write_is_reserved()
    {
        var rig = new Rig { Free = 20 * Gb };
        var first = rig.Issue(estimated: 5 * Gb).Slot!;

        // 14 (this) + 5 (first, nothing sent yet) + 2 headroom = 21 > 20
        Assert.Equal(TicketOutcome.NoSpace, rig.Issue(estimated: 14 * Gb).Outcome);

        // Once the first has sent 4 GB only 1 GB of it is still to come: 14 + 1 + 2 = 17 <= 20
        first.AddBytes(4 * Gb);
        Assert.Equal(TicketOutcome.Granted, rig.Issue(estimated: 14 * Gb).Outcome);
    }

    [Fact]
    public void A_download_that_ran_past_its_estimate_reserves_nothing_rather_than_a_negative_amount()
    {
        var rig = new Rig { Free = 20 * Gb };
        var first = rig.Issue(estimated: 5 * Gb).Slot!;
        first.AddBytes(8 * Gb);

        // Its remainder counts as 0, not -3 GB: 18 + 0 + 2 = 20 fits exactly, and anything more must not.
        Assert.Equal(TicketOutcome.NoSpace, rig.Issue(estimated: 18 * Gb + Gb / 2).Outcome);
        Assert.Equal(TicketOutcome.Granted, rig.Issue(estimated: 18 * Gb).Outcome);
    }

    [Fact]
    public void Finished_downloads_stop_reserving_space()
    {
        var rig = new Rig { Free = 10 * Gb };
        var slot = rig.Issue(estimated: 8 * Gb).Slot!;
        Assert.Equal(TicketOutcome.NoSpace, rig.Issue(estimated: 2 * Gb).Outcome);

        rig.Limiter.TryStart(slot.Ticket, slot.ItemId);
        rig.Limiter.Release(slot);

        Assert.Equal(TicketOutcome.Granted, rig.Issue(estimated: 8 * Gb).Outcome);
    }

    [Fact]
    public void Unknown_free_space_skips_the_check()
    {
        var rig = new Rig { Free = -1 };

        Assert.Equal(TicketOutcome.Granted, rig.Issue(estimated: long.MaxValue / 4).Outcome);
    }

    [Fact]
    public void A_refusal_for_space_does_not_use_up_the_users_slot()
    {
        var rig = new Rig { Free = 10 * Gb };
        var user = Guid.NewGuid();

        Assert.Equal(TicketOutcome.NoSpace, rig.Issue(user, estimated: 50 * Gb).Outcome);
        Assert.Equal(TicketOutcome.Granted, rig.Issue(user, estimated: Gb).Outcome);
    }

    [Fact]
    public void Slot_limits_are_checked_before_space()
    {
        var rig = new Rig { Free = 10 * Gb };
        var user = Guid.NewGuid();
        rig.Issue(user, estimated: Gb);

        Assert.Equal(TicketOutcome.UserBusy, rig.Issue(user, estimated: 500 * Gb).Outcome);
    }

    [Fact]
    public void The_space_probe_is_asked_about_the_transcode_folder()
    {
        string? asked = null;
        var limiter = new DownloadLimiter(NullLogger<DownloadLimiter>.Instance, () => new PluginConfiguration(), path => { asked = path; return 100 * Gb; }, null);

        limiter.TryIssue(Guid.NewGuid(), Guid.NewGuid(), Gb, 0, "/some/transcode/dir");

        Assert.Equal("/some/transcode/dir", asked);
    }

    [Fact]
    public void The_real_space_probe_reads_the_drive_and_refuses_an_impossible_estimate()
    {
        var limiter = new DownloadLimiter(NullLogger<DownloadLimiter>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), "td-limiter-" + Guid.NewGuid().ToString("N"));
        try
        {
            var small = limiter.TryIssue(Guid.NewGuid(), Guid.NewGuid(), 1, 0, dir);
            Assert.Equal(TicketOutcome.Granted, small.Outcome);
            Assert.True(small.FreeBytes > 0);

            var huge = limiter.TryIssue(Guid.NewGuid(), Guid.NewGuid(), long.MaxValue / 4, 0, dir);
            Assert.Equal(TicketOutcome.NoSpace, huge.Outcome);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_folder_that_cannot_be_probed_skips_the_space_check_instead_of_failing()
    {
        var limiter = new DownloadLimiter(NullLogger<DownloadLimiter>.Instance);
        var file = Path.GetTempFileName();
        try
        {
            // A path below a regular file cannot be created, so the probe throws.
            var result = limiter.TryIssue(Guid.NewGuid(), Guid.NewGuid(), long.MaxValue / 4, 0, Path.Combine(file, "sub"));

            Assert.Equal(TicketOutcome.Granted, result.Outcome);
            Assert.Equal(-1, result.FreeBytes);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- results ----

    [Fact]
    public void A_recorded_result_can_be_read_back_by_ticket()
    {
        var rig = new Rig();
        var result = new DownloadResult { UserId = Guid.NewGuid(), Complete = true, BytesSent = 7, RecordedUtc = rig.Now };

        rig.Limiter.RecordResult("abc", result);

        Assert.Same(result, rig.Limiter.GetResult("abc"));
        Assert.Null(rig.Limiter.GetResult("other"));
    }

    [Fact]
    public void Results_older_than_30_minutes_are_dropped_when_the_next_one_is_recorded()
    {
        var rig = new Rig();
        rig.Limiter.RecordResult("old", new DownloadResult { RecordedUtc = rig.Now - TimeSpan.FromMinutes(31) });
        rig.Limiter.RecordResult("recent", new DownloadResult { RecordedUtc = rig.Now - TimeSpan.FromMinutes(29) });

        rig.Limiter.RecordResult("new", new DownloadResult { RecordedUtc = rig.Now });

        Assert.Null(rig.Limiter.GetResult("old"));
        Assert.NotNull(rig.Limiter.GetResult("recent"));
        Assert.NotNull(rig.Limiter.GetResult("new"));
    }
}
