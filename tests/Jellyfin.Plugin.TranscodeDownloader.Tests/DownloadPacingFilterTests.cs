using System.Net;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TranscodeDownloader.Tests;

/// <summary>
/// Runs the real middleware in a real ASP.NET pipeline against a fake transcode manager. What matters most
/// here is how a download that must not be kept ends: by breaking the connection, never by a status code,
/// because the iOS app saves whatever 200 body it is handed as the file.
/// </summary>
public class DownloadPacingFilterTests : IDisposable
{
    private static readonly Guid ItemId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Device = "device-1";
    private const string PlaySession = "play-session-1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "td-filter-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ITranscodeManager> _transcodes = new();
    private readonly DownloadLimiter _limiter = new(NullLogger<DownloadLimiter>.Instance);
    private readonly Mock<INetworkManager> _network = new();

    public DownloadPacingFilterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public int Aborts { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborts++;
    }

    private sealed class Run
    {
        public DefaultHttpContext Context { get; init; } = null!;

        public Lifetime Lifetime { get; init; } = null!;

        public MemoryStream Body { get; init; } = null!;

        public int NextCalls { get; set; }
    }

    private DownloadSlot IssueSlot(Guid? item = null)
    {
        var result = _limiter.TryIssue(Guid.NewGuid(), item ?? ItemId, 10_000_000, TimeSpan.TicksPerSecond * 60, _dir);
        Assert.Equal(TicketOutcome.Granted, result.Outcome);
        return result.Slot!;
    }

