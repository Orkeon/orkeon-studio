using System.Text.Json;
using Orkeon.Constants.Llm;
using Orkeon.Domain.SharedKernel.ValueObjects;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Presets;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Config;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The editor's « Test connection » carries the <c>Llm:ModelCapabilities</c> overrides of the settings
/// document, as they stand when the button is pressed: the probe narrows the thinking switch by them,
/// as a run does.
/// </summary>
public sealed class ConnectionTestModelCapabilitiesTests
{
    private static ModelProfile DockerSetting() => new()
    {
        Name = "Docker",
        Provider = LlmPresets.DockerModelRunner,
        Model = LlmPresets.DockerModelRunnerDefaultModel,
        BaseUrl = LlmPresets.DockerModelRunnerBaseUrl,
    };

    [Fact]
    public async Task The_test_carries_the_overrides_of_the_document_as_it_stands_now()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Llm": { "Model": "m", "ModelCapabilities": { "glm-5.3": { "Thinking": "Toggle" }, "gpt-4o-mini": { "Vision": true } } } }""");
        var probe = new FakeLlmEndpointProbe();
        var profiles = new ModelProfilesViewModel(
            new InMemoryModelProfileStore(), new LlmSectionViewModel(() => document, () => { }), probe: probe, keyStore: new FakeApiKeyStore());
        profiles.BeginEdit(DockerSetting());
        var editor = profiles.Editor!;

        await editor.TestConnectionAsync(TestContext.Current.CancellationToken);
        // The document changes under the open editor: the next test reads it again.
        document.SetNode("Llm:ModelCapabilities:glm-5.3:Thinking", JsonSerializer.SerializeToNode("None"));
        await editor.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, probe.Requests.Count);
        Assert.Equal(ThinkingSupport.Toggle, probe.Requests[0].ModelThinkingOverrides["glm-5.3"]);
        Assert.Null(probe.Requests[0].ModelThinkingOverrides["gpt-4o-mini"]);
        Assert.Equal(ThinkingSupport.None, probe.Requests[1].ModelThinkingOverrides["glm-5.3"]);
    }

    [Fact]
    public async Task The_test_follows_the_document_another_file_opens()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var probe = new FakeLlmEndpointProbe();
        var profiles = new ModelProfilesViewModel(
            new InMemoryModelProfileStore(), new LlmSectionViewModel(() => document, () => { }), probe: probe, keyStore: new FakeApiKeyStore());
        profiles.BeginEdit(DockerSetting());

        await profiles.Editor!.TestConnectionAsync(TestContext.Current.CancellationToken);
        document = AppSettingsDocument.Parse("""{ "Llm": { "ModelCapabilities": { "qwen3": { "Thinking": "None" } } } }""");
        await profiles.Editor!.TestConnectionAsync(TestContext.Current.CancellationToken);

        Assert.Empty(probe.Requests[0].ModelThinkingOverrides);
        Assert.Equal(ThinkingSupport.None, probe.Requests[1].ModelThinkingOverrides["qwen3"]);
    }
}
