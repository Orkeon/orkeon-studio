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

    /// <summary>
    /// Every key the packed engine names, read off its own <c>LlmProviderKeys</c> by reflection - each
    /// <c>public const string</c>, each entry of <c>All</c>, each alias and what it stands for -, so a
    /// literal the engine adds is compared without this list being touched; and names it does not hold.
    /// </summary>
    public static TheoryData<string> Keys() => [.. EngineKeys()];

    private static List<string> EngineKeys()
    {
        var constants = typeof(LlmProviderKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);
        return
        [
            .. constants
                .Concat(LlmProviderKeys.All)
                .Concat(LlmProviderKeys.Aliases.Keys)
                .Concat(LlmProviderKeys.Aliases.Values)
                .Concat(["acme", "", "open-ai"])
                .Distinct(StringComparer.Ordinal),
        ];
    }

    [Fact]
    public void The_keys_compared_are_the_engines_own_list()
    {
        // Not vacuous: the reflection reaches the constants (a renamed type or a moved key would leave few).
        var keys = EngineKeys();
        Assert.Contains(LlmProviderKeys.Custom, keys);
        Assert.Contains(LlmProviderKeys.Mock, keys);
        Assert.Contains(LlmProviderKeys.None, keys);
        Assert.True(keys.Count > LlmProviderKeys.All.Count + 3, $"Only {keys.Count} keys read off LlmProviderKeys.");
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

    /// <summary>
    /// On a host that registers provider modules, a key none answers is told the keys it does, in the
    /// engine's order (<c>SettingsValidation.UnansweredProviderKeys</c>, ordinal), and a key one answers
    /// is accepted whatever its case.
    /// </summary>
    [Fact]
    public void A_host_with_modules_lists_them_and_accepts_theirs()
    {
        var section = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{ "Provider": "acme", "Model": "m" }""")!;

        var refusal = Assert.Single(LlmProviderRules.Check(section, "Llm", ["zeta", "alpha"]));
        Assert.Contains("(registered: alpha, zeta)", refusal.Text, StringComparison.Ordinal);

        Assert.Empty(LlmProviderRules.Check(section, "Llm", ["ACME"]));
    }

    /// <summary>
    /// A launch reads the refusals of the whole file: the default and each profile, the section found
    /// whatever its letter case, a profile that is no object left to the shape check.
    /// </summary>
    [Fact]
    public void The_refusals_of_a_file_are_those_of_the_default_and_of_each_profile_whatever_the_case()
    {
        var document = AppSettingsDocument.Parse("""
            { "llm": { "Provider": "openai", "Model": "m", "profiles": { "p": { "Provider": "mock", "Model": "m" }, "q": "x", "r": { "Model": "m" } } } }
            """);

        var refusals = LlmProviderRules.Check(document);

        Assert.Equal(["Llm:Provider", "Llm:Profiles:p:Responses"], refusals.Select(refusal => refusal.Path));
        Assert.All(refusals, refusal => Assert.Equal(ValidationSeverity.Error, refusal.Severity));
        Assert.Empty(LlmProviderRules.Check(AppSettingsDocument.Parse("""{ "Orkeon": {} }""")));
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
