using System.Reflection;
using System.Text.Json;
using Jint;

namespace Jellyfin.Plugin.TranscodeDownloader.Tests;

/// <summary>
/// The shipped client script (web/plugin.js, read from the plugin assembly's embedded resource, so it is
/// exactly what a build ships) and a way to run its pure functions without a browser.
/// The script is one big closure that wires itself to the page as soon as it loads, so it cannot simply be
/// executed. <see cref="Functions"/> cuts named functions (and constants) out of the source by brace
/// matching and evaluates only those, with the few globals they read supplied by the test. A rename or
/// restructure of one of them fails loudly in <see cref="Extract"/> rather than silently testing nothing.
/// </summary>
internal static class ClientScript
{
    public const string ResourcePrefix = "Jellyfin.Plugin.TranscodeDownloader.web.";

    public static Assembly PluginAssembly => typeof(TokenBucket).Assembly;

    public static string ReadResource(string name)
    {
        using var stream = PluginAssembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException("Embedded resource not found: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Source => ReadResource("plugin.js");

    /// <summary>The source of <c>function name(...) { ... }</c>.</summary>
    public static string Extract(string source, string functionName)
    {
        var start = source.IndexOf("function " + functionName + "(", StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"function {functionName} not found in plugin.js (renamed or removed?)");
        var open = source.IndexOf('{', source.IndexOf(')', start));
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(start, i - start + 1);
        }

        throw new InvalidOperationException($"function {functionName} is not closed");
    }

    /// <summary>The source of <c>const name = ...;</c> (up to the semicolon that ends the statement).</summary>
    public static string ExtractConst(string source, string constName)
    {
        var start = source.IndexOf("const " + constName + " =", StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"const {constName} not found in plugin.js (renamed or removed?)");
        var end = source.IndexOf(';', start);
        return source.Substring(start, end - start + 1);
    }

    /// <summary>
    /// An engine holding the named functions and constants, with <c>navigator</c>, <c>window</c> and
    /// <c>codecCapabilities</c> (the globals they read) as given.
    /// </summary>
    public static Engine Functions(string[] functions, string[]? constants = null, object? navigator = null, object? window = null, object? codecCapabilities = null)
    {
        var source = Source;
        var engine = new Engine();
        engine.Execute("var navigator = " + JsonSerializer.Serialize(navigator ?? new { userAgent = string.Empty, platform = string.Empty, maxTouchPoints = 0 }) + ";");
        engine.Execute("var window = " + JsonSerializer.Serialize(window ?? new { }) + ";");
        engine.Execute("var codecCapabilities = " + JsonSerializer.Serialize(codecCapabilities) + ";");

        // window.NativeShell.downloadFile has to be a function, which JSON cannot carry.
        engine.Execute("if (window.NativeShell && window.NativeShell.downloadFile === 'fn') window.NativeShell.downloadFile = function () {};");

        foreach (var name in constants ?? Array.Empty<string>()) engine.Execute(ExtractConst(source, name));
        foreach (var name in functions) engine.Execute(Extract(source, name));
        return engine;
    }
}
