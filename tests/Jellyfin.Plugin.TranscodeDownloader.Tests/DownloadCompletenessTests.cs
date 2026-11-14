using Xunit;

namespace Jellyfin.Plugin.TranscodeDownloader.Tests;

/// <summary>
/// The rule that decides whether a finished download is a whole file. Each row changes one fact about an
/// otherwise complete download and says whether it still counts.
/// </summary>
public class DownloadCompletenessTests
{
    [Theory]
    [InlineData(true, true, 0, 1000, 1000, true)]    // the normal finish: job exited 0, every byte sent
    [InlineData(true, true, 0, 1001, 1000, true)]    // sent more than the file length (never shorter)
    [InlineData(false, true, 0, 1000, 1000, false)]  // job gone from the list: Jellyfin killed it early
    [InlineData(true, false, null, 1000, 1000, false)] // ffmpeg still running: not finished
    [InlineData(true, false, 0, 1000, 1000, false)]  // exit code without HasExited is not an exit
    [InlineData(true, true, 1, 1000, 1000, false)]   // ffmpeg failed
    [InlineData(true, true, 137, 1000, 1000, false)] // ffmpeg was killed
    [InlineData(true, true, -1, 1000, 1000, false)]
    [InlineData(true, true, null, 1000, 1000, false)] // exited with no code recorded
    [InlineData(true, true, 0, 999, 1000, false)]    // client missed the last byte
    [InlineData(true, true, 0, 0, 1000, false)]      // client received nothing
    [InlineData(true, true, 0, 0, 0, false)]         // no output file at all
    [InlineData(true, true, 0, 500, 0, false)]       // output file missing or empty although bytes went out
    [InlineData(false, false, null, 0, 0, false)]    // nothing known
    public void Complete_only_when_the_job_ran_to_the_end_and_every_byte_was_sent(
        bool jobPresent, bool hasExited, int? exitCode, long bytesSent, long outputBytes, bool expected)
        => Assert.Equal(expected, DownloadCompleteness.IsComplete(jobPresent, hasExited, exitCode, bytesSent, outputBytes));

    [Fact]
    public void The_exit_code_alone_decides_between_success_and_failure_for_an_exited_job()
    {
        for (var code = -2; code <= 255; code++)
        {
            Assert.Equal(code == 0, DownloadCompleteness.IsComplete(true, true, code, 10, 10));
        }
    }
}