    private TranscodingJob Job(bool exited, int exitCode, int outputLength)
    {
        var path = Path.Combine(_dir, "out.mp4");
        File.WriteAllBytes(path, new byte[outputLength]);
        return new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            Path = path,
            HasExited = exited,
            ExitCode = exitCode,
        };
    }

    private void Setup(TranscodingJob? job) => _transcodes.Setup(m => m.GetTranscodingJob(PlaySession)).Returns(job);

    /// <summary>Sends one GET through the filter; the "server" behind it writes <paramref name="bodyBytes"/> bytes.</summary>
    private async Task<Run> Send(string path, string query, int bodyBytes = 0, string method = "GET", Func<DefaultHttpContext, Task>? whileServing = null, bool throwFromServer = false)
    {
        var services = new ServiceCollection().AddSingleton(_transcodes.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        _network.Setup(n => n.IsInLocalNetwork(It.IsAny<IPAddress>())).Returns(false);
        var body = new MemoryStream();
        context.Response.Body = body;
        var lifetime = new Lifetime();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var run = new Run { Context = context, Lifetime = lifetime, Body = body };

        var filter = new DownloadPacingFilter(_limiter, _network.Object, NullLogger<DownloadPacingFilter>.Instance);
        var builder = new ApplicationBuilder(services);
        filter.Configure(app => app.Run(async ctx =>
        {
            run.NextCalls++;
            if (bodyBytes > 0) await ctx.Response.Body.WriteAsync(new byte[bodyBytes]);
            if (whileServing is not null) await whileServing(context);
            if (throwFromServer) throw new InvalidOperationException("server failed");
        }))(builder);

        await builder.Build()(context);
        return run;
    }

    private static string StreamPath(Guid? item = null) => $"/Videos/{(item ?? ItemId):N}/stream.mp4";

    private static string Query(string ticket, bool identify = true)
        => $"?TdTicket={ticket}" + (identify ? $"&PlaySessionId={PlaySession}&DeviceId={Device}" : string.Empty);

    // ---- tickets that do not exist ----

    [Fact]
    public async Task An_unknown_ticket_drops_the_connection_and_answers_nothing()
    {
        var run = await Send(StreamPath(), Query("no-such-ticket"));

        Assert.Equal(1, run.Lifetime.Aborts);
        Assert.Equal(0, run.NextCalls);                 // never reaches the transcoder
        Assert.Equal(200, run.Context.Response.StatusCode); // not turned into a 4xx the app would save as the file
        Assert.Equal(0, run.Body.Length);
    }

    [Fact]
    public async Task A_ticket_for_another_item_is_dropped_the_same_way()
    {
        var slot = IssueSlot();

        var run = await Send(StreamPath(Guid.NewGuid()), Query(slot.Ticket));

        Assert.Equal(1, run.Lifetime.Aborts);
        Assert.Equal(0, run.NextCalls);
        Assert.False(slot.Started);
    }

    [Fact]
    public async Task A_ticket_can_only_be_used_once()
    {
        var slot = IssueSlot();
        Setup(Job(true, 0, 100));
        await Send(StreamPath(), Query(slot.Ticket), bodyBytes: 100);

        var second = await Send(StreamPath(), Query(slot.Ticket), bodyBytes: 100);

        Assert.Equal(1, second.Lifetime.Aborts);
        Assert.Equal(0, second.NextCalls);
    }

    // ---- requests the filter must leave alone ----

    [Fact]
    public async Task Requests_without_a_ticket_pass_straight_through()
    {
        var run = await Send(StreamPath(), "?PlaySessionId=x", bodyBytes: 500);

        Assert.Equal(1, run.NextCalls);
        Assert.Equal(500, run.Body.Length);
        Assert.Equal(0, run.Lifetime.Aborts);
    }

    [Fact]
    public async Task Other_urls_with_a_ticket_parameter_pass_through_untouched()
    {
        var run = await Send("/Items/abc/PlaybackInfo", Query("whatever"), bodyBytes: 20);

        Assert.Equal(1, run.NextCalls);
        Assert.Equal(0, run.Lifetime.Aborts);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Only_GET_requests_are_considered(string method)
    {
        var slot = IssueSlot();

        var run = await Send(StreamPath(), Query(slot.Ticket), method: method);

        Assert.Equal(1, run.NextCalls);
        Assert.False(slot.Started);
        Assert.Equal(0, run.Lifetime.Aborts);
    }

    [Fact]
    public async Task Ordinary_downloads_of_the_original_file_are_not_paced_unless_enabled()
    {
        var run = await Send($"/Items/{ItemId:N}/Download", string.Empty, bodyBytes: 3_000_000);

        Assert.Equal(1, run.NextCalls);
        Assert.Equal(3_000_000, run.Body.Length);
    }

    // ---- finishing a ticketed download ----

    [Fact]
    public async Task A_download_that_ran_to_the_end_and_was_sent_in_full_is_kept()
    {
        var slot = IssueSlot();
        Setup(Job(exited: true, exitCode: 0, outputLength: 4_000));

        var run = await Send(StreamPath(), Query(slot.Ticket), bodyBytes: 4_000);

        Assert.Equal(0, run.Lifetime.Aborts);
        Assert.Equal(4_000, run.Body.Length);
        var result = _limiter.GetResult(slot.Ticket)!;
        Assert.True(result.Complete);
        Assert.Equal(slot.UserId, result.UserId);
        Assert.Equal(4_000, result.BytesSent);
        Assert.Equal(4_000, result.OutputBytes);
        Assert.Equal(60, result.RuntimeSeconds);
    }

    [Fact]
    public async Task The_transcode_is_killed_and_its_file_deleted_after_every_download_even_complete_ones()
    {
        var slot = IssueSlot();
        Setup(Job(true, 0, 10));

        await Send(StreamPath(), Query(slot.Ticket), bodyBytes: 10);

        _transcodes.Verify(m => m.KillTranscodingJobs(Device, PlaySession, It.Is<Func<string, bool>>(delete => delete("any-path"))), Times.Once);
    }

    [Fact]
    public async Task The_slot_is_free_again_once_the_download_has_ended()
    {
        var slot = IssueSlot();
        Setup(Job(true, 0, 10));

        await Send(StreamPath(), Query(slot.Ticket), bodyBytes: 10);

        Assert.Equal((0, 0), _limiter.Snapshot());
    }

    [Fact]
    public async Task A_download_cut_short_by_the_server_drops_the_connection_so_the_client_cannot_keep_a_partial_file()
    {
        var slot = IssueSlot();
        Setup(Job(exited: true, exitCode: 0, outputLength: 4_000));

        var run = await Send(StreamPath(), Query(slot.Ticket), bodyBytes: 3_999);

        Assert.Equal(1, run.Lifetime.Aborts);
        Assert.Equal(200, run.Context.Response.StatusCode);
        Assert.False(_limiter.GetResult(slot.Ticket)!.Complete);
    }

    [Fact]
    public async Task A_failed_ffmpeg_drops_the_connection_even_if_every_byte_it_wrote_was_sent()
    {
        var slot = IssueSlot();
        Setup(Job(exited: true, exitCode: 1, outputLength: 4_000));

        var run = await Send(StreamPath(), Query(slot.Ticket), bodyBytes: 4_000);

        Assert.Equal(1, run.Lifetime.Aborts);
        Assert.False(_limiter.GetResult(slot.Ticket)!.Complete);
    }

    [Fact]
    public async Task A_job_that_jellyfin_already_removed_counts_as_stopped_early()
    {
        var slot = IssueSlot();
        Setup(null);

        var run = await Send(StreamPath(), Query(slot.Ticket), bodyBytes: 4_000);

        Assert.Equal(1, run.Lifetime.Aborts);
        var result = _limiter.GetResult(slot.Ticket)!;
        Assert.False(result.Complete);
        Assert.Equal(0, result.OutputBytes);
    }

    [Fact]
    public async Task The_position_a_job_reports_plays_no_part_in_the_verdict()
    {
        // Jellyfin reads 0 here after a progressive job exits; the old rule trusted it and called good downloads bad.
        var slot = IssueSlot();
        var job = Job(exited: true, exitCode: 0, outputLength: 1_000);
        job.TranscodingPositionTicks = 0;
        Setup(job);

        var run = await Send(StreamPath(), Query(slot.Ticket), bodyBytes: 1_000);

        Assert.Equal(0, run.Lifetime.Aborts);
        Assert.True(_limiter.GetResult(slot.Ticket)!.Complete);
    }

    [Fact]
    public async Task A_client_that_already_left_is_not_aborted_again()
    {
        var slot = IssueSlot();
        Setup(Job(true, 0, 4_000));
        using var gone = new CancellationTokenSource();

        var run = await Send(StreamPath(), Query(slot.Ticket), bodyBytes: 1_000, whileServing: ctx =>
        {
            ctx.RequestAborted = gone.Token;
            gone.Cancel();
            return Task.CompletedTask;
        });

        Assert.Equal(0, run.Lifetime.Aborts);
        Assert.False(_limiter.GetResult(slot.Ticket)!.Complete);
    }

    [Fact]
    public async Task Without_a_play_session_and_device_nothing_is_recorded_and_nothing_is_dropped_but_the_slot_is_freed()
    {
        var slot = IssueSlot();

        var run = await Send(StreamPath(), Query(slot.Ticket, identify: false), bodyBytes: 100);

        Assert.Equal(0, run.Lifetime.Aborts);
        Assert.Null(_limiter.GetResult(slot.Ticket));
        Assert.Equal((0, 0), _limiter.Snapshot());
        _transcodes.Verify(m => m.KillTranscodingJobs(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<string, bool>>()), Times.Never);
    }

    [Fact]
    public async Task A_failure_while_cleaning_up_neither_throws_nor_drops_the_connection()
    {
        var slot = IssueSlot();
        _transcodes.Setup(m => m.GetTranscodingJob(PlaySession)).Throws(new InvalidOperationException("boom"));

        var run = await Send(StreamPath(), Query(slot.Ticket), bodyBytes: 100);

        Assert.Equal(0, run.Lifetime.Aborts);
        Assert.Equal(100, run.Body.Length);
        Assert.Equal((0, 0), _limiter.Snapshot());
    }

    [Fact]
    public async Task The_slot_is_freed_even_when_serving_the_file_fails()
    {
        var slot = IssueSlot();
        Setup(null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Send(StreamPath(), Query(slot.Ticket), throwFromServer: true));

        Assert.Equal((0, 0), _limiter.Snapshot());
    }

    [Fact]
    public async Task Route_forms_with_and_without_dashes_and_extension_are_all_recognised()
    {
        foreach (var path in new[]
                 {
                     $"/Videos/{ItemId:N}/stream.mp4",
                     $"/Videos/{ItemId:D}/stream.mp4",
                     $"/Videos/{ItemId:N}/stream",
                     $"/videos/{ItemId:N}/STREAM.MKV",
                 })
        {
            var slot = IssueSlot();
            Setup(Job(true, 0, 5));

            var run = await Send(path, Query(slot.Ticket), bodyBytes: 5);

            Assert.True(slot.Started, path);
            Assert.Equal(0, run.Lifetime.Aborts);
        }
    }
}
