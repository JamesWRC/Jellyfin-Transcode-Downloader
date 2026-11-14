using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Xunit;

namespace Jellyfin.Plugin.TranscodeDownloader.Tests;

public class ClientScriptTests
{
    // ---- Jellyfin 12 authentication ----

    [Fact]
    public void A_token_goes_in_an_Authorization_header_in_the_MediaBrowser_scheme()
    {
        var engine = ClientScript.Functions(new[] { "authHeaders" });

        var headers = engine.Evaluate("JSON.stringify(authHeaders('abc123'))").AsString();

        Assert.Equal("{\"Authorization\":\"MediaBrowser Token=\\\"abc123\\\"\"}", headers);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("undefined")]
    [InlineData("''")]
    public void Without_a_token_no_header_is_sent_at_all(string token)
    {
        var engine = ClientScript.Functions(new[] { "authHeaders" });

        Assert.Equal("{}", engine.Evaluate($"JSON.stringify(authHeaders({token}))").AsString());
    }

    [Fact]
    public void The_token_is_never_put_in_a_url_or_sent_in_a_legacy_header()
    {
        // Jellyfin 12 rejects ?api_key= and X-Emby-Token with 401; only the Authorization header works.
        var code = ClientScript.Source.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(line => line.Contains("//") && !line.Contains("://") ? line[..line.IndexOf("//", StringComparison.Ordinal)] : line);

        // The hand-off URL carries the literal placeholder "ApiKey=unused" because the app insists on the parameter.
        foreach (var line in code.Select(l => l.Replace("ApiKey=unused", string.Empty, StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("api_key", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ApiKey=", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("X-Emby-Token", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("X-MediaBrowser-Token", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Apple devices get only formats Apple's players can open ----

    private const string IPhone = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
    private const string IPad = "Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
    private const string MacSafari = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15";
    private const string Android = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36";
    private const string Windows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    private static readonly string[] AppleFunctions = { "isIosApp", "isApplePlayback", "codecBlockReason" };

    private static Engine Apple(string userAgent, string platform = "", int touchPoints = 0, bool iosApp = false, object? capabilities = null)
    {
        object window = iosApp ? new { NativeShell = new { downloadFile = "fn" }, ExpoAppInfo = new { } } : new { };
        return ClientScript.Functions(AppleFunctions, new[] { "APPLE_PLAYABLE" },
            new { userAgent, platform, maxTouchPoints = touchPoints }, window, capabilities);
    }

    private static string? Reason(Engine engine, string kind, string id)
    {
        var value = engine.Evaluate($"codecBlockReason('{kind}', '{id}')");
        return value.IsNull() ? null : value.AsString();
    }

    [Theory]
    [InlineData(IPhone, "iPhone", 5, false)]
    [InlineData(IPad, "iPad", 5, false)]
    [InlineData(MacSafari, "MacIntel", 5, false)]   // iPadOS Safari posing as a Mac: touch gives it away
    [InlineData(MacSafari, "MacIntel", 0, true)]    // the iOS app, whatever it claims to be
    [InlineData(Windows, "Win32", 0, true)]
    public void Apple_devices_are_only_offered_H264_and_AAC(string userAgent, string platform, int touchPoints, bool iosApp)
    {
        var engine = Apple(userAgent, platform, touchPoints, iosApp);

        Assert.Equal("apple", Reason(engine, "video", "hevc"));
        Assert.Equal("apple", Reason(engine, "video", "av1"));
        Assert.Equal("apple", Reason(engine, "audio", "opus"));
        Assert.Null(Reason(engine, "video", "h264"));
        Assert.Null(Reason(engine, "audio", "aac"));
    }

    [Theory]
    [InlineData(Android, "Linux armv81", 5)]
    [InlineData(Windows, "Win32", 0)]
    [InlineData(MacSafari, "MacIntel", 0)]          // a real Mac has no touch screen
    [InlineData(MacSafari, "MacIntel", 1)]          // one reported touch point is not an iPad
    public void Other_devices_may_pick_any_codec(string userAgent, string platform, int touchPoints)
    {
        var engine = Apple(userAgent, platform, touchPoints);

        foreach (var (kind, id) in new[] { ("video", "h264"), ("video", "hevc"), ("video", "av1"), ("audio", "aac"), ("audio", "opus") })
        {
            Assert.Null(Reason(engine, kind, id));
        }
    }

    [Fact]
    public void The_Apple_rule_wins_over_what_the_server_says_it_can_encode()
    {
        var capabilities = new
        {
            video = new { hevc = new { allowed = true, supported = true }, h264 = new { allowed = true, supported = true } },
            audio = new { opus = new { allowed = true, supported = true }, aac = new { allowed = true, supported = true } },
        };
        var engine = Apple(IPhone, "iPhone", 5, capabilities: capabilities);

        Assert.Equal("apple", Reason(engine, "video", "hevc"));
        Assert.Equal("apple", Reason(engine, "audio", "opus"));
        Assert.Null(Reason(engine, "video", "h264"));
    }

    [Fact]
    public void On_Apple_a_server_that_cannot_encode_H264_still_blocks_it_for_the_server_reason()
    {
        var capabilities = new
        {
            video = new { h264 = new { allowed = true, supported = false } },
            audio = new { aac = new { allowed = false, supported = true } },
        };
        var engine = Apple(IPhone, "iPhone", 5, capabilities: capabilities);

        Assert.Equal("unsupported", Reason(engine, "video", "h264"));
        Assert.Equal("admin", Reason(engine, "audio", "aac"));
    }

    [Fact]
    public void Off_Apple_the_server_capabilities_decide()
    {
        var capabilities = new
        {
            video = new
            {
                hevc = new { allowed = true, supported = true },
                av1 = new { allowed = false, supported = true },
                h264 = new { allowed = true, supported = false },
            },
            audio = new { },
        };
        var engine = Apple(Windows, "Win32", 0, capabilities: capabilities);

        Assert.Null(Reason(engine, "video", "hevc"));
        Assert.Equal("admin", Reason(engine, "video", "av1"));
        Assert.Equal("unsupported", Reason(engine, "video", "h264"));
        Assert.Equal("unsupported", Reason(engine, "audio", "opus")); // not listed at all
    }

    [Fact]
    public void The_apple_whitelist_is_exactly_H264_and_AAC()
    {
        var engine = ClientScript.Functions(Array.Empty<string>(), new[] { "APPLE_PLAYABLE" });

        Assert.Equal("{\"video\":[\"h264\"],\"audio\":[\"aac\"]}", engine.Evaluate("JSON.stringify(APPLE_PLAYABLE)").AsString());
    }

    // ---- the shipped script itself ----

    [Fact]
    public void The_script_parses()
    {
        var error = Record.Exception(() => Engine.PrepareScript(ClientScript.Source));

        Assert.Null(error);
    }

    [Fact]
    public void The_script_stays_one_closure_so_it_leaks_no_globals()
    {
        var source = ClientScript.Source.TrimStart();

        Assert.StartsWith("(() => {", source);
        Assert.EndsWith("})();", source.TrimEnd());
    }

    [Fact]
    public void Every_string_the_script_asks_for_exists_in_the_English_bundle_and_the_built_in_fallbacks()
    {
        var bundle = JsonSerializer.Deserialize<Dictionary<string, string>>(ClientScript.ReadResource("strings.en-us.json"))!;
        var used = Regex.Matches(ClientScript.Source, @"\bt\('(?<key>[A-Za-z0-9_]+)'\)").Select(m => m.Groups["key"].Value).Distinct().ToList();
        var fallbackBlock = ClientScript.Source[ClientScript.Source.IndexOf("FALLBACK_STRINGS = {", StringComparison.Ordinal)..];
        fallbackBlock = fallbackBlock[..fallbackBlock.IndexOf("};", StringComparison.Ordinal)];

        Assert.NotEmpty(used);
        foreach (var key in used)
        {
            Assert.True(bundle.ContainsKey(key), $"en-us.json lacks '{key}'");
            Assert.Contains(key + ":", fallbackBlock);
        }
    }

    [Theory]
    [InlineData("en-us")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("zh-cn")]
    [InlineData("nl")]
    public void Each_language_bundle_is_valid_json_with_only_known_keys(string locale)
    {
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(ClientScript.ReadResource("strings.en-us.json"))!;
        var bundle = JsonSerializer.Deserialize<Dictionary<string, string>>(ClientScript.ReadResource($"strings.{locale}.json"))!;

        Assert.NotEmpty(bundle);
        Assert.Empty(bundle.Keys.Except(english.Keys));
    }
}
