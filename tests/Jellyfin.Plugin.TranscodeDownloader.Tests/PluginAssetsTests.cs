using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.TranscodeDownloader.Tests;

public class PluginAssetsTests
{
    [Fact]
    public void The_settings_page_has_a_field_for_every_setting_and_no_others()
    {
        var html = ClientScript.ReadResource("config.html");
        var fields = Regex.Matches(html, @"<input[^>]*\bid=""(?<id>[A-Za-z]+)""").Select(m => m.Groups["id"].Value).ToHashSet();
        var settings = typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.NotEmpty(settings);
        Assert.Equal(settings.OrderBy(x => x), fields.OrderBy(x => x));
    }

    [Fact]
    public void The_settings_page_script_loads_and_saves_every_setting()
    {
        var html = ClientScript.ReadResource("config.html");

        foreach (var name in typeof(PluginConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => p.Name))
        {
            // Once as the field's id, once in the script's list of fields it loads into and saves from.
            Assert.True(Regex.Matches(html, @"\b" + name + @"\b").Count >= 2, $"config.html's script does not handle {name}");
        }
    }

    [Theory]
    [InlineData("plugin.js")]
    [InlineData("config.html")]
    [InlineData("strings.en-us.json")]
    [InlineData("strings.de.json")]
    [InlineData("strings.fr.json")]
    [InlineData("strings.es.json")]
    [InlineData("strings.zh-cn.json")]
    [InlineData("strings.nl.json")]
    public void The_web_assets_are_embedded_under_the_names_the_controller_and_plugin_look_for(string name)
    {
        Assert.False(string.IsNullOrWhiteSpace(ClientScript.ReadResource(name)));
    }

    [Fact]
    public void The_plugin_identity_is_stable()
    {
        // Jellyfin keys the installed plugin and its saved settings by this id; changing it orphans both.
        Assert.Equal(new Guid("a4b5c6d7-e8f9-0a1b-2c3d-4e5f6a7b8c9d"), Plugin.PluginId);
    }
}
