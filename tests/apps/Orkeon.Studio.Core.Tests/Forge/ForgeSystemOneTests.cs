using Orkeon.Studio.Core.Events;
using Orkeon.Studio.Core.Forge;

namespace Orkeon.Studio.Core.Tests.Forge;

/// <summary>
/// System One in the forge: the decision mode a new session designs its crew for
/// (<c>--crew-decision</c>), the assistant's own switch (<c>--no-decision</c>), and what the session's
/// stream says of both: the mode, the assistant's circuit, its System One calls, and the passes of a
/// trial that runs with the decision models then without.
/// </summary>
public sealed class ForgeSystemOneTests
{
    private static OrkeonEvent Parse(string line)
    {
        Assert.True(OrkeonEventParser.TryParse(line, out var orkeonEvent), line);
        return orkeonEvent!;
    }

    [Fact]
    public void The_crews_mode_is_passed_to_a_new_session_only()
    {
        Assert.Equal(
            ["forge", "--events", "jsonl", "--crew-decision", "optional", "--", "une veille"],
            ForgeArgumentsBuilder.Build(new ForgeStartRequest { Need = "une veille", CrewDecision = "Optional" }));

        // A resume keeps the mode the session was created with: the engine refuses another.
        Assert.Equal(
            ["forge", "resume", "veille", "--events", "jsonl"],
            ForgeArgumentsBuilder.Build(new ForgeStartRequest { ResumeSlug = "veille", CrewDecision = "required" }));

        // A reopen shapes no cycle, and a mode the engine does not know is not passed.
        Assert.Equal(
            ["forge", "reopen", "/teams/veille", "--events", "jsonl"],
            ForgeArgumentsBuilder.Build(new ForgeStartRequest { ReopenDirectory = "/teams/veille", CrewDecision = "required", NoDecision = true }));
        Assert.Equal(
            ["forge", "--events", "jsonl", "--", "une veille"],
            ForgeArgumentsBuilder.Build(new ForgeStartRequest { Need = "une veille", CrewDecision = "sometimes" }));
    }

    [Fact]
    public void The_assistants_switch_goes_with_every_run_but_an_adoption()
    {
        Assert.Equal(
            ["forge", "--events", "jsonl", "--dry", "--no-decision", "--", "une veille"],
            ForgeArgumentsBuilder.Build(new ForgeStartRequest { Need = "une veille", Dry = true, NoDecision = true }));
        Assert.Equal(
            ["forge", "resume", "veille", "--events", "jsonl", "--no-decision"],
            ForgeArgumentsBuilder.Build(new ForgeStartRequest { ResumeSlug = "veille", NoDecision = true }));

        // An adoption asks System One nothing, and the engine refuses the switch beside it.
        Assert.Equal(
            ["forge", "resume", "veille", "--events", "jsonl", "--adopt"],
            ForgeArgumentsBuilder.Build(new ForgeStartRequest { ResumeSlug = "veille", Adopt = true, NoDecision = true }));
    }

    [Fact]
    public void Nothing_is_passed_when_nothing_is_asked()
    {
        // The command line of an engine that predates both options.
        Assert.Equal(
            ["forge", "--events", "jsonl", "--", "une veille"],
            ForgeArgumentsBuilder.Build(new ForgeStartRequest { Need = "une veille" }));
        Assert.Equal(["none", "optional", "required"], ForgeCrewDecisions.All);
    }

