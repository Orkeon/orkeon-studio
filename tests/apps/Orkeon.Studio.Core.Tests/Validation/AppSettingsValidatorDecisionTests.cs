using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Tests.Doubles;
using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Core.Tests.Validation;

/// <summary>
/// The check before saving says what the host of a run refuses in the decision circuit's settings, each
/// once: the profiles' and the keys' findings merged as the host's walk merges them, then what the
/// circuit's resolution refuses (its switches, its ladder, the provider in use, a profile's name, the
/// fail-closed judge), unless it is said already. What depends on the run's environment is a warning.
/// </summary>
public sealed class AppSettingsValidatorDecisionTests
{
    private const string Llm = "\"Llm\": { \"BaseUrl\": \"http://localhost:11434\", \"Model\": \"qwen3\" }";

    private static List<ValidationMessage> Refused(string orkeon) =>
        [.. new AppSettingsValidator(new FakeDirectoryProbe())
            .Validate(AppSettingsDocument.Parse($$"""{ {{Llm}}, "Orkeon": { {{orkeon}} } }"""))
            .Where(message => message.Code == ValidationCodes.EngineSettingRefused)];

    [Fact]
    public void A_value_the_walk_and_the_profiles_both_see_is_said_once()
    {
        var refused = Refused("""
            "TypeSafe": { "Model": "m" },
            "Decision": { "Profiles": { "p": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" }, "BlockThreshold": "x" } ] }, "q": "x" } }
            """);

        Assert.Equal(refused.Count, refused.Select(message => message.Text).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(refused.Count, refused.Select(message => message.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(refused, message => message.Path?.EndsWith(":BlockThreshold", StringComparison.Ordinal) == true);
        Assert.Contains(refused, message => message.Path == "Orkeon:Decision:Profiles:q");
    }

    public static TheoryData<string, string> Resolutions => new()
    {
        { "a switch that is no boolean", "\"Decision\": { \"Enabled\": \"yes\" }" },
        { "a tool switch that is no boolean", "\"TypeSafe\": { \"Model\": \"m\" }, \"CrewFactory\": { \"StrictTools\": \"yes\" }" },
        { "a ladder of one rung", "\"Decision\": { \"Route\": { \"Ladder\": [\"default\"] } }" },
        { "a provider no section answers", "\"Decision\": { \"Provider\": \"nope\" }" },
        { "a decision profile named default", "\"Decision\": { \"Profiles\": { \"Default\": { \"Target\": \"default\", \"Stages\": [ { \"Kind\": \"guard\", \"Risks\": { \"x\": \"y\" } } ] } } }" },
    };

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void What_the_resolution_refuses_is_an_error_said_once(string what, string orkeon)
    {
        var document = AppSettingsDocument.Parse($$"""{ {{Llm}}, "Orkeon": { {{orkeon}} } }""");
        var refusal = document.Decision.Resolve(_ => null).Refusal;
        var refused = Refused(orkeon);

        Assert.True(refusal is not null, what);
        var said = Assert.Single(refused, message => message.Text == refusal);
        Assert.Equal(ValidationSeverity.Error, said.Severity);
    }

    [Fact]
    public void Each_failure_of_the_provider_in_use_is_said_at_its_key()
    {
        var refused = Refused("\"TypeSafe\": { \"Model\": \"m\", \"TimeoutSeconds\": 0, \"MaxRetries\": -1 }");

        Assert.Equal(["Orkeon:TypeSafe:TimeoutSeconds", "Orkeon:TypeSafe:MaxRetries"], refused.Select(message => message.Path));
        Assert.All(refused, message => Assert.Equal(ValidationSeverity.Error, message.Severity));
    }

    [Fact]
    public void A_name_a_decision_profile_shares_with_a_model_profile_is_an_error_at_the_profile()
    {
        var document = AppSettingsDocument.Parse("""
            { "Llm": { "BaseUrl": "http://localhost:11434", "Model": "qwen3", "Profiles": { "Strong": { "Model": "big" } } },
              "Orkeon": { "Decision": { "Profiles": { "strong": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" } } ] } } } } }
            """);

        var said = Assert.Single(new AppSettingsValidator(new FakeDirectoryProbe()).Validate(document), message => message.Text == "Two LLM profiles are named 'strong'.");

        Assert.Equal(ValidationSeverity.Error, said.Severity);
        Assert.Equal("Orkeon:Decision:Profiles:strong", said.Path);
    }

    /// <summary>
    /// The fail-closed judge refuses a circuit off. Off by its switch, that is certain: an error. Off for a
    /// provider or a key the file does not hold, the run's environment may still lay them: a warning, in
    /// the engine's sentence.
    /// </summary>
    [Fact]
    public void A_fail_closed_judge_over_a_circuit_off_is_said_as_certain_as_the_cause()
    {
        const string judge = "\"Guardian\": { \"Llm\": { \"Enabled\": true } }";

        var off = Assert.Single(Refused("\"TypeSafe\": { \"Model\": \"m\" }, \"Decision\": { \"Enabled\": false }, " + judge));
        var keyless = Assert.Single(Refused("\"Liquid\": { \"Model\": \"m\" }, " + judge));

        Assert.Equal(ValidationSeverity.Error, off.Severity);
        Assert.Equal(ValidationSeverity.Warning, keyless.Severity);
        Assert.Contains("the decision circuit is off: no Liquid API key: set the environment variable LIQUID_API_KEY, or Orkeon:Liquid:ApiKey", keyless.Text, StringComparison.Ordinal);
        Assert.Empty(Refused("\"Liquid\": { \"Model\": \"m\" }, \"Guardian\": { \"Llm\": { \"Enabled\": true, \"FailMode\": \"Open\" } }"));
    }
}
