namespace Jellyfin.Plugin.TranscodeDownloader
{
    /// <summary>
    /// The rule that decides whether a finished ticketed download is a whole file. Kept free of
    /// Jellyfin types so it can be tested on its own.
    /// </summary>
    public static class DownloadCompleteness
    {
        /// <summary>
        /// True only when the encode ran to the end and the client was sent all of it.
        /// Jellyfin does not keep a progressive job's position once ffmpeg exits (it reads 0), so
        /// the position is never used. A job Jellyfin stopped early (a kill request, the kill
        /// timer) is removed from its job list; one whose ffmpeg ran to the end stays in it,
        /// exited, until someone kills it.
        /// </summary>
        /// <param name="jobPresent">The job is still in Jellyfin's job list.</param>
        /// <param name="hasExited">ffmpeg has exited.</param>
        /// <param name="exitCode">ffmpeg's exit code, when it has one.</param>
        /// <param name="bytesSent">Bytes written to the client.</param>
        /// <param name="outputBytes">Length of the transcode's output file (0 when there is none).</param>
        public static bool IsComplete(bool jobPresent, bool hasExited, int? exitCode, long bytesSent, long outputBytes)
        {
            var encodedToEnd = jobPresent && hasExited && exitCode == 0;
            var allSent = outputBytes > 0 && bytesSent >= outputBytes;
            return encodedToEnd && allSent;
        }
    }
}
