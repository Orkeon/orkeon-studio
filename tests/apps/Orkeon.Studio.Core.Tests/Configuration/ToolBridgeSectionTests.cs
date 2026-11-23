using System.Text.Json;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Launch;
using Orkeon.Studio.Core.Validation;
using Orkeon.Studio.Tests.Shared;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// The tool bridge's two sections as Studio reads and writes them, their defaults against the engine's
/// settings catalogue, and what a launch is told of the bridge.
/// </summary>
public sealed class ToolBridgeSectionTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Every_key_is_written_in_place_and_removed_when_emptied()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Bridge": { "MaxFileBytes": 1048576 }, "Shell": { "Enabled": true } } } }""");
        var caller = document.ToolBridge;
        caller.Address = " https://desk.example:7410/ ";
        caller.Allow = ["*"];
        caller.Deny = ["shell_command"];
        caller.TokenEnvVar = "MY_BRIDGE_TOKEN";
        caller.HostHeader = "desk.example:7410";
        var server = document.BridgeServer;
        server.Prefix = "https://0.0.0.0:7410/";
        server.Allow = ["browser_open", "browser_click"];

        var reparsed = AppSettingsDocument.Parse(document.ToJson());
        Assert.Equal("https://desk.example:7410/", reparsed.ToolBridge.Address);
        Assert.Equal(["*"], reparsed.ToolBridge.Allow);
        Assert.Equal(["shell_command"], reparsed.ToolBridge.Deny);
        Assert.Equal("MY_BRIDGE_TOKEN", reparsed.ToolBridge.EffectiveTokenEnvVar);
        Assert.Equal(1048576, reparsed.GetInt32("Orkeon:Tools:Bridge:MaxFileBytes"));
        Assert.True(reparsed.ToolBridge.IsEnabled);
        Assert.Equal("https://0.0.0.0:7410/", reparsed.BridgeServer.Prefix);
        Assert.Equal(2, reparsed.BridgeServer.Allow.Count);
        // The neighbours Studio models elsewhere stay.
        Assert.True(reparsed.GetBoolean("Orkeon:Tools:Shell:Enabled"));
        Assert.Empty(reparsed.ToolBridge.Problems());
        Assert.Empty(reparsed.BridgeServer.Problems());

        caller.Address = "";
        caller.Allow = [];
        caller.TokenEnvVar = null;
        Assert.False(document.ContainsPath("Orkeon:Tools:Bridge:Address"));
        Assert.False(document.ContainsPath("Orkeon:Tools:Bridge:Allow"));
        Assert.Equal(ToolBridgeSection.DefaultTokenEnvVar, caller.EffectiveTokenEnvVar);
        Assert.False(caller.IsEnabled);
    }

    /// <summary>
    /// The engine's options classes are internal: the defaults Studio shows are copied, and pinned here
    /// against the engine's own settings catalogue, key by key. A key the catalogue adds to either
    /// section fails here too, so the rules restated for it are looked at.
    /// </summary>
    [Fact]
    public void The_defaults_are_those_of_the_engines_settings_catalogue()
    {
        var catalogue = Path.Combine(
            OrkeonSources.Root(), "src", "hosting", "Orkeon.Hosting", "Settings", "Catalog", "library.json");
        using var library = JsonDocument.Parse(File.ReadAllText(catalogue));
        var settings = library.RootElement.GetProperty("settings").EnumerateArray()
            .Where(setting => setting.TryGetProperty("section", out var section)
                && section.GetString() is ToolBridgeSection.SectionPath or BridgeServerSection.SectionPath)
            .ToDictionary(setting => setting.GetProperty("path").GetString()!, setting => setting, StringComparer.Ordinal);

        string Default(string path) => settings[path].GetProperty("default").ToString();

        Assert.Equal(
            [
                "Orkeon:Bridge:Allow", "Orkeon:Bridge:Deny", "Orkeon:Bridge:HeartbeatWindow", "Orkeon:Bridge:MaxCallsPerSession",
                "Orkeon:Bridge:MaxConcurrentCalls", "Orkeon:Bridge:MaxFileBytes", "Orkeon:Bridge:MaxRequestBytes",
                "Orkeon:Bridge:MaxRunsPerSession", "Orkeon:Bridge:MaxSessions", "Orkeon:Bridge:Prefix",
                "Orkeon:Bridge:RequestReadTimeout", "Orkeon:Bridge:StopTimeout", "Orkeon:Bridge:TokenEnvVar",
                "Orkeon:Tools:Bridge:Address", "Orkeon:Tools:Bridge:Allow", "Orkeon:Tools:Bridge:ConnectTimeout",
                "Orkeon:Tools:Bridge:Deny", "Orkeon:Tools:Bridge:HostHeader", "Orkeon:Tools:Bridge:MaxFileBytes",
                "Orkeon:Tools:Bridge:MaxResponseBytes", "Orkeon:Tools:Bridge:TokenEnvVar",
            ],
            settings.Keys.Order(StringComparer.Ordinal));

        Assert.Equal(ToolBridgeSection.DefaultTokenEnvVar, Default("Orkeon:Tools:Bridge:TokenEnvVar"));
        Assert.Equal(ToolBridgeSection.DefaultConnectTimeout, Default("Orkeon:Tools:Bridge:ConnectTimeout"));
        Assert.Equal(ToolBridgeSection.DefaultMaxFileBytes, settings["Orkeon:Tools:Bridge:MaxFileBytes"].GetProperty("default").GetInt64());
        Assert.Equal(ToolBridgeSection.DefaultMaxResponseBytes, settings["Orkeon:Tools:Bridge:MaxResponseBytes"].GetProperty("default").GetInt64());
        Assert.Equal(ToolBridgeSection.DefaultTokenEnvVar, Default("Orkeon:Bridge:TokenEnvVar"));
        Assert.Equal(ToolBridgeSection.DefaultMaxFileBytes, settings["Orkeon:Bridge:MaxFileBytes"].GetProperty("default").GetInt64());
        Assert.Equal(BridgeServerSection.DefaultMaxRequestBytes, settings["Orkeon:Bridge:MaxRequestBytes"].GetProperty("default").GetInt64());
        Assert.Equal(BridgeServerSection.DefaultHeartbeatWindow, Default("Orkeon:Bridge:HeartbeatWindow"));
        Assert.Equal(BridgeServerSection.DefaultRequestReadTimeout, Default("Orkeon:Bridge:RequestReadTimeout"));
        Assert.Equal(BridgeServerSection.DefaultStopTimeout, Default("Orkeon:Bridge:StopTimeout"));
        Assert.Equal(BridgeServerSection.DefaultMaxSessions, settings["Orkeon:Bridge:MaxSessions"].GetProperty("default").GetInt32());
        Assert.Equal(BridgeServerSection.DefaultMaxConcurrentCalls, settings["Orkeon:Bridge:MaxConcurrentCalls"].GetProperty("default").GetInt32());
        Assert.Equal(BridgeServerSection.DefaultMaxCallsPerSession, settings["Orkeon:Bridge:MaxCallsPerSession"].GetProperty("default").GetInt32());
        Assert.Equal(BridgeServerSection.DefaultMaxRunsPerSession, settings["Orkeon:Bridge:MaxRunsPerSession"].GetProperty("default").GetInt32());
        // The prefix depends on the platform: the catalogue states both in a note.
        var note = settings["Orkeon:Bridge:Prefix"].GetProperty("defaultNote").GetString();
        Assert.Contains(BridgeServerSection.DefaultPrefixElsewhere, note, StringComparison.Ordinal);
        Assert.Contains(BridgeServerSection.DefaultPrefixWindows, note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_value_of_either_side_is_an_error_of_the_settings_check_at_its_key()
    {
        var messages = new AppSettingsValidator().Validate(AppSettingsDocument.Parse("""
            { "Orkeon": { "Tools": { "Bridge": { "Address": "http://desk.example:7410/" } }, "Bridge": { "MaxSessions": 0 } } }
            """));

        var errors = messages.Where(m => m.Code == ValidationCodes.EngineSettingRefused).ToList();
        Assert.Equal(["Orkeon:Tools:Bridge:Address", "Orkeon:Bridge:MaxSessions"], errors.Select(m => m.Path));
        Assert.Contains("requires https", errors[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_launch_is_told_nothing_without_a_bridge_and_that_nothing_is_bridged_without_a_tool()
    {
        Assert.Null(ToolBridgePreflight.Check(AppSettingsDocument.CreateEmpty(), _ => null));

        var idle = Assert.IsType<ToolBridgePreflightFinding>(ToolBridgePreflight.Check(
            AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Bridge": { "Address": "https://desk.example:7410/" } } } }"""),
            _ => null));
        Assert.Equal(ToolBridgePreflightKind.NothingBridged, idle.Kind);
        Assert.Equal("https://desk.example:7410/", idle.Reason);
        Assert.False(idle.BlocksStart);
    }

    [Fact]
    public void A_launch_that_bridges_tools_needs_the_token_in_the_variable_the_section_names()
    {
        var document = AppSettingsDocument.Parse("""
            { "Orkeon": { "Tools": { "Bridge": { "Address": "https://desk.example:7410/", "Allow": ["*"], "TokenEnvVar": "DESK_TOKEN" } } } }
            """);

        var missing = Assert.IsType<ToolBridgePreflightFinding>(ToolBridgePreflight.Check(document, _ => null));
        Assert.Equal(ToolBridgePreflightKind.TokenRefused, missing.Kind);
        Assert.Contains("DESK_TOKEN, which is not set", missing.Reason, StringComparison.Ordinal);
        Assert.True(missing.BlocksStart);

        var tooShort = ToolBridgePreflight.Check(document, name => name == "DESK_TOKEN" ? "short" : null);
        Assert.Contains("shorter than 16 characters", tooShort?.Reason, StringComparison.Ordinal);

        Assert.Null(ToolBridgePreflight.Check(document, name => name == "DESK_TOKEN" ? Token : null));
        // The default variable is not the one read once the section names another.
        Assert.NotNull(ToolBridgePreflight.Check(document, name => name == ToolBridgeSection.DefaultTokenEnvVar ? Token : null));
    }

    [Fact]
    public void A_value_the_start_refuses_stops_the_launch_whichever_side_holds_it()
    {
        var caller = Assert.IsType<ToolBridgePreflightFinding>(ToolBridgePreflight.Check(
            AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Bridge": { "Address": "desk.example", "Allow": ["*"] } } } }"""),
            _ => Token));
        Assert.Equal(ToolBridgePreflightKind.SettingsRefused, caller.Kind);
        Assert.Equal("Orkeon:Tools:Bridge:Address", caller.Path);
        Assert.True(caller.BlocksStart);

        // Every host validates the serving section too, the run bridging anything or not.
        var server = Assert.IsType<ToolBridgePreflightFinding>(ToolBridgePreflight.Check(
            AppSettingsDocument.Parse("""{ "Orkeon": { "Bridge": { "Allow": ["browser_*"] } } }"""),
            _ => null));
        Assert.Equal(ToolBridgePreflightKind.SettingsRefused, server.Kind);
        Assert.Equal("Orkeon:Bridge:Allow", server.Path);
    }

    /// <summary>
    /// The configuration reads a key in any letter case: Studio reads the section as the engine does,
    /// and writes each key where the file spells it, never a twin beside it.
    /// </summary>
    [Fact]
    public void Keys_in_another_letter_case_are_read_judged_and_written_in_place()
    {
        var document = AppSettingsDocument.Parse("""{ "orkeon": { "tools": { "bridge": { "address": "http://desk.example:7410/", "allow": ["*"] } } } }""");
        var caller = document.ToolBridge;

        Assert.Equal("http://desk.example:7410/", caller.Address);
        Assert.Equal(["*"], caller.Allow);
        Assert.True(caller.IsEnabled);
        Assert.Contains("requires https", Assert.Single(caller.Problems()).Problem, StringComparison.Ordinal);

        caller.Address = "https://desk.example:7410/";

        Assert.Equal("https://desk.example:7410/", document.GetString("orkeon:tools:bridge:address"));
        Assert.DoesNotContain("\"Address\"", document.ToJson(), StringComparison.Ordinal);
        Assert.Empty(caller.Problems());
    }

    /// <summary>A key that is no setting of its section stops every start: a misspelt one first of all.</summary>
    [Theory]
    [InlineData("""{ "Orkeon": { "Bridge": { "Port": 7410 } } }""", "Orkeon:Bridge:Port")]
    [InlineData("""{ "Orkeon": { "Tools": { "Bridge": { "Adress": "https://desk.example:7410/" } } } }""", "Orkeon:Tools:Bridge:Adress")]
    public void A_key_that_is_no_setting_of_the_bridge_is_refused(string json, string path)
    {
        var document = AppSettingsDocument.Parse(json);

        var problem = Assert.Single(document.ToolBridge.Problems().Concat(document.BridgeServer.Problems()));

        Assert.Equal(path, problem.Path);
        Assert.StartsWith(path + " is not a setting: ", problem.Problem, StringComparison.Ordinal);
        Assert.Equal(ToolBridgePreflightKind.SettingsRefused, ToolBridgePreflight.Check(document, _ => null)?.Kind);
    }

    /// <summary>
    /// A list of names is shown as the binder reads it, whatever shape the file gives it: a list
    /// written as an object by its values in the order of their keys, an entry that is no text as the
    /// empty name the engine then refuses. Applying what is shown writes nothing, so the object stays
    /// an object; a change writes a list, and an empty list removes the key.
    /// </summary>
    [Fact]
    public void A_list_of_names_is_read_as_the_binder_reads_it_and_written_only_when_changed()
    {
        var document = AppSettingsDocument.Parse(
            """
            { "Orkeon": {
                "Tools": { "Bridge": { "Address": "https://desk.example:7410/", "Allow": { "1": "b*", "0": "a*" }, "Deny": [null, "c*", 3] } },
                "Bridge": { "allow": { "first": "*" }, "Deny": [] } } }
            """);
        var before = document.ToJson();
        var (caller, server) = (document.ToolBridge, document.BridgeServer);

        Assert.Equal(["a*", "b*"], caller.Allow);
        Assert.Equal(["", "c*", "3"], caller.Deny);
        Assert.Equal(["*"], server.Allow);
        Assert.Empty(server.Deny);
        Assert.True(caller.IsEnabled);

        caller.Allow = ["a*", "b*"];
        caller.Deny = ["", "c*", "3"];
        server.Allow = ["*"];
        Assert.Equal(before, document.ToJson());

        caller.Allow = ["a*"];
        server.Allow = [];
        server.Deny = [];
        Assert.Equal("""["a*"]""", document.GetNode("Orkeon:Tools:Bridge:Allow")!.ToJsonString());
        Assert.False(document.ContainsPath("Orkeon:Bridge:allow"));
        Assert.False(document.ContainsPath("Orkeon:Bridge:Deny"));
    }

    /// <summary>
    /// A value that does not convert elsewhere in the section hides no list: the names are still read,
    /// and the address written beside it is still judged.
    /// </summary>
    [Fact]
    public void A_value_that_does_not_convert_hides_neither_the_lists_nor_the_rules()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Orkeon": { "Tools": { "Bridge": { "ConnectTimeout": "soon", "Allow": ["browser_open"] } } } }""");
        var caller = document.ToolBridge;

        Assert.Equal(["browser_open"], caller.Allow);
        Assert.Equal("Orkeon:Tools:Bridge:ConnectTimeout", Assert.Single(caller.Problems()).Path);

        caller.Address = "not a url";

        Assert.Equal(["Orkeon:Tools:Bridge:ConnectTimeout", "Orkeon:Tools:Bridge:Address"], caller.Problems().Select(problem => problem.Path));
        Assert.False(caller.IsEnabled);
        Assert.Null(caller.BoundAddress);
    }
}
