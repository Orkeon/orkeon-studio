using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// What Studio says of the decision profiles and of the guardian the engine's start refuses: where the
/// status of the decision settings puts the refusal, and what the settings check reports. The
/// sentences themselves are held to the engine by <see cref="DecisionProfileRulesOracleTests"/> and by
/// <c>EngineStartAndLoadOracleTests</c>.
/// </summary>
public sealed class GuardianAndDecisionProfileProblemsTests
{
    private static string? NoEnvironment(string name) => null;

    [Fact]
    public void A_refused_decision_profile_is_the_refusal_of_the_status_met_while_the_host_is_composed()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Orkeon": { "Decision": { "Profiles": { "p": { "Stages": [ { "Kind": "guard", "Risks": { "x": "y" } } ] } } }, "Compaction": { "TriggerShare": 2 } } }""");

        var status = document.Decision.Resolve(NoEnvironment);

        Assert.Equal("Orkeon:Decision:Profiles:p:Target: a profile name is required.", status.Refusal);
        Assert.Equal(DecisionRefusalStage.Composition, status.RefusalStage);
        Assert.Equal(["p"], status.DecisionProfiles);
    }

    [Fact]
    public void The_reserved_name_is_still_refused_first_among_the_rules_of_a_profile()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Decision": { "Profiles": { "Auto": { } } } } }""");

        var status = document.Decision.Resolve(NoEnvironment);

        Assert.Equal(DecisionRefusalStage.Composition, status.RefusalStage);
        Assert.StartsWith("Orkeon:Decision:Profiles:Auto: ", status.Refusal, StringComparison.Ordinal);
        Assert.Equal(
            ["Orkeon:Decision:Profiles:Auto", "Orkeon:Decision:Profiles:Auto:Target", "Orkeon:Decision:Profiles:Auto:Stages"],
            document.Decision.ProfileProblems().Select(problem => problem.Path));
    }

    [Fact]
    public void A_rule_of_the_guardian_is_the_refusal_of_the_status_before_the_circuits_switch()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Orkeon": { "Guardian": { "Llm": { "Enabled": true, "MaxContentChars": 0 } }, "Decision": { "Enabled": "x" } } }""");

        var status = document.Decision.Resolve(NoEnvironment);

        Assert.Equal("Orkeon:Guardian:Llm:MaxContentChars: one character or more.", status.Refusal);
        Assert.Equal(DecisionRefusalStage.Switches, status.RefusalStage);
    }

    [Fact]
    public void The_guardian_says_its_values_then_its_rules_then_its_keys_each_at_its_key()
    {
        var document = AppSettingsDocument.Parse("""
            { "Orkeon": { "Guardian": {
                "Enable": true,
                "DefaultPolicy": { "MaxDelegationDepth": "deep" },
                "Llm": { "Phases": ["Delegation", "Input"], "BlockThreshold": 0.5, "WarnThreshold": 1.5, "MaxContentChars": -1, "FailMod": "Open" } } } }
            """);

        var problems = document.Guardian.Problems();

        Assert.Equal(
            [
                "Orkeon:Guardian:DefaultPolicy:MaxDelegationDepth", "Orkeon:Guardian:Llm:Phases", "Orkeon:Guardian:Llm:WarnThreshold",
                "Orkeon:Guardian:Llm:MaxContentChars", "Orkeon:Guardian:Enable", "Orkeon:Guardian:Llm:FailMod",
            ],
            problems.Select(problem => problem.Path));
        Assert.Equal(
            "Orkeon:Guardian:Llm:Phases: 'Delegation' is not a phase a model judges. Only `Input` and `Output` are "
            + "(`ToolExecution` and `Delegation` are checked by rules); remove the other values or leave the list empty to judge both.",
            problems[1].Problem);
        Assert.Equal("Orkeon:Guardian:Llm:BlockThreshold and WarnThreshold: probabilities in [0, 1].", problems[2].Problem);
    }

    [Fact]
    public void A_guardian_and_profiles_the_engine_accepts_say_nothing()
    {
        var document = AppSettingsDocument.Parse("""
            { "Orkeon": {
                "Guardian": { "Enabled": true, "DefaultPolicy": { "MaxDelegationDepth": 3 }, "Llm": { "Phases": ["input"], "Risks": { "leak": "a secret leaves" }, "FailMode": "Open" } },
                "Decision": { "Enabled": true, "Provider": "typesafe", "Route": { "Ladder": ["default", "default"] },
                    "Profiles": { "p": { "Target": "default", "FailClosed": true, "Stages": [ { "Kind": "guard", "Risks": { "x": "y" } } ] } } },
                "TypeSafe": { "Model": "m" } } }
            """);

        Assert.Empty(document.Guardian.Problems());
        Assert.Empty(document.Decision.ProfileProblems());
        Assert.Empty(document.Decision.UnknownKeys());
        Assert.DoesNotContain(new AppSettingsValidator().Validate(document), message => message.Code == ValidationCodes.EngineSettingRefused);
    }

    [Fact]
    public void The_settings_check_reports_them_as_errors_at_their_keys()
    {
        var messages = new AppSettingsValidator().Validate(AppSettingsDocument.Parse("""
            { "Orkeon": {
                "Guardian": { "Llm": { "BlockThreshold": 2 } },
                "Decision": { "Profiles": { "p": { "Target": "default", "Stages": [ { "Kind": "sentinel", "Risk": { } } ] } } },
                "Kev": { "Modle": "x" } } }
            """));

        var errors = messages.Where(message => message.Code == ValidationCodes.EngineSettingRefused).ToList();
        Assert.All(errors, error => Assert.Equal(ValidationSeverity.Error, error.Severity));
        Assert.Equal(
            ["Orkeon:Guardian:Llm:BlockThreshold", "Orkeon:Decision:Profiles:p:Stages:0:Kind", "Orkeon:Decision:Profiles:p:Stages:0:Risk", "Orkeon:Kev:Modle"],
            errors.Select(error => error.Path));
        Assert.Equal("Orkeon:Decision:Profiles:p:Stages:0:Kind: 'sentinel' is not a stage; expected route, guard or verify.", errors[1].Text);
    }
}