    [Fact]
    public void The_session_states_the_crews_mode_and_the_assistants_circuit()
    {
        var model = new ForgeSessionModel();
        Assert.Null(model.CrewDecision);

        model.Feed(Parse("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml","crewDecision":"optional","forgeCircuit":"off","forgeCircuitCause":"switch"}"""));

        Assert.Equal(ForgeCrewDecisions.Optional, model.CrewDecision);
        Assert.Equal("off", model.ForgeCircuit);
        Assert.Equal("switch", model.ForgeCircuitCause);

        // An engine that predates the modes says none of it.
        var older = new ForgeSessionModel();
        older.Feed(Parse("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml"}"""));
        Assert.Null(older.CrewDecision);
        Assert.Null(older.ForgeCircuit);
    }

    [Fact]
    public void The_assistants_System_One_calls_are_counted_with_their_tokens()
    {
        var model = new ForgeSessionModel();
        model.Feed(Parse("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","forgeCircuit":"on"}"""));
        model.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"systemone.answered","stage":"compose","use":"process","circuit":"on","questions":1,"answers":1,"model":"d1","inputTokens":120,"outputTokens":4,"durationMs":80}"""));
        model.Feed(Parse("""{"v":2,"seq":3,"ts":"t","kind":"systemone.answered","stage":"compose","use":"coverage","circuit":"on","questions":3,"answers":3}"""));
        model.Feed(Parse("""{"v":2,"seq":4,"ts":"t","kind":"cost.updated","tokens":5000,"systemOneTokens":248}"""));

        Assert.Equal(2, model.SystemOneAnswers);
        Assert.Equal(248, model.SystemOneTokens);
        Assert.Equal(5000, model.TokensSpent);

        // A later reading that names no System One figure keeps the last one.
        model.Feed(Parse("""{"v":2,"seq":5,"ts":"t","kind":"cost.updated","tokens":6000}"""));
        Assert.Equal(248, model.SystemOneTokens);
    }

    [Fact]
    public void A_trial_of_a_crew_that_runs_either_way_names_its_passes_and_judges_each()
    {
        var model = new ForgeSessionModel();
        model.Feed(Parse("""{"v":2,"seq":1,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":1,"circuit":"on"}"""));
        Assert.Equal((1, 2, "on"), (model.TrialPass, model.TrialPasses, model.TrialCircuit));

        model.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":2,"circuit":"off","cause":"trial"}"""));
        Assert.Equal((2, 2, "off"), (model.TrialPass, model.TrialPasses, model.TrialCircuit));

        model.Feed(Parse("""{"v":2,"seq":3,"ts":"t","kind":"verdict.ready","score":0.7,"passing":false,"judge":"llm","findings":[],"suggestions":[],"passes":[{"circuit":"on","passing":true,"score":0.9,"tokens":1200},{"circuit":"off","passing":false,"score":0.5}]}"""));

        Assert.NotNull(model.Verdict);
        Assert.Equal(
            [new ForgeVerdictPassView("on", true, 0.9), new ForgeVerdictPassView("off", false, 0.5)],
            model.Verdict.Passes);

        // A single-pass trial of an older engine carries no pass.
        var older = new ForgeSessionModel();
        older.Feed(Parse("""{"v":2,"seq":1,"ts":"t","kind":"run.started","run":1}"""));
        older.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"verdict.ready","score":1,"passing":true,"judge":"llm","findings":[],"suggestions":[]}"""));
        Assert.Null(older.TrialPass);
        Assert.Empty(older.Verdict!.Passes);
    }

    /// <summary>
    /// A crew that runs either way is judged once per pass. A criterion is as good as its worst finding,
    /// whichever pass it comes from, and an <c>info</c> finding (a pass that was not run) fails nothing.
    /// </summary>
    [Fact]
    public void A_two_pass_verdict_shows_each_criterion_by_its_worst_finding_and_an_info_finding_as_a_remark()
    {
        var model = new ForgeSessionModel();
        model.Feed(Parse("""{"v":2,"seq":1,"ts":"t","kind":"brief.ready","brief":{"goal":"g","acceptance":[{"id":"A1","statement":"The summary names its sources","kind":"must"}]}}"""));
        model.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"verdict.ready","score":0.4,"passing":false,"judge":"llm","suggestions":[],"findings":[{"id":"F1","severity":"minor","acceptance":"A1","statement":"one source is vague","circuit":"on"},{"id":"F2","severity":"major","acceptance":"A1","statement":"no source at all","circuit":"off"},{"id":"F-PASS-ON-SKIPPED","severity":"info","statement":"The trial ran without System One only","evidence":"no-provider","circuit":"off"}]}"""));

        Assert.Equal(["on", "off", "off"], model.Verdict!.Findings.Select(finding => finding.Circuit));
        var checklist = model.BuildChecklist();

        var skipped = Assert.Single(checklist, line => line.Statement.StartsWith("The trial ran without", StringComparison.Ordinal));
        Assert.True(skipped.IsRemark);
        var criterion = Assert.Single(checklist, line => line.Statement == "The summary names its sources");
        Assert.False(criterion.IsRemark);
        Assert.Equal(false, criterion.Passed);
        Assert.Contains("no source at all", criterion.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The options, the event and the fields above are the forge's own spelling, which Studio cannot
    /// reference: each is read off the engine's source, where a rename would otherwise pass unseen.
    /// </summary>
    [Theory]
    [InlineData("ForgeCommand.cs", "\"--crew-decision\"")]
    [InlineData("ForgeCommand.cs", "\"--no-decision\"")]
    [InlineData("ForgeSystemOne.cs", "\"systemone.answered\"")]
    [InlineData("ForgeEventWriter.cs", "crewDecision =")]
    [InlineData("ForgeEventWriter.cs", "forgeCircuit =")]
    [InlineData("ForgeEventWriter.cs", "forgeCircuitCause =")]
    [InlineData("ForgeCost.cs", "systemOneTokens =")]
    [InlineData("ForgeRunStages.cs", "passes =")]
    [InlineData("ForgeRunStages.cs", "pass =")]
    [InlineData("ForgeRunStages.cs", "circuit =")]
    [InlineData("ForgeRunStages.cs", "passing =")]
    [InlineData("ForgeRunStages.cs", "Severity = \"info\"")]
    public void What_Studio_reads_of_the_forge_is_spelt_as_the_forge_writes_it(string file, string spelling)
    {
        var source = File.ReadAllText(Path.Combine(
            Orkeon.Studio.Tests.Shared.OrkeonSources.Root(), "src", "scripting", "Orkeon.Scripting.Cli", "Commands", "Forge", file));

        Assert.Contains(spelling, source, StringComparison.Ordinal);
    }
}
