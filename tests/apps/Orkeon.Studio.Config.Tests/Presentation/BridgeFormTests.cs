using Orkeon.Studio.Config.Presentation;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Config.Tests.Presentation;

/// <summary>
/// The tool bridge form of the terminal editor: both sides as text, lists as names separated by commas,
/// no token stored, and a value the engine's start refuses refused at apply with nothing written.
/// </summary>
public sealed class BridgeFormTests
{
    [Fact]
    public void Load_shows_both_sides_and_says_what_a_run_bridges()
    {
        var document = AppSettingsDocument.Parse("""
            {"Orkeon":{"Tools":{"Bridge":{"Address":"https://desk.example:7410/","Allow":["browser_open","browser_click"]}},
                       "Bridge":{"Allow":["*"],"Deny":["shell_command"]}}}
            """);
        var form = new BridgeForm(name => name == "ORKEON_BRIDGE_TOKEN" ? "0123456789abcdef0123456789abcdef" : null);

        form.LoadFrom(document);

        Assert.Equal("https://desk.example:7410/", form.Address);
        Assert.Equal("browser_open, browser_click", form.Allow);
        Assert.Equal("*", form.ServeAllow);
        Assert.Equal("shell_command", form.ServeDeny);
        Assert.Contains("the token is read from ORKEON_BRIDGE_TOKEN", form.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void The_status_says_when_a_run_would_refuse_to_start_for_its_token()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Tools":{"Bridge":{"Address":"https://desk.example:7410/","Allow":["*"]}}}}""");
        var form = new BridgeForm(_ => null);

        form.LoadFrom(document);

        Assert.StartsWith("A run would refuse to start", form.Status, StringComparison.Ordinal);
        Assert.Contains("ORKEON_BRIDGE_TOKEN", form.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_writes_both_sides_and_removes_what_is_blanked()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Tools":{"Bridge":{"Address":"https://desk.example:7410/","Allow":["*"],"MaxFileBytes":1048576}}}}""");
        var form = new BridgeForm(_ => null);
        form.LoadFrom(document);

        form.Address = "";
        form.Allow = " ";
        form.ServeAllow = "browser_open , browser_click";
        form.ServeTokenEnvVar = "DESK_TOKEN";
        Assert.Empty(form.ApplyTo(document));

        Assert.False(document.ContainsPath("Orkeon:Tools:Bridge:Address"));
        Assert.False(document.ContainsPath("Orkeon:Tools:Bridge:Allow"));
        // What the screen does not show stays.
        Assert.Equal(1048576, document.GetInt32("Orkeon:Tools:Bridge:MaxFileBytes"));
        Assert.Equal(["browser_open", "browser_click"], document.BridgeServer.Allow);
        Assert.Equal("DESK_TOKEN", document.BridgeServer.TokenEnvVar);
        Assert.StartsWith("No bridge", form.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Address", "http://desk.example:7410/", "requires https")]
    [InlineData("Allow", "browser_*", "not a pattern")]
    [InlineData("HostHeader", "two words", "not a host")]
    [InlineData("ServePrefix", "http://0.0.0.0:7410", "must end with '/'")]
    public void A_value_the_engine_refuses_is_refused_at_apply_and_nothing_is_written(string field, string value, string reason)
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Bridge":{"Allow":["*"]}}}""");
        var before = document.ToJson();
        var form = new BridgeForm(_ => null);
        form.LoadFrom(document);
        typeof(BridgeForm).GetProperty(field)!.SetValue(form, value);

        var errors = form.ApplyTo(document);

        Assert.Contains(errors, error => error.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(before, document.ToJson());
    }

    /// <summary>
    /// The screen shows nine of the bridge's keys: a refused value of another one (a limit, a size) is
    /// the JSON file's to fix and the settings check's to report, and must not lock the fields shown.
    /// </summary>
    [Fact]
    public void A_refused_key_the_screen_does_not_show_blocks_no_edit()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Bridge":{"MaxSessions":0}}}""");
        var form = new BridgeForm(_ => null);
        form.LoadFrom(document);

        form.ServeAllow = "browser_open";
        Assert.Empty(form.ApplyTo(document));

        Assert.Equal(["browser_open"], document.BridgeServer.Allow);
        Assert.Equal(0, document.GetInt32("Orkeon:Bridge:MaxSessions"));
    }

    /// <summary>
    /// A field left as loaded is not written: a list the file writes in a shape the field cannot show
    /// (here an object, which the engine reads as a list) survives an apply that edits something else.
    /// </summary>
    [Fact]
    public void A_field_left_as_loaded_is_not_rewritten()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Tools":{"Bridge":{"Address":"https://desk.example:7410/","Allow":{"0":"*"}}}}}""");
        var form = new BridgeForm(_ => "0123456789abcdef0123456789abcdef");
        form.LoadFrom(document);
        Assert.Equal("", form.Allow);

        form.HostHeader = "desk.example:7410";
        Assert.Empty(form.ApplyTo(document));

        Assert.True(document.ContainsPath("Orkeon:Tools:Bridge:Allow:0"));
        Assert.True(document.ToolBridge.IsEnabled);
        Assert.Equal("desk.example:7410", document.ToolBridge.HostHeader);
    }
}
