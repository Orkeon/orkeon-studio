using System.Runtime.CompilerServices;
using System.Xml.Linq;
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

    [Fact]
    public void The_field_is_not_under_an_element_gated_by_the_expert_mode()
    {
        // #25: the field once sat in the URL panel, collapsed outside Expert unless the card was the
        // catch-all, so a novice on an Anthropic card never saw it. MainWindow.xaml is read as XML.
        var root = XDocument.Load(MainWindowXaml()).Root!;
        var field = Assert.Single(root.Descendants(), e => e.Name.LocalName == "TextBox"
            && ((string?)e.Attribute("Text") ?? "").StartsWith("{Binding WorkspaceId", StringComparison.Ordinal));

        var gated = field.Ancestors().Where(GatedByExpert).Select(e => e.Name.LocalName).ToList();

        Assert.True(gated.Count == 0, "The workspace field sits under an element gated by IsExpert: " + string.Join(", ", gated));
        Assert.Contains(field.Ancestors(), e => ((string?)e.Attribute("Visibility") ?? "").Contains("ShowWorkspace", StringComparison.Ordinal));
    }

    private static bool GatedByExpert(XElement element)
    {
        if (((string?)element.Attribute("Visibility") ?? "").Contains("IsExpert", StringComparison.Ordinal))
            return true;
        var ownStyle = element.Elements().Where(child => child.Name.LocalName == element.Name.LocalName + ".Style");
        return ownStyle.SelectMany(style => style.Descendants())
            .Where(trigger => trigger.Name.LocalName == "DataTrigger"
                           && ((string?)trigger.Attribute("Binding") ?? "").Contains("IsExpert", StringComparison.Ordinal))
            .Any(trigger => trigger.Descendants().Any(setter => setter.Name.LocalName == "Setter"
                                                              && (string?)setter.Attribute("Property") == "Visibility"));
    }

    private static string MainWindowXaml([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!,
            "..", "..", "..", "src", "apps", "Orkeon.Studio.Wpf", "Views", "MainWindow.xaml"));

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
