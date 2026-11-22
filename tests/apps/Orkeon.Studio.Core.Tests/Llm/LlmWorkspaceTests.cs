using System.Reflection;
using System.Text.Json;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Core.Validation;
using Orkeon.Studio.Tests.Shared;

namespace Orkeon.Studio.Core.Tests.Llm;

/// <summary>
/// <c>WorkspaceId</c> (Orkeon #468): the Anthropic workspace a key acts in, a string of the
/// <c>Llm</c> shape, on the default and on every profile. Studio reads and writes it, lays it over a
/// launch, sends it with a probe, and warns where the provider does not send it.
/// </summary>
public sealed class LlmWorkspaceTests
{
    [Fact]
    public void The_engine_knows_the_key_on_the_default_and_on_a_profile()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            OrkeonSources.Root(), "src", "hosting", "Orkeon.Hosting", "Settings", "Catalog", "library.json")));
        var paths = Entries(catalog.RootElement)
            .Select(entry => entry.TryGetProperty("path", out var path) ? path.GetString() : null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains($"{LlmSection.SectionPath}:{LlmProfilesSection.WorkspaceIdKey}", paths);
        Assert.Contains($"{LlmProfilesSection.SectionPath}:<name>:{LlmProfilesSection.WorkspaceIdKey}", paths);
    }

    [Fact]
    public void The_header_is_the_one_the_run_sends()
    {
        var engine = typeof(Orkeon.Infrastructure.Constants.Llm.HttpDefaults)
            .GetField("AnthropicWorkspaceIdHeader", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal(LlmProbeDialect.AnthropicWorkspaceIdHeader, (string)engine.GetRawConstantValue()!);
    }

    [Fact]
    public void The_default_and_a_profile_carry_it_in_whatever_letter_case_the_file_spells()
    {
        var document = AppSettingsDocument.Parse("""
            { "Llm": { "workspaceid": "wrkspc_default", "Profiles": { "p": { "Model": "m", "WORKSPACEID": "wrkspc_p" } } } }
            """);

        Assert.Equal("wrkspc_default", document.Llm.WorkspaceId);
        Assert.Equal("wrkspc_p", document.Llm.Profiles.Get("p")!.WorkspaceId);

        document.Llm.WorkspaceId = "wrkspc_other";
        document.Llm.Profiles.Set(document.Llm.Profiles.Get("p")! with { WorkspaceId = null });

        Assert.Equal("wrkspc_other", document.GetString("Llm:workspaceid"));
        Assert.False(document.ContainsPath("Llm:Profiles:p:WORKSPACEID"));
        Assert.Equal("m", document.GetString("Llm:Profiles:p:Model"));
    }

    [Fact]
    public void A_model_setting_takes_it_to_its_entry_and_lays_it_over_a_launch()
    {
        var profile = new ModelProfile { Name = "Claude", BaseUrl = "https://api.anthropic.com", Model = "claude-x", WorkspaceId = " wrkspc_01 " };

        Assert.Equal("wrkspc_01", profile.ToHostEntry("claude").WorkspaceId);
        Assert.Equal("wrkspc_01", profile.EnvironmentOverrides()["ORKEON_Llm__WorkspaceId"]);
        Assert.Equal("wrkspc_01", profile.HostEnvironment("claude", static _ => null)["ORKEON_Llm__Profiles__claude__WorkspaceId"]);
        Assert.Null((profile with { WorkspaceId = " " }).ToHostEntry("claude").WorkspaceId);
        Assert.Equal("", (profile with { WorkspaceId = null }).EnvironmentOverrides()["ORKEON_Llm__WorkspaceId"]);
    }

    /// <summary>
    /// A section, and the provider the run builds for it when it does not send the workspace (null when
    /// it sends it, or builds no provider to warn).
    /// </summary>
    public static TheoryData<string, string?> Sections => new()
    {
        // An Anthropic address sends it, and so does a section without one whose model reads as Claude.
        { """ "BaseUrl": "https://api.anthropic.com", "Model": "claude-x" """, null },
        { """ "Model": "claude-x" """, null },
        { """ "model": "Claude-Sonnet" """, null },
        // Every other provider does not send it, and the run warns: by the address...
        { """ "BaseUrl": "https://api.openai.com/v1", "Model": "gpt-x" """, "openai" },
        { """ "BaseUrl": "http://localhost:11434/v1", "Model": "qwen" """, "ollama" },
        // ... the address read before the model ...
        { """ "BaseUrl": "https://api.deepseek.com", "Model": "claude-x" """, "deepseek" },
        // ... else the model's name, then the key's prefix, else OpenAI.
        { """ "Model": "gpt-4o" """, "openai" },
        { """ "Model": "mistral-large" """, "mistral" },
        { """ "Model": "m", "ApiKey": "xai-0123456789" """, "grok" },
        { """ "Model": "m" """, "openai" },
        // The mock a section names, in any case and padded.
        { """ "Provider": "mock", "Responses": "/r.jsonl", "Model": "m" """, "mock" },
        { """ "Provider": " Mock ", "Model": "claude-x" """, "mock" },
        // A section that names another provider builds none of the catalogue: not judged.
        { """ "Provider": "acme", "Model": "gpt-4o" """, null },
        // An address the start refuses builds no provider: its error is said, not the workspace.
        { """ "BaseUrl": "https://api.deepseek.com/anthropic", "Model": "m" """, null },
    };

    [Theory]
    [MemberData(nameof(Sections))]
    public void A_workspace_the_provider_does_not_send_is_a_warning_on_the_default_and_on_a_profile(string keys, string? provider)
    {
        foreach (var (json, path) in new[]
                 {
                     ($$"""{ "Llm": { {{keys}}, "WorkspaceId": "wrkspc_01" } }""", "Llm:WorkspaceId"),
                     ($$"""{ "llm": { {{keys}}, "workspaceid": "wrkspc_01" } }""", "Llm:WorkspaceId"),
                     ($$"""{ "Llm": { "Model": "claude-x", "Profiles": { "p": { {{keys}}, "WorkspaceId": "wrkspc_01" } } } }""", "Llm:Profiles:p:WorkspaceId"),
                 })
        {
            var found = new AppSettingsValidator().Validate(AppSettingsDocument.Parse(json))
                .Where(message => message.Code == ValidationCodes.LlmWorkspace)
                .ToList();

            if (provider is null)
            {
                Assert.Empty(found);
                continue;
            }

            var warning = Assert.Single(found);
            Assert.Equal(ValidationSeverity.Warning, warning.Severity);
            Assert.Equal(path, warning.Path);
            Assert.Equal(AppSettingsValidator.WorkspaceNotSent(path, provider), warning.Text);
            Assert.EndsWith("Only Anthropic scopes a key to a workspace; remove Llm:WorkspaceId (or the profile's) for this provider.", warning.Text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The provider a section without an address is built on is the Domain's own reading, model then key
    /// (<see cref="Orkeon.Domain.SharedKernel.ValueObjects.LlmEndpointReading.Of"/>), the one the run's
    /// factory takes: Studio warns exactly where it reads another provider than Anthropic.
    /// </summary>
    [Theory]
    [InlineData("claude-3-5-sonnet", null)]
    [InlineData("gpt-4o", null)]
    [InlineData("llama3.2", null)]
    [InlineData("deepseek-chat", null)]
    [InlineData("m", "hf_0123456789")]
    [InlineData(null, null)]
    public void A_section_without_an_address_is_read_by_the_model_then_the_key(string? model, string? apiKey)
    {
        var expected = Orkeon.Domain.SharedKernel.ValueObjects.LlmEndpointReading.Of(null, model, apiKey).ProviderKey;
        var section = new System.Text.Json.Nodes.JsonObject { ["WorkspaceId"] = "wrkspc_01" };
        if (model is not null)
            section["Model"] = model;
        if (apiKey is not null)
            section["ApiKey"] = apiKey;
        var document = AppSettingsDocument.Parse(new System.Text.Json.Nodes.JsonObject { ["Llm"] = section }.ToJsonString());

        var found = new AppSettingsValidator().Validate(document).Where(message => message.Code == ValidationCodes.LlmWorkspace).ToList();

        if (expected == Orkeon.Constants.Llm.LlmProviderKeys.Anthropic)
            Assert.Empty(found);
        else
            Assert.Equal(AppSettingsValidator.WorkspaceNotSent("Llm:WorkspaceId", expected), Assert.Single(found).Text);
    }

    [Fact]
    public void The_remedy_is_the_one_the_run_logs()
    {
        var source = File.ReadAllText(Path.Combine(
            OrkeonSources.Root(), "src", "core", "Orkeon.Infrastructure", "LLMs", "Base", "HttpLlmProviderBase.cs"));

        Assert.Contains("\"Only Anthropic scopes a key to a workspace; remove Llm:WorkspaceId (or the profile's) for this provider\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workspace_that_is_no_single_value_is_of_the_wrong_type()
    {
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m", "WorkspaceId": { "id": "x" } } }""");

        Assert.Contains(new AppSettingsValidator().Validate(document), message => message.Path == "Llm:WorkspaceId" && message.Severity == ValidationSeverity.Error);
    }

    private static IEnumerable<JsonElement> Entries(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var entry in Entries(item))
                    yield return entry;
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("path", out _))
                yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var entry in Entries(property.Value))
                    yield return entry;
            }
        }
    }
}
