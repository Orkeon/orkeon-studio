using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Tests;

/// <summary>
/// The typed view over the decision models: the circuit's switch and provider, each provider's section,
/// every write in place, and the provider a run gets, resolved by the engine's own
/// <see cref="DecisionProviderChoice"/> over the document.
/// </summary>
public sealed class DecisionSectionTests
{
    private static string? NoEnvironment(string name) => null;

    [Fact]
    public void An_empty_document_has_the_circuit_on_and_no_provider()
    {
        var decision = AppSettingsDocument.CreateEmpty().Decision;

        Assert.Null(decision.Enabled);
        Assert.True(decision.IsEnabled);
        Assert.Null(decision.Provider);
        var status = decision.Resolve(NoEnvironment);
        Assert.True(status.Enabled);
        Assert.Null(status.Provider);
        Assert.Null(status.Refusal);
        Assert.False(status.CircuitOn);
    }

    [Fact]
    public void The_fields_round_trip_and_keep_the_keys_studio_does_not_model()
    {
        var document = AppSettingsDocument.Parse("""
            {
              "Orkeon": {
                "Decision": { "Profiles": { "guarded": { "Target": "default" } } },
                "Liquid": { "ApiKey": "in-file" }
              }
            }
            """);
        var decision = document.Decision;
        var liquid = decision.For(DecisionProviderDescriptor.Liquid);

        decision.Enabled = false;
        decision.Provider = " liquid ";
        liquid.Model = "d1-ultra";
        liquid.TimeoutSeconds = 60;
        liquid.MaxRetries = 1;
        liquid.BaseUrl = "   ";

        var reparsed = AppSettingsDocument.Parse(document.ToJson());
        Assert.False(reparsed.Decision.Enabled);
        Assert.Equal("liquid", reparsed.Decision.Provider);
        var section = reparsed.Decision.For(DecisionProviderDescriptor.Liquid);
        Assert.Equal("d1-ultra", section.Model);
        Assert.Equal(60, section.TimeoutSeconds);
        Assert.Equal(1, section.MaxRetries);
        Assert.Null(section.BaseUrl);
        Assert.True(section.HasApiKeyInFile);
        Assert.Equal("default", reparsed.GetString("Orkeon:Decision:Profiles:guarded:Target"));
    }

    [Fact]
    public void Clearing_a_field_removes_its_key_so_the_engine_gives_its_default()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var typesafe = document.Decision.For(DecisionProviderDescriptor.TypeSafe);

        typesafe.Model = "jev-1.14.0";
        typesafe.Model = "";
        document.Decision.Provider = "typesafe";
        document.Decision.Provider = null;

        Assert.False(document.ContainsPath("Orkeon:TypeSafe:Model"));
        Assert.False(document.ContainsPath("Orkeon:Decision:Provider"));
        Assert.Equal("TYPESAFE_API_KEY", typesafe.EffectiveApiKeyEnvVar);
    }

    [Fact]
    public void A_section_written_empty_is_enough_for_the_engine_to_deduce_its_provider()
    {
        var document = AppSettingsDocument.CreateEmpty();
        document.Decision.For(DecisionProviderDescriptor.Liquid).Create();

        var status = AppSettingsDocument.Parse(document.ToJson()).Decision.Resolve(NoEnvironment);

        Assert.Same(DecisionProviderDescriptor.Liquid, status.Provider);
        Assert.Equal(DecisionProviderSelection.Deduced, status.How);
        Assert.True(status.CircuitOn);
    }

    [Fact]
    public void A_key_in_the_environment_is_enough_when_no_section_is_written()
    {
        var status = AppSettingsDocument.CreateEmpty().Decision.Resolve(
            name => name == "TYPESAFE_API_KEY" ? "secret" : null);

        Assert.Same(DecisionProviderDescriptor.TypeSafe, status.Provider);
    }

    [Fact]
    public void A_named_provider_wins_and_the_other_sections_are_said_ignored()
    {
        var document = AppSettingsDocument.Parse("""
            { "Orkeon": { "Decision": { "Provider": "kev" }, "TypeSafe": {}, "Liquid": {} } }
            """);

        var status = document.Decision.Resolve(NoEnvironment);

        Assert.Same(DecisionProviderDescriptor.Kev, status.Provider);
        Assert.Equal(DecisionProviderSelection.Named, status.How);
        Assert.Equal(["Orkeon:TypeSafe", "Orkeon:Liquid"], status.IgnoredSections);
    }

    [Fact]
    public void Two_deducible_providers_are_the_refusal_the_engine_starts_with()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "TypeSafe": {}, "Liquid": {} } }""");

        var status = document.Decision.Resolve(NoEnvironment);

        Assert.Null(status.Provider);
        Assert.Contains("TypeSafe", status.Refusal, StringComparison.Ordinal);
        Assert.False(status.CircuitOn);
    }

    [Fact]
    public void An_unknown_provider_name_is_refused()
    {
        var document = AppSettingsDocument.CreateEmpty();
        document.Decision.Provider = "acme";

        Assert.Contains("acme", document.Decision.Resolve(NoEnvironment).Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void The_switch_off_keeps_the_provider_but_turns_the_circuit_off()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Decision": { "Enabled": false }, "Liquid": {} } }""");

        var status = document.Decision.Resolve(NoEnvironment);

        Assert.Same(DecisionProviderDescriptor.Liquid, status.Provider);
        Assert.False(status.Enabled);
        Assert.False(status.CircuitOn);
    }

    [Fact]
    public void The_switch_reads_as_the_engine_binds_it_and_an_unreadable_one_is_refused()
    {
        var lowered = AppSettingsDocument.Parse("""{ "orkeon": { "decision": { "enabled": "False" } } }""").Decision.Resolve(NoEnvironment);
        Assert.False(lowered.Enabled);

        var unreadable = AppSettingsDocument.Parse("""{ "Orkeon": { "Decision": { "Enabled": "off" } } }""").Decision.Resolve(NoEnvironment);
        Assert.Contains("Orkeon:Decision:Enabled", unreadable.Refusal, StringComparison.Ordinal);
        Assert.False(unreadable.CircuitOn);
    }

    [Fact]
    public void A_hosted_provider_without_its_key_says_which_variable_is_empty()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Liquid": {} } }""");

        Assert.Equal("LIQUID_API_KEY", document.Decision.Resolve(NoEnvironment).MissingKeyVariable);
        Assert.Null(document.Decision.Resolve(name => name == "LIQUID_API_KEY" ? "k" : null).MissingKeyVariable);

        var custom = AppSettingsDocument.Parse("""{ "Orkeon": { "Liquid": { "ApiKeyEnvVar": "MY_KEY" } } }""");
        Assert.Equal("MY_KEY", custom.Decision.Resolve(NoEnvironment).MissingKeyVariable);

        var inFile = AppSettingsDocument.Parse("""{ "Orkeon": { "Liquid": { "ApiKey": "k" } } }""");
        Assert.Null(inFile.Decision.Resolve(NoEnvironment).MissingKeyVariable);

        // A local server needs no key.
        var kev = AppSettingsDocument.Parse("""{ "Orkeon": { "Decision": { "Provider": "kev" } } }""");
        Assert.Null(kev.Decision.Resolve(NoEnvironment).MissingKeyVariable);
    }

    [Fact]
    public void A_section_written_null_counts_as_written_as_the_engine_deduces_from_it()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Liquid": null } }""");

        Assert.True(document.Decision.For(DecisionProviderDescriptor.Liquid).Exists);
        Assert.Same(DecisionProviderDescriptor.Liquid, document.Decision.Resolve(NoEnvironment).Provider);
    }
}
