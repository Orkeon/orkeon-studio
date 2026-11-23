using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Config;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The two cards of the tool bridge: lists typed as names separated by commas, the token stored under
/// the variable the section names, and what the engine's start refuses said under the fields.
/// </summary>
public sealed class ToolBridgeSectionViewModelTests
{
    private static (ToolBridgeSectionViewModel Caller, BridgeServerSectionViewModel Server, AppSettingsDocument Document, FakeApiKeyStore Keys) Forms(string json = "{}")
    {
        var document = AppSettingsDocument.Parse(json);
        var keys = new FakeApiKeyStore();
        return (
            new ToolBridgeSectionViewModel(() => document, () => { }, keyStore: keys),
            new BridgeServerSectionViewModel(() => document, () => { }),
            document,
            keys);
    }

    [Fact]
    public void An_empty_file_bridges_nothing_and_shows_the_engines_defaults()
    {
        var (caller, server, _, _) = Forms();

        Assert.False(caller.Exists);
        Assert.Equal("", caller.Address);
        Assert.Equal("ORKEON_BRIDGE_TOKEN", ToolBridgeSectionViewModel.TokenEnvVarDefault);
        Assert.Equal("ORKEON_BRIDGE_TOKEN", caller.Token.EnvName);
        Assert.Equal(BridgeServerSection.DefaultPrefix, BridgeServerSectionViewModel.PrefixDefault);
        Assert.Equal("", caller.ProblemsText);
        Assert.Equal("", server.ProblemsText);
    }

    [Fact]
    public void Lists_are_typed_with_commas_and_remove_their_key_when_emptied()
    {
        var (caller, server, document, _) = Forms();

        caller.Address = " https://desk.example:7410/ ";
        caller.AllowText = "browser_open ,browser_click";
        server.AllowText = "*";
        server.DenyText = "shell_command";

        Assert.Equal("https://desk.example:7410/", document.ToolBridge.Address);
        Assert.Equal(["browser_open", "browser_click"], document.ToolBridge.Allow);
        Assert.Equal("browser_open, browser_click", caller.AllowText);
        Assert.Equal(["*"], document.BridgeServer.Allow);
        Assert.Equal(["shell_command"], document.BridgeServer.Deny);

        caller.AllowText = "";
        Assert.False(document.ContainsPath("Orkeon:Tools:Bridge:Allow"));
    }

    [Fact]
    public void The_token_row_follows_the_variable_the_section_names()
    {
        var (caller, _, _, keys) = Forms();
        keys.Stage("DESK_TOKEN", "0123456789abcdef0123456789abcdef");
        Assert.False(caller.Token.HasKey);

        caller.TokenEnvVar = "DESK_TOKEN";

        Assert.Equal("DESK_TOKEN", caller.Token.EnvName);
        Assert.True(caller.Token.HasKey);
    }

    [Fact]
    public void What_the_engines_start_refuses_is_said_under_the_fields_of_each_side()
    {
        var (caller, server, _, _) = Forms();

        caller.Address = "http://desk.example:7410/";
        server.AllowText = "browser_*";

        Assert.Contains("requires https", caller.ProblemsText, StringComparison.Ordinal);
        Assert.Contains("not a pattern", server.ProblemsText, StringComparison.Ordinal);

        caller.Address = "http://host.docker.internal:7410/";
        server.AllowText = "*";
        Assert.Equal("", caller.ProblemsText);
        Assert.Equal("", server.ProblemsText);
    }
}
