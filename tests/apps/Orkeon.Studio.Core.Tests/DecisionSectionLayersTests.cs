using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Tests;

/// <summary>
/// The decision settings as a run reads them beyond the exact keys of the file: every key in any letter
/// case, read and written where the file spells it; the environment variables laid under the file and the
/// <c>ORKEON_</c> ones above it; the runner's strict tool resolution; and the section of the provider in
/// use as the host's start validates it.
/// </summary>
public sealed class DecisionSectionLayersTests
{
    private static Func<string, string?> Environment(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    private static readonly Func<string, string?> NoEnvironment = _ => null;

    [Fact]
    public void A_provider_section_in_another_case_is_read_written_and_removed_where_the_file_spells_it()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"typesafe":{"Model":"a"}}}""");
        var typeSafe = document.Decision.For(DecisionProviderDescriptor.TypeSafe);

        Assert.True(typeSafe.Exists);
        Assert.Equal("a", typeSafe.Model);

        typeSafe.Model = "b";
        typeSafe.TimeoutSeconds = 12;
        Assert.Equal("b", document.GetString("Orkeon:typesafe:Model"));
        Assert.Equal(12, document.GetInt32("Orkeon:typesafe:TimeoutSeconds"));
        Assert.Equal(["typesafe"], document.Root["Orkeon"]!.AsObject().Select(p => p.Key));

        typeSafe.Remove();
        Assert.False(typeSafe.Exists);
        Assert.Empty(document.Root["Orkeon"]!.AsObject());
    }

    [Fact]
    public void Remove_takes_every_twin_of_a_section_the_configuration_merges()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Liquid":{},"liquid":{"Model":"x"},"Kev":{}}}""");
        var liquid = document.Decision.For(DecisionProviderDescriptor.Liquid);

        liquid.Remove();

        Assert.False(liquid.Exists);
        Assert.Equal(["Kev"], document.Root["Orkeon"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void Create_on_a_section_written_null_in_another_case_replaces_it_in_place()
    {
        var document = AppSettingsDocument.Parse("""{"orkeon":{"kev":null}}""");

        document.Decision.For(DecisionProviderDescriptor.Kev).Create();

        Assert.IsType<System.Text.Json.Nodes.JsonObject>(document.GetNode("orkeon:kev"));
        Assert.Equal(["orkeon"], document.Root.Select(p => p.Key));
    }

    [Fact]
    public void The_switch_and_the_provider_in_another_case_are_read_and_written_in_place()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Decision":{"provider":"liquid","enabled":false}}}""");
        var decision = document.Decision;

        Assert.Equal("liquid", decision.Provider);
        Assert.False(decision.Enabled);
        Assert.True(decision.Exists);

        decision.Provider = "typesafe";
        decision.Enabled = null;

        var written = document.Root["Orkeon"]!["Decision"]!.AsObject();
        Assert.Equal(["provider"], written.Select(p => p.Key));
        Assert.Equal("typesafe", decision.Provider);
        Assert.Equal("Orkeon:Decision:enabled", AppSettingsDocument.Parse("""{"Orkeon":{"Decision":{"enabled":1}}}""").Decision.EnabledPathAsWritten);
    }

    [Fact]
    public void A_blank_key_variable_gives_the_providers_and_a_written_one_is_trimmed()
    {
        var blank = AppSettingsDocument.Parse("""{"Orkeon":{"Liquid":{"ApiKeyEnvVar":"   "}}}""");
        var padded = AppSettingsDocument.Parse("""{"Orkeon":{"Liquid":{"ApiKeyEnvVar":" MY_LIQUID_KEY "}}}""");

        Assert.Equal(
            DecisionProviderDescriptor.Liquid.DefaultApiKeyVariable,
            blank.Decision.For(DecisionProviderDescriptor.Liquid).EffectiveApiKeyEnvVar);
        Assert.Equal("MY_LIQUID_KEY", padded.Decision.For(DecisionProviderDescriptor.Liquid).EffectiveApiKeyEnvVar);
    }

    [Fact]
    public void The_strict_tool_resolution_is_on_by_default_read_in_any_case_and_refused_when_unreadable()
    {
        Assert.True(AppSettingsDocument.CreateEmpty().Decision.Resolve(NoEnvironment).StrictTools);
        Assert.False(AppSettingsDocument.Parse("""{"orkeon":{"crewfactory":{"stricttools":false}}}""")
            .Decision.Resolve(NoEnvironment).StrictTools);
        Assert.False(AppSettingsDocument.Parse("""{"Orkeon":{"CrewFactory":{"StrictTools":" False "}}}""")
            .Decision.Resolve(NoEnvironment).StrictTools);

        var refused = AppSettingsDocument.Parse("""{"Orkeon":{"CrewFactory":{"StrictTools":"nope"}}}""").Decision.Resolve(NoEnvironment);
        Assert.Contains("'Orkeon:CrewFactory:StrictTools'", refused.Refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("nope", refused.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unprefixed_variable_lies_under_the_file_and_a_prefixed_one_above_it()
    {
        var empty = AppSettingsDocument.CreateEmpty().Decision;
        var named = empty.Resolve(Environment(("Orkeon__Decision__Provider", "typesafe")));
        Assert.Same(DecisionProviderDescriptor.TypeSafe, named.Provider);
        Assert.Equal(DecisionProviderSelection.Named, named.How);

        var file = AppSettingsDocument.Parse("""{"Orkeon":{"Decision":{"Provider":"liquid"}}}""").Decision;
        Assert.Same(DecisionProviderDescriptor.Liquid, file.Resolve(Environment(("Orkeon__Decision__Provider", "typesafe"))).Provider);
        Assert.Same(
            DecisionProviderDescriptor.TypeSafe,
            file.Resolve(Environment(("ORKEON_Orkeon__Decision__Provider", "typesafe"))).Provider);

        var off = file.Resolve(Environment(("ORKEON_Orkeon__Decision__Enabled", "false")));
        Assert.False(off.Enabled);
        Assert.False(off.CircuitOn);

        Assert.False(empty.Resolve(Environment(("ORKEON_Orkeon__CrewFactory__StrictTools", "false"))).StrictTools);
    }

    [Fact]
    public void A_provider_field_in_the_environment_makes_its_section_present_and_its_key_in_place()
    {
        var empty = AppSettingsDocument.CreateEmpty().Decision;

        var deduced = empty.Resolve(Environment(("Orkeon__Liquid__Model", "d1")));
        Assert.Same(DecisionProviderDescriptor.Liquid, deduced.Provider);
        Assert.Equal(DecisionProviderSelection.Deduced, deduced.How);
        Assert.Equal(DecisionProviderDescriptor.Liquid.DefaultApiKeyVariable, deduced.MissingKeyVariable);

        var keyed = empty.Resolve(Environment(("Orkeon__Liquid__Model", "d1"), ("ORKEON_Orkeon__Liquid__ApiKey", "secret")));
        Assert.Null(keyed.MissingKeyVariable);
    }

    [Fact]
    public void The_section_of_the_provider_in_use_is_refused_as_the_hosts_start_validates_it()
    {
        static string? Refusal(string json) => AppSettingsDocument.Parse(json).Decision.Resolve(NoEnvironment).Refusal;

        Assert.Equal(
            "Orkeon:TypeSafe:TimeoutSeconds: one second or more.",
            Refusal("""{"Orkeon":{"TypeSafe":{"TimeoutSeconds":0}}}"""));
        Assert.Equal(
            "Orkeon:TypeSafe:TimeoutSeconds: one second or more.; Orkeon:TypeSafe:MaxRetries: zero or more.",
            Refusal("""{"Orkeon":{"TypeSafe":{"TimeoutSeconds":-5,"MaxRetries":-1}}}"""));
        Assert.Equal(
            "Orkeon:Liquid:BaseUrl: an absolute URL.",
            Refusal("""{"Orkeon":{"Liquid":{"BaseUrl":"not-a-url"}}}"""));
        Assert.Contains(
            "Failed to convert",
            Refusal("""{"Orkeon":{"Liquid":{"MaxRetries":"many"}}}"""),
            StringComparison.Ordinal);

        // A blank address gives the provider's; a section left aside is bound by nobody.
        Assert.Null(Refusal("""{"Orkeon":{"Liquid":{"BaseUrl":"  ","TimeoutSeconds":5,"MaxRetries":0}}}"""));
        Assert.Null(Refusal("""{"Orkeon":{"Decision":{"Provider":"liquid"},"Liquid":{},"TypeSafe":{"TimeoutSeconds":0}}}"""));
        Assert.Null(Refusal("""{"Orkeon":{"Decision":{"Provider":"kev"}}}"""));
    }
}
