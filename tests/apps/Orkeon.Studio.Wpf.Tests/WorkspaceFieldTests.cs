using Orkeon.Constants.Llm;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Presets;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Config;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The Anthropic workspace of a model setting (<c>WorkspaceId</c>, Orkeon #468): the editor shows the
/// field where the address reads as Anthropic or a workspace is set, sends it with the connection test
/// and saves it trimmed into the setting and its host entry.
/// </summary>
public sealed class WorkspaceFieldTests
{
    private const string AnthropicKey = "sk-ant-workspace-test";

    private static ModelProfile Claude(string? workspace = null) => new()
    {
        Name = "Claude",
        Provider = LlmPresets.Anthropic,
        Model = LlmProviderDefaultModels.Anthropic,
        BaseUrl = LlmProviderEndpoints.Anthropic,
        KeyEnvName = "ANTHROPIC_API_KEY",
        WorkspaceId = workspace,
    };

    [Fact]
    public void The_field_shows_for_an_anthropic_address_or_a_workspace_set_and_nowhere_else()
    {
        var (profiles, _, _) = Screen();
        profiles.BeginEdit(Claude());
        var editor = profiles.Editor!;

        Assert.True(editor.ShowWorkspace);

        var raised = new List<string?>();
        editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        editor.BaseUrl = "https://api.openai.com/v1";
        Assert.False(editor.ShowWorkspace);
        Assert.Contains(nameof(ModelProfileEditorViewModel.ShowWorkspace), raised);

        editor.WorkspaceId = "wrkspc_01";
        Assert.True(editor.ShowWorkspace);
    }

    [Fact]
    public async Task The_connection_test_sends_it_and_a_save_writes_it_trimmed()
    {
        var (profiles, probe, document) = Screen();
        profiles.BeginEdit(Claude(" wrkspc_01 "));
        var editor = profiles.Editor!;

        await editor.TestConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal("wrkspc_01", probe.LastRequest.WorkspaceId);

        editor.SaveCommand.Execute(null);

        Assert.Equal("wrkspc_01", profiles.Set.Find("Claude")!.WorkspaceId);
        Assert.Equal("wrkspc_01", document.Llm.Profiles.Get("claude")?.WorkspaceId);
    }

    private static (ModelProfilesViewModel Profiles, FakeLlmEndpointProbe Probe, AppSettingsDocument Document) Screen()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var keys = new FakeApiKeyStore();
        keys.Stage("ANTHROPIC_API_KEY", AnthropicKey);
        var probe = new FakeLlmEndpointProbe();
        var llm = new LlmSectionViewModel(() => document, () => { });
        return (new ModelProfilesViewModel(new InMemoryModelProfileStore(), llm, probe: probe, keyStore: keys), probe, document);
    }
}
