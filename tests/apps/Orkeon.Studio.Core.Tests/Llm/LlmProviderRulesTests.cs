using System.Reflection;
using System.Text.RegularExpressions;
using Orkeon.Constants.Llm;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Validation;
using Orkeon.Studio.Tests.Shared;

namespace Orkeon.Studio.Core.Tests.Llm;

/// <summary>
/// <see cref="LlmProviderRules"/> mirrors two lists of the engine: the provider modules the
/// <c>orkeon</c> tool registers (<c>CliLlmProviderModules.All</c>) and the vendor keys
/// <c>Provider</c> never names (<c>MockProviderSettings.IsBuiltInKey</c>). These tests fail when
/// either moves, before a file Studio accepts is refused at start.
/// </summary>
public sealed partial class LlmProviderRulesTests
{
    [Fact]
    public void The_modules_are_those_the_orkeon_tool_registers()
    {
        var source = File.ReadAllText(Path.Combine(
            OrkeonSources.Root(), "src", "scripting", "Orkeon.Scripting.Cli", "CliLlmProviderModules.cs"));
        var all = ModulesList().Match(source);

        Assert.True(all.Success, "CliLlmProviderModules.All is no longer written as a list Studio can read: re-read it and update LlmProviderRules.CliModuleKeys.");
        // The tool ships no module yet: a first one means a key Studio must accept.
        Assert.Equal("[]", Blanks().Replace(all.Groups["list"].Value, ""));
        Assert.Empty(LlmProviderRules.CliModuleKeys);
    }

    public static TheoryData<string> Keys()
    {
        var data = new TheoryData<string>();
        foreach (var key in LlmProviderKeys.All.Concat(LlmProviderKeys.Aliases.Keys)
                     .Concat([LlmProviderKeys.Custom, LlmProviderKeys.DockerModelRunner, LlmProviderKeys.AzureShort, LlmProviderKeys.Mock, LlmProviderKeys.None, "acme", ""])
                     .Distinct(StringComparer.Ordinal))
            data.Add(key);
        return data;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void A_vendor_key_is_the_one_the_engine_refuses_under_Provider(string key)
    {
        // Internal to the engine: reached by name, from the assembly of a public type beside it.
        var engine = typeof(Orkeon.Infrastructure.LLMs.Profiles.LlmSettings).Assembly
            .GetType("Orkeon.Infrastructure.LLMs.Mock.MockProviderSettings", throwOnError: true)!
            .GetMethod("IsBuiltInKey", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal((bool)engine.Invoke(null, [key])!, LlmProviderRules.IsVendorKey(key));
    }

    [Fact]
    public void A_section_without_a_provider_has_no_finding()
    {
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m", "Responses": "/x.jsonl" } }""");

        Assert.DoesNotContain(new AppSettingsValidator().Validate(document), message => message.Code == ValidationCodes.LlmProvider);
    }

    [Fact]
    public void A_provider_that_is_no_single_value_is_of_the_wrong_type()
    {
        var document = AppSettingsDocument.Parse("""{ "Llm": { "Model": "m", "Provider": ["mock"] } }""");

        Assert.Contains(new AppSettingsValidator().Validate(document), message => message.Path == "Llm:Provider" && message.Severity == ValidationSeverity.Error);
    }

    [GeneratedRegex(@"IReadOnlyList<ILlmProviderModule>\s+All\s*\{\s*get;\s*\}\s*=\s*(?<list>[^;]*);", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ModulesList();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Blanks();
}
