using System.Text.RegularExpressions;
using Orkeon.Studio.Core.Events;
using Orkeon.Studio.Core.Forge;

namespace Orkeon.Studio.Core.Tests.Forge;

/// <summary>
/// System One in the forge: the decision mode a new session designs its crew for
/// (<c>--crew-decision</c>), the assistant's own switch (<c>--no-decision</c>), and what the session's
/// stream says of both: the mode, the assistant's circuit, its System One calls, and the passes of a
/// trial that runs with the decision models then without.
/// </summary>
public sealed partial class ForgeSystemOneTests
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
        // « No » is the engine's own default: it is never spelt on the command line.
        Assert.Equal(
            ["forge", "--events", "jsonl", "--", "une veille"],
            ForgeArgumentsBuilder.Build(new ForgeStartRequest { Need = "une veille", CrewDecision = ForgeCrewDecisions.None }));
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
        // The verdict names no pass of its own (an off-only trial): no line names one.
        Assert.All(checklist, line => Assert.Null(line.Circuit));

        // The worst finding first, the remark last: the order the findings come in decides nothing.
        var reversed = new ForgeSessionModel();
        reversed.Feed(Parse("""{"v":2,"seq":1,"ts":"t","kind":"brief.ready","brief":{"goal":"g","acceptance":[{"id":"A1","statement":"The summary names its sources","kind":"must"}]}}"""));
        reversed.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"verdict.ready","score":0.4,"passing":false,"judge":"llm","suggestions":[],"findings":[{"id":"F2","severity":"major","acceptance":"A1","statement":"no source at all","circuit":"on"},{"id":"F1","severity":"minor","acceptance":"A1","statement":"one source is vague","circuit":"off"}]}"""));

        var worstFirst = Assert.Single(reversed.BuildChecklist());
        Assert.False(worstFirst.IsRemark);
        Assert.Equal(false, worstFirst.Passed);
        Assert.Contains("no source at all", worstFirst.Detail, StringComparison.Ordinal);
    }

    private const string TwoCriteria =
        """{"v":2,"seq":1,"ts":"t","kind":"brief.ready","brief":{"goal":"g","acceptance":[{"id":"A1","statement":"The summary names its sources","kind":"must"},{"id":"A2","statement":"The summary fits one page","kind":"must"}]}}""";

    /// <summary>
    /// Both passes of a trial can leave the same mechanical finding, word for word: each line of a
    /// two-pass verdict carries the pass of its finding, a criterion the pass of its worst one.
    /// </summary>
    [Fact]
    public void A_two_pass_verdict_says_which_pass_each_finding_comes_from()
    {
        var model = new ForgeSessionModel();
        model.Feed(Parse(TwoCriteria));
        model.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"verdict.ready","score":0,"passing":false,"judge":"deterministic","suggestions":[],"findings":[{"id":"F-RUN","severity":"blocking","statement":"The crew did not complete its run.","evidence":"timeout","circuit":"on"},{"id":"F-RUN","severity":"blocking","statement":"The crew did not complete its run.","circuit":"off"},{"id":"F1","severity":"major","acceptance":"A1","statement":"no source at all","circuit":"off"}],"passes":[{"circuit":"on","passing":false,"score":0},{"circuit":"off","passing":false,"score":0}]}"""));

        var checklist = model.BuildChecklist();

        Assert.Equal(
            [("The crew did not complete its run.", "on"), ("The crew did not complete its run.", "off"), ("The summary names its sources", "off"), ("The summary fits one page", null)],
            checklist.Select(line => (line.Statement, line.Circuit)));
    }

    /// <summary>
    /// The verdict names one judge, the first pass's. A pass that failed without a finding on any
    /// criterion may have had the mechanical checks alone: a criterion nobody found fault with is then
    /// « judge for yourself », never a ✔ for a pass no judge looked at.
    /// </summary>
    [Fact]
    public void A_criterion_is_not_met_for_a_pass_no_judge_looked_at()
    {
        var unjudged = new ForgeSessionModel();
        unjudged.Feed(Parse(TwoCriteria));
        unjudged.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"verdict.ready","score":0,"passing":false,"judge":"llm","suggestions":[],"findings":[{"id":"F-RUN","severity":"blocking","statement":"The crew did not complete its run.","circuit":"off"}],"passes":[{"circuit":"on","passing":true,"score":0.9},{"circuit":"off","passing":false,"score":0}]}"""));
        Assert.All(unjudged.BuildChecklist().Skip(1), line => Assert.Null(line.Passed));

        // A pass that failed on a criterion was judged: the criterion it found no fault with is met.
        var judged = new ForgeSessionModel();
        judged.Feed(Parse(TwoCriteria));
        judged.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"verdict.ready","score":0.4,"passing":false,"judge":"llm","suggestions":[],"findings":[{"id":"F1","severity":"major","acceptance":"A1","statement":"no source at all","circuit":"off"}],"passes":[{"circuit":"on","passing":true,"score":0.9},{"circuit":"off","passing":false,"score":0.4}]}"""));
        Assert.Equal([false, true], judged.BuildChecklist().Select(line => line.Passed));

        // Two passes that pass, and a single-pass verdict, read as they always did.
        var passing = new ForgeSessionModel();
        passing.Feed(Parse(TwoCriteria));
        passing.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"verdict.ready","score":0.9,"passing":true,"judge":"llm","suggestions":[],"findings":[],"passes":[{"circuit":"on","passing":true,"score":0.9},{"circuit":"off","passing":true,"score":0.9}]}"""));
        Assert.Equal([true, true], passing.BuildChecklist().Select(line => line.Passed));
        var single = new ForgeSessionModel();
        single.Feed(Parse(TwoCriteria));
        single.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"verdict.ready","score":0.4,"passing":false,"judge":"llm","suggestions":[],"findings":[{"id":"F-RUN","severity":"blocking","statement":"The crew did not complete its run."}],"passes":[{"circuit":"off","passing":false,"score":0.4}]}"""));
        Assert.Equal([false, true, true], single.BuildChecklist().Select(line => line.Passed));
    }

    /// <summary>
    /// A trial ends with the run of the engine that started it. The bench that crashes on the first
    /// pass of two writes that pass's <c>run.finished</c>, then fails: no second pass ever ends it.
    /// </summary>
    [Fact]
    public void A_two_pass_trial_that_ends_at_its_first_pass_is_over()
    {
        const string FirstPass = """{"v":2,"seq":1,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":1,"circuit":"on"}""";
        const string FirstPassEnds = """{"v":2,"seq":2,"ts":"t","kind":"run.finished","run":1,"success":false,"circuit":"on"}""";

        var crashed = new ForgeSessionModel();
        crashed.Feed(Parse(FirstPass));
        crashed.Feed(Parse(FirstPassEnds));
        Assert.True(crashed.RunInProgress);
        crashed.Feed(Parse("""{"v":2,"seq":3,"ts":"t","kind":"error","code":"FORGE-STAGE-FAILED","message":"The test bench crashed: boom","recoverable":false}"""));
        Assert.False(crashed.RunInProgress);
        Assert.Equal((null, null, null), (crashed.TrialPass, crashed.TrialPasses, crashed.TrialCircuit));

        // A budget spent between the passes ends the session with no error at all.
        var spent = new ForgeSessionModel();
        spent.Feed(Parse(FirstPass));
        spent.Feed(Parse(FirstPassEnds));
        spent.Feed(Parse("""{"v":2,"seq":3,"ts":"t","kind":"session.finished","status":"budget","exitCode":0}"""));
        Assert.False(spent.RunInProgress);
        Assert.Null(spent.TrialPass);

        // A process stopped or dead writes nothing more: the client says it is gone.
        var stopped = new ForgeSessionModel();
        stopped.Feed(Parse(FirstPass));
        Assert.True(stopped.RunInProgress);
        stopped.AcknowledgeEngineExit();
        Assert.False(stopped.RunInProgress);
        Assert.Equal((null, null, null), (stopped.TrialPass, stopped.TrialPasses, stopped.TrialCircuit));

        // And a model nobody told starts its next run of the engine composing, not trying.
        var next = new ForgeSessionModel();
        next.Feed(Parse(FirstPass));
        next.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml","resumed":true}"""));
        Assert.False(next.RunInProgress);
        Assert.Null(next.TrialPasses);
    }

    /// <summary>
    /// A resume after a budget stop between the passes keeps the pass that ran: the engine starts the
    /// second one alone, and the trial ends with it.
    /// </summary>
    [Fact]
    public void A_resume_that_keeps_the_first_pass_runs_the_second_alone()
    {
        var model = new ForgeSessionModel();
        model.Feed(Parse("""{"v":2,"seq":1,"ts":"t","kind":"session.started","slug":"veille","dir":"/s/veille","format":"yaml","resumed":true,"crewDecision":"optional"}"""));
        model.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":2,"circuit":"off","cause":"trial"}"""));
        Assert.True(model.RunInProgress);
        Assert.Equal((2, 2, "off"), (model.TrialPass, model.TrialPasses, model.TrialCircuit));

        model.Feed(Parse("""{"v":2,"seq":3,"ts":"t","kind":"run.finished","run":1,"success":true,"circuit":"off","mailActions":[{"kind":"send","to":"a@example.org","subject":"second"}]}"""));
        Assert.False(model.RunInProgress);
        // Each simulated mail action knows the pass it was simulated in.
        Assert.Equal("off", Assert.Single(model.MailActions).Circuit);
    }

    /// <summary>
    /// The options, the event kind and the severity above are the forge's own spelling, which Studio
    /// cannot reference: each is read off the engine's source, where a rename would otherwise pass unseen.
    /// </summary>
    [Theory]
    [InlineData("ForgeCommand.cs", "\"--crew-decision\"")]
    [InlineData("ForgeCommand.cs", "\"--no-decision\"")]
    [InlineData("ForgeSystemOne.cs", "\"systemone.answered\"")]
    [InlineData("ForgeRunStages.cs", "Severity = \"info\"")]
    public void What_Studio_reads_of_the_forge_is_spelt_as_the_forge_writes_it(string file, string spelling) =>
        Assert.Contains(spelling, ForgeSource(file), StringComparison.Ordinal);

    /// <summary>
    /// The fields Studio reads off the forge's events, each looked for in the payload of the event it
    /// is read from, comments stripped: a member the engine writes in full (<c>circuit = …</c>) or by
    /// its name alone (<c>pass,</c>), never a lambda or a local of the same name elsewhere in the file.
    /// A nested row looks in the objects of the payload's <c>passes</c> list.
    /// </summary>
    [Theory]
    [InlineData("ForgeEventWriter.cs", "session.started", "crewDecision", false)]
    [InlineData("ForgeEventWriter.cs", "session.started", "forgeCircuit", false)]
    [InlineData("ForgeEventWriter.cs", "session.started", "forgeCircuitCause", false)]
    [InlineData("ForgeCost.cs", "cost.updated", "systemOneTokens", false)]
    [InlineData("ForgeRunStages.cs", "run.started", "passes", false)]
    [InlineData("ForgeRunStages.cs", "run.started", "pass", false)]
    [InlineData("ForgeRunStages.cs", "run.started", "circuit", false)]
    [InlineData("ForgeRunStages.cs", "run.finished", "circuit", false)]
    [InlineData("ForgeRunStages.cs", "run.finished", "mailActions", false)]
    [InlineData("ForgeRunStages.cs", "task.completed", "circuit", false)]
    [InlineData("ForgeRunStages.cs", "verdict.ready", "findings", false)]
    [InlineData("ForgeRunStages.cs", "verdict.ready", "judge", false)]
    [InlineData("ForgeRunStages.cs", "verdict.ready", "passes", false)]
    [InlineData("ForgeRunStages.cs", "verdict.ready", "circuit", true)]
    [InlineData("ForgeRunStages.cs", "verdict.ready", "passing", true)]
    [InlineData("ForgeRunStages.cs", "verdict.ready", "score", true)]
    public void A_field_Studio_reads_is_in_the_event_the_forge_writes(string file, string kind, string field, bool inPasses)
    {
        var payloads = PayloadsOf(ForgeSource(file), kind);
        Assert.True(payloads.Count > 0, $"{file} no longer emits \"{kind}\" with an anonymous payload: find where the forge now writes it.");

        var member = new Regex(@"[{,]\s*" + Regex.Escape(field) + @"\s*(?:=(?![=>])|,|\})", RegexOptions.CultureInvariant);
        Assert.All(payloads, payload =>
        {
            var scope = payload;
            if (inPasses)
            {
                var list = PassesMemberPattern().Match(payload);
                Assert.True(list.Success, $"{file} writes \"{kind}\" without 'passes'.");
                scope = Assert.Single(BlockFrom(payload, list.Index + list.Length));
            }

            Assert.True(member.IsMatch(scope), $"{file} writes \"{kind}\" without '{field}'{(inPasses ? " in its passes" : "")}: make ForgeSessionModel read what the forge now writes.");
        });
    }

    /// <summary>A finding travels as the record is serialized: the pass it comes from is the record's <c>circuit</c>.</summary>
    [Fact]
    public void A_finding_is_written_with_the_circuit_of_its_pass()
    {
        var source = ForgeSource("ForgeVerdict.cs");
        var from = source.IndexOf("sealed record ForgeFinding", StringComparison.Ordinal);
        Assert.True(from >= 0, "ForgeVerdict.cs no longer declares ForgeFinding.");
        var record = Assert.Single(BlockFrom(source, from, anonymous: false));

        Assert.Matches(@"\[JsonPropertyName\(""circuit""\)\]\s*public string\? Circuit", record);
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentPattern();

    [GeneratedRegex(@"//[^\r\n]*")]
    private static partial Regex LineCommentPattern();

    [GeneratedRegex(@"[{,]\s*passes\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex PassesMemberPattern();

    /// <summary>A file of the forge command, off the Orkeon checkout, comments stripped.</summary>
    private static string ForgeSource(string file)
    {
        var path = Path.Combine(
            Orkeon.Studio.Tests.Shared.OrkeonSources.Root(), "src", "scripting", "Orkeon.Scripting.Cli", "Commands", "Forge", file);
        Assert.True(File.Exists(path), $"{file} is no longer in the Orkeon checkout.");
        return LineCommentPattern().Replace(BlockCommentPattern().Replace(File.ReadAllText(path), ""), "");
    }

    /// <summary>The anonymous payload of every <c>Emit("kind", new { … })</c> of <paramref name="source"/>.</summary>
    private static List<string> PayloadsOf(string source, string kind)
    {
        var payloads = new List<string>();
        foreach (Match call in Regex.Matches(source, @"\bEmit\(\s*""" + Regex.Escape(kind) + @"""\s*,", RegexOptions.CultureInvariant))
        {
            var end = source.IndexOf(';', call.Index);
            payloads.AddRange(BlockFrom(end < 0 ? source[call.Index..] : source[call.Index..end], call.Length));
        }

        return payloads;
    }

    /// <summary>
    /// The first braced block of <paramref name="source"/> from <paramref name="from"/>, braces included:
    /// an anonymous object (<c>new { … }</c>), or any block when <paramref name="anonymous"/> is false.
    /// None when there is no such block.
    /// </summary>
    private static List<string> BlockFrom(string source, int from, bool anonymous = true)
    {
        var opening = Regex.Match(source[from..], anonymous ? @"\bnew\s*\{" : @"\{", RegexOptions.CultureInvariant);
        if (!opening.Success)
            return [];

        var open = from + opening.Index + opening.Length - 1;
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return [source[open..(index + 1)]];
        }

        return [];
    }

    /// <summary>
    /// The second pass of a trial continues it: what the first did stays, each line knows its pass, the
    /// trial is under way until the last pass ends, and the mail actions of the passes add up.
    /// </summary>
    [Fact]
    public void The_second_pass_of_a_trial_keeps_what_the_first_one_did()
    {
        var model = new ForgeSessionModel();
        model.Feed(Parse("""{"v":2,"seq":1,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":1,"circuit":"on"}"""));
        model.Feed(Parse("""{"v":2,"seq":2,"ts":"t","kind":"task.completed","taskId":"read","agentRole":"Analyst","success":false,"circuit":"on"}"""));
        model.Feed(Parse("""{"v":2,"seq":3,"ts":"t","kind":"run.finished","run":1,"circuit":"on","mailActions":[{"kind":"send","to":"a@example.org","subject":"first"}]}"""));
        Assert.True(model.RunInProgress);

        model.Feed(Parse("""{"v":2,"seq":4,"ts":"t","kind":"run.started","run":1,"passes":2,"pass":2,"circuit":"off","cause":"trial"}"""));
        model.Feed(Parse("""{"v":2,"seq":5,"ts":"t","kind":"task.completed","taskId":"read","agentRole":"Analyst","success":true,"circuit":"off"}"""));
        model.Feed(Parse("""{"v":2,"seq":6,"ts":"t","kind":"run.finished","run":1,"circuit":"off","mailActions":[{"kind":"send","to":"a@example.org","subject":"second"}]}"""));

        Assert.False(model.RunInProgress);
        Assert.Equal([("on", false), ("off", true)], model.Activity.Select(task => (task.Circuit!, task.Success)));
        Assert.Equal(2, model.MailActions.Count);

        // Another trial starts from nothing.
        model.Feed(Parse("""{"v":2,"seq":7,"ts":"t","kind":"run.started","run":2,"passes":2,"pass":1,"circuit":"on"}"""));
        Assert.Empty(model.Activity);
        Assert.Empty(model.MailActions);
    }
}
