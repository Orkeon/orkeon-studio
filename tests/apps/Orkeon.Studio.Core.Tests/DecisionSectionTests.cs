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

        Assert.True(document.Decision.Resolve(NoEnvironment).KeyMissing);
        Assert.Null(document.Decision.Resolve(NoEnvironment).MissingKeyVariableSetting);

        // The provider's own variable written out is named as when unset.
        var ownWritten = AppSettingsDocument.Parse("""{ "Orkeon": { "Liquid": { "ApiKeyEnvVar": " LIQUID_API_KEY " } } }""");
        Assert.Equal("LIQUID_API_KEY", ownWritten.Decision.Resolve(NoEnvironment).MissingKeyVariable);

        var inFile = AppSettingsDocument.Parse("""{ "Orkeon": { "Liquid": { "ApiKey": "k" } } }""");
        Assert.Null(inFile.Decision.Resolve(NoEnvironment).MissingKeyVariable);
        Assert.False(inFile.Decision.Resolve(NoEnvironment).KeyMissing);

        // A local server needs no key.
        var kev = AppSettingsDocument.Parse("""{ "Orkeon": { "Decision": { "Provider": "kev" } } }""");
        Assert.Null(kev.Decision.Resolve(NoEnvironment).MissingKeyVariable);
        Assert.False(kev.Decision.Resolve(NoEnvironment).KeyMissing);
    }

    /// <summary>
    /// Another variable than the provider's own is read, and never named: what is written in
    /// <c>ApiKeyEnvVar</c> may be a key pasted there by mistake, whatever its shape. As the engine
    /// (<c>DecisionApiKey.Remedy</c>, Orkeon #467), Studio names the setting that holds it instead.
    /// </summary>
    [Theory]
    [InlineData("MY_KEY")]
    [InlineData("sk_live_0123456789abcdef")]
    [InlineData("sk-ant-api03-0123456789")]
    [InlineData("liquid_api_key")]
    public void A_variable_other_than_the_providers_own_is_named_by_its_setting_never_by_its_value(string written)
    {
        var document = AppSettingsDocument.Parse($$"""{ "Orkeon": { "Liquid": { "ApiKeyEnvVar": "{{written}}" } } }""");

        var missing = document.Decision.Resolve(NoEnvironment);
        Assert.True(missing.KeyMissing);
        Assert.Null(missing.MissingKeyVariable);
        Assert.Equal("Orkeon:Liquid:ApiKeyEnvVar", missing.MissingKeyVariableSetting);

        // That variable is the one read: holding a key, nothing is missing; the provider's own is not read.
        var held = document.Decision.Resolve(name => name == written ? "k" : null);
        Assert.False(held.KeyMissing);
        Assert.True(document.Decision.Resolve(name => name == "LIQUID_API_KEY" ? "k" : null).KeyMissing);
    }

    /// <summary>
    /// The engine's rule, read in the Orkeon checkout (<c>DecisionApiKey</c>): the variable read is
    /// <c>ApiKeyEnvVar</c> trimmed, the provider's own when blank; it is named only when it is the
    /// provider's own, compared ordinally; otherwise the remedy names the settings alone.
    /// </summary>
    [Fact]
    public void The_engine_names_the_missing_keys_variable_as_Studio_does()
    {
        var path = Path.Combine(
            Orkeon.Studio.Tests.Shared.OrkeonSources.Root(), "src", "core", "Orkeon.Infrastructure", "Decision", "SystemOne", "DecisionApiKey.cs");
        Assert.True(File.Exists(path), "DecisionApiKey.cs is no longer in the Orkeon checkout.");
        var source = File.ReadAllText(path);

        Assert.Contains(
            "string.IsNullOrWhiteSpace(options.ApiKeyEnvVar) ? provider.DefaultApiKeyVariable : options.ApiKeyEnvVar.Trim()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(Variable(provider, options), provider.DefaultApiKeyVariable, StringComparison.Ordinal)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("$\"set the environment variable {provider.DefaultApiKeyVariable}, or {provider.Section}:ApiKey\"", source, StringComparison.Ordinal);
        Assert.Contains("$\"set the variable named by {provider.Section}:ApiKeyEnvVar, or {provider.Section}:ApiKey\"", source, StringComparison.Ordinal);
        Assert.Equal("Orkeon:Liquid:ApiKeyEnvVar", DecisionSection.ApiKeyEnvVarSetting(DecisionProviderDescriptor.Liquid));
    }

    [Fact]
    public void A_section_written_null_counts_as_written_as_the_engine_deduces_from_it()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Liquid": null } }""");

        Assert.True(document.Decision.For(DecisionProviderDescriptor.Liquid).Exists);
        Assert.Same(DecisionProviderDescriptor.Liquid, document.Decision.Resolve(NoEnvironment).Provider);
    }

    /// <summary>
    /// A fail-closed LLM judge cannot judge without the decision model: the host refuses to start once
    /// the circuit is off, naming the settings' switch before the missing provider, as the engine does.
    /// </summary>
    [Theory]
    [InlineData("""{ "Orkeon": { "Guardian": { "Llm": { "Enabled": true } } } }""", "no decision provider resolved")]
    [InlineData("""{ "Orkeon": { "Decision": { "Enabled": false }, "Liquid": {}, "Guardian": { "Llm": { "Enabled": true } } } }""", "Orkeon:Decision:Enabled=false")]
    public void A_fail_closed_llm_judge_refuses_the_start_once_the_circuit_is_off(string json, string cause)
    {
        var status = AppSettingsDocument.Parse(json).Decision.Resolve(_ => null);

        Assert.True(status.ClosedJudge);
        Assert.Contains("Orkeon:Guardian:Llm:Enabled is true with FailMode Closed", status.Refusal, StringComparison.Ordinal);
        Assert.Contains(cause, status.Refusal, StringComparison.Ordinal);
        Assert.False(status.CircuitOn);
    }

    [Theory]
    [InlineData("""{ "Orkeon": { "Guardian": { "Llm": { "Enabled": true, "FailMode": "Open" } } } }""")]
    [InlineData("""{ "Orkeon": { "Guardian": { "Enabled": false, "Llm": { "Enabled": true } } } }""")]
    [InlineData("""{ "Orkeon": { "Guardian": { "Llm": { "Enabled": false } } } }""")]
    public void A_judge_that_fails_open_is_off_or_has_its_guardian_off_refuses_nothing(string json)
    {
        var status = AppSettingsDocument.Parse(json).Decision.Resolve(_ => null);

        Assert.False(status.ClosedJudge);
        Assert.Null(status.Refusal);
        Assert.Null(status.RefusalOfRunSwitch);
    }

    [Fact]
    public void A_fail_closed_llm_judge_starts_with_the_circuit_on_and_refuses_the_runs_own_switch()
    {
        var status = AppSettingsDocument
            .Parse("""{ "Orkeon": { "Liquid": { "ApiKey": "in-file" }, "Guardian": { "Llm": { "Enabled": true } } } }""")
            .Decision.Resolve(_ => null);

        Assert.True(status.CircuitOn);
        Assert.Null(status.Refusal);
        Assert.Contains("--no-decision", status.RefusalOfRunSwitch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ladder_and_the_levels_are_written_as_lists_and_removed_when_emptied()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Decision": { "Provider": "liquid" } } }""");
        var section = document.Decision;
        Assert.Empty(section.RouteLadder);
        Assert.Empty(section.EffortLevels);

        section.RouteLadder = DecisionSection.SplitList(" default , strong;\n");
        section.EffortLevels = ["low", "high"];

        var reparsed = AppSettingsDocument.Parse(document.ToJson());
        Assert.Equal(["default", "strong"], reparsed.Decision.RouteLadder);
        Assert.Equal(["low", "high"], reparsed.Decision.EffortLevels);
        Assert.Equal("liquid", reparsed.Decision.Provider);

        section.RouteLadder = [];
        section.EffortLevels = [];
        Assert.False(document.ContainsPath(DecisionSection.RouteLadderPath));
        Assert.False(document.ContainsPath(DecisionSection.EffortLevelsPath));
    }

    /// <summary>The start refuses what the engine's own rules refuse in the two lists, whatever the circuit.</summary>
    [Theory]
    [InlineData("""{ "Orkeon": { "Decision": { "Route": { "Ladder": ["default"] } } } }""", "a route needs two or more")]
    [InlineData("""{ "Orkeon": { "Decision": { "Route": { "Ladder": ["default", "auto"] } } } }""", "the name of the route itself")]
    [InlineData("""{ "Orkeon": { "Decision": { "Route": { "Ladder": ["fast", "FAST"] } } } }""", "more than once")]
    [InlineData("""{ "Orkeon": { "Decision": { "Effort": { "Levels": ["low", "extreme"] } } } }""", "is not a thinking level")]
    [InlineData("""{ "Orkeon": { "Decision": { "Effort": { "Levels": ["high", "low"] } } } }""", "cheapest first")]
    [InlineData("""{ "Orkeon": { "Decision": { "Profiles": { "auto": { "Target": "default" } } } } }""", "is a reserved profile name")]
    public void A_ladder_levels_or_a_decision_profile_the_engine_refuses_stop_the_start(string json, string reason)
    {
        var status = AppSettingsDocument.Parse(json).Decision.Resolve(_ => null);

        Assert.Contains(reason, status.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ladder_serves_when_each_of_its_two_rungs_or_more_is_a_profile_of_the_settings()
    {
        var served = AppSettingsDocument.Parse("""
            { "Llm": { "Model": "small", "Profiles": { "strong": { "Model": "big" } } },
              "Orkeon": { "Decision": { "Route": { "Ladder": ["default", "Strong"] } } } }
            """).Decision.Resolve(_ => null);
        Assert.Null(served.Refusal);
        Assert.Equal(["default", "Strong"], served.RouteLadder);
        Assert.Empty(served.UnknownRungs);
        Assert.True(served.ServesRouteAuto);

        var unknown = AppSettingsDocument.Parse("""
            { "Orkeon": { "Decision": { "Route": { "Ladder": ["default", "strong"] } } } }
            """).Decision.Resolve(_ => null);
        Assert.Null(unknown.Refusal);
        Assert.Equal(["strong"], unknown.UnknownRungs);
        Assert.False(unknown.ServesRouteAuto);

        Assert.False(AppSettingsDocument.Parse("{}").Decision.Resolve(_ => null).ServesRouteAuto);
    }

    [Fact]
    public void A_ladder_of_the_environment_is_read_above_the_file()
    {
        var environment = new Dictionary<string, string?>
        {
            ["ORKEON_Orkeon__Decision__Route__Ladder__0"] = "default",
            ["ORKEON_Orkeon__Decision__Route__Ladder__1"] = "default",
        };

        var status = AppSettingsDocument.Parse("{}").Decision.Resolve(name => environment.GetValueOrDefault(name));

        // One variable per rung, as the configuration reads a list: the ladder is seen, and refused.
        Assert.Contains("more than once", status.Refusal, StringComparison.Ordinal);
    }
}
