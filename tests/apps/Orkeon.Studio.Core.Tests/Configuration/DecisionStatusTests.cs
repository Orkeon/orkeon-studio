using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// What the status of the decision settings holds beside its refusal: the stage the host meets it at,
/// the decision profiles and those that block every call with the circuit off, the judge, each read
/// before anything is judged so a refusal hides none of them; and the lists of the section, read as
/// the binder reads them whatever shape the file gives them.
/// </summary>
public sealed class DecisionStatusTests
{
    private static readonly Func<string, string?> NoEnvironment = _ => null;

    private const string Profiles = """
        "Profiles": {
          "open": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" } } ] },
          "closed-route": { "Target": "default", "FailClosed": true, "Stages": [ { "Kind": "route", "Routes": { "default": "all" } } ] },
          "Closed-Guard": { "Target": "default", "failClosed": "True", "Stages": [ { "Kind": "route", "Routes": { "default": "all" } }, { "kind": " GUARD ", "Risks": { "x": "y" } } ] },
          "stages-as-an-object": { "Target": "default", "FailClosed": true, "Stages": { "0": { "Kind": "Guard", "Risks": { "x": "y" } } } }
        }
        """;

    private static DecisionStatus Status(string orkeon, string llm = "") =>
        AppSettingsDocument.Parse($$"""{ "Llm": { {{llm}} }, "Orkeon": { {{orkeon}} } }""").Decision.Resolve(NoEnvironment);

    [Fact]
    public void The_profiles_that_block_every_call_are_the_fail_closed_ones_that_carry_a_guard()
    {
        var status = Status($$"""  "Decision": { {{Profiles}} }  """);

        Assert.Null(status.Refusal);
        Assert.Equal(DecisionRefusalStage.None, status.RefusalStage);
        Assert.Equal(["Closed-Guard", "closed-route", "open", "stages-as-an-object"], status.DecisionProfiles);
        Assert.Equal(["Closed-Guard", "stages-as-an-object"], status.BlockingProfiles);
        Assert.Empty(Status("").BlockingProfiles);
    }

    /// <summary>
    /// A value inside a profile the binder cannot convert was dropped without a word; the start refuses
    /// it since Orkeon #454, in the binder's sentence, while the host is composed, as the profiles' rules are.
    /// </summary>
    [Fact]
    public void A_profile_value_the_binder_cannot_convert_is_refused()
    {
        var status = Status("""
              "Decision": { "Profiles": { "unreadable": { "Target": "default", "FailClosed": "maybe", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" } } ] } } }
            """);

        Assert.Equal(DecisionRefusalStage.Composition, status.RefusalStage);
        Assert.Contains("'maybe' at 'Orkeon:Decision:Profiles:unreadable:FailClosed'", status.Refusal, StringComparison.Ordinal);
    }

    public static TheoryData<string, DecisionRefusalStage, string> Refusals => new()
    {
        { """ "CrewFactory": { "StrictTools": "y" }, "Decision": { "Provider": "nope" } """, DecisionRefusalStage.Composition, "'Orkeon:CrewFactory:StrictTools'" },
        { """ "Decision": { "Provider": "nope", "Enabled": "x" } """, DecisionRefusalStage.Composition, "'nope'" },
        { """ "Decision": { "Route": { "Ladder": ["auto"] }, "Enabled": "x" } """, DecisionRefusalStage.Picks, "Orkeon:Decision:Route:Ladder names one profile" },
        { """ "Decision": { "Effort": { "Levels": ["high", "low"] } }, "Guardian": { "Enabled": "z" } """, DecisionRefusalStage.Picks, "Orkeon:Decision:Effort:Levels" },
        { """ "Decision": { "Enabled": "x" }, "Guardian": { "Enabled": "z" }, "TypeSafe": { "TimeoutSeconds": 0 } """, DecisionRefusalStage.Switches, "'Orkeon:Guardian:Enabled'" },
        { """ "Decision": { "Enabled": [] }, "TypeSafe": { "TimeoutSeconds": 0 } """, DecisionRefusalStage.Switches, "value '' at 'Orkeon:Decision:Enabled'" },
        { """ "Decision": { "Enabled": "" } """, DecisionRefusalStage.Switches, "value '' at 'Orkeon:Decision:Enabled'" },
        { """ "Guardian": { "Llm": { "Enabled": true } }, "TypeSafe": { "TimeoutSeconds": 0 } """, DecisionRefusalStage.Provider, "Orkeon:TypeSafe:TimeoutSeconds" },
        { """ "Guardian": { "Llm": { "Enabled": true } } """, DecisionRefusalStage.Judge, "Orkeon:Guardian:Llm:Enabled is true with FailMode Closed" },
    };

    /// <summary>
    /// The status holds one refusal, the first the host meets, and says at which stage; what the file
    /// tells besides is still there: the decision profiles, those that block, the judge.
    /// </summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_refusal_is_the_first_the_host_meets_and_hides_nothing_else_the_file_tells(
        string orkeon, DecisionRefusalStage stage, string sentence)
    {
        // The profiles under a twin of the section in another letter case: the two merge, as in a run.
        var status = Status($$"""  {{orkeon}}, "decision": { {{Profiles}} }  """);

        Assert.Contains(sentence, status.Refusal, StringComparison.Ordinal);
        Assert.Equal(stage, status.RefusalStage);
        Assert.False(status.CircuitOn);
        Assert.Equal(4, status.DecisionProfiles.Count);
        Assert.Equal(["Closed-Guard", "stages-as-an-object"], status.BlockingProfiles);
        Assert.Equal(orkeon.Contains("\"Llm\": { \"Enabled\": true }", StringComparison.Ordinal), status.ClosedJudge);
    }

    /// <summary>
    /// The engine registers a decision profile as a profile, after the model profiles: a name both
    /// sections give refuses the start, whatever the crew, in the spelling of the decision profile.
    /// </summary>
    [Fact]
    public void A_name_both_a_model_profile_and_a_decision_profile_take_refuses_the_start()
    {
        const string Decision = """ "Decision": { "Profiles": { "strong": { "Target": "default", "Stages": [ { "Kind": "guard", "Risks": { "x": "y" } } ] } } } """;

        var twice = Status(Decision, """ "Profiles": { "Strong": { "Model": "m" } } """);
        Assert.Equal("Two LLM profiles are named 'strong'.", twice.Refusal);
        Assert.Equal(DecisionRefusalStage.Profiles, twice.RefusalStage);
        Assert.Equal(["strong"], twice.DecisionProfiles);

        Assert.Null(Status(Decision, """ "Profiles": { "stronger": { "Model": "m" } } """).Refusal);
    }

    /// <summary>
    /// The circuit's switch is on when its key is absent; written without a value, a JSON null or an
    /// empty object, it is the binder's false; an empty text, which an empty list also is to the binder,
    /// is refused as any text that is neither true nor false.
    /// </summary>
    [Theory]
    [InlineData("null", true, false)]
    [InlineData("{}", true, false)]
    [InlineData("true", true, true)]
    [InlineData("\" false \"", true, false)]
    [InlineData("false", true, false)]
    [InlineData("\"\"", false, true)]
    [InlineData("\" \"", false, true)]
    [InlineData("[]", false, true)]
    [InlineData("0", false, true)]
    public void A_switch_is_read_as_the_binder_reads_it(string written, bool accepted, bool enabled)
    {
        var status = Status($$""" "Decision": { "Enabled": {{written}} } """);

        Assert.Equal(accepted, status.Refusal is null);
        Assert.Equal(enabled, status.Enabled);
        Assert.Equal(enabled, AppSettingsDocument.Parse($$"""{ "Orkeon": { "decision": { "enabled": {{written}} } } }""").Decision.IsEnabled);
        Assert.True(Status("").Enabled);
    }

    /// <summary>
    /// The guardian's switch is bound the same way, so a guardian written without a value is off and
    /// its judge refuses nothing; the runner's strict tools are one value with a default, which a key
    /// without a value leaves on.
    /// </summary>
    [Theory]
    [InlineData(""" "Guardian": { "Llm": { "Enabled": true } } """, true, true)]
    [InlineData(""" "Guardian": { "Enabled": null, "Llm": { "Enabled": true } } """, false, true)]
    [InlineData(""" "Guardian": { "Enabled": {}, "Llm": { "Enabled": true } } """, false, true)]
    [InlineData(""" "Guardian": { "Llm": { "Enabled": true, "FailMode": "open" } } """, false, true)]
    [InlineData(""" "Guardian": { "Llm": { "Enabled": null } } """, false, true)]
    [InlineData(""" "CrewFactory": { "StrictTools": null } """, false, true)]
    [InlineData(""" "CrewFactory": { "StrictTools": {} } """, false, true)]
    [InlineData(""" "CrewFactory": { "StrictTools": "False" } """, false, false)]
    public void The_guardian_and_the_strict_tools_are_read_as_the_host_reads_them(string orkeon, bool closedJudge, bool strictTools)
    {
        var status = Status(orkeon);

        Assert.Equal(closedJudge, status.ClosedJudge);
        Assert.Equal(closedJudge, status.Refusal is not null);
        Assert.Equal(strictTools, status.StrictTools);
    }

    /// <summary>
    /// A list the file writes as an object, or with an entry that is no text, is shown as the binder
    /// reads it, and applying what is shown writes nothing: the object stays an object. Only a change
    /// rewrites the key, as a list.
    /// </summary>
    [Fact]
    public void A_ladder_and_levels_are_read_as_the_binder_reads_them_and_written_only_when_changed()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Orkeon": { "decision": { "route": { "ladder": { "1": "strong", "0": "default" } }, "Effort": { "Levels": [null, "low", 3] } } } }""");
        var before = document.ToJson();

        Assert.Equal(["default", "strong"], document.Decision.RouteLadder);
        Assert.Equal(["", "low", "3"], document.Decision.EffortLevels);

        document.Decision.RouteLadder = ["default", "strong"];
        document.Decision.EffortLevels = ["", "low", "3"];
        Assert.Equal(before, document.ToJson());

        document.Decision.RouteLadder = ["default", "stronger"];
        document.Decision.EffortLevels = [];
        Assert.Equal(
            AppSettingsDocument.Parse("""{ "Orkeon": { "decision": { "route": { "ladder": ["default", "stronger"] }, "Effort": {} } } }""").ToJson(),
            document.ToJson());
        Assert.Empty(AppSettingsDocument.CreateEmpty().Decision.RouteLadder);
    }
}
