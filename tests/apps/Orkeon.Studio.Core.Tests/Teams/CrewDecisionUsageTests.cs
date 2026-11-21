using Orkeon.Studio.Core.Targets;
using Orkeon.Studio.Core.Teams;

namespace Orkeon.Studio.Core.Tests.Teams;

/// <summary>
/// What a crew asks of the decision models, read with the engine's own loader: nothing, a decision task
/// (the crew runs with or without System One), or <c>system_one_decide</c> in a belt (the crew cannot run
/// without it). Every fixture is a crew the engine accepts, but the one it must refuse.
/// </summary>
public sealed class CrewDecisionUsageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orkeon-crew-decision-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private const string Agents = """
        agents:
          analyst:
            role: Analyst
            goal: read
          writer:
            role: Writer
            goal: write
        """;

    private static string Crew(string tasks, string agents = Agents) => $"""
        name: triage
        goal: triage
        process: sequential
        {agents}
        tasks:
        {tasks}
        """;

    private const string DecisionTask = """
          read:
            description: read
            expected_output: notes
            agent: analyst
          pick:
            description: is it ready
            dependencies: [read]
            decision:
              questions:
                go: { noul: the work is ready }
        """;

    [Fact]
    public void A_crew_that_states_required_is_required_whatever_it_holds()
    {
        var usage = CrewDecisionUsage.OfCrewFile(Crew(DecisionTask + "\n" + """
                  fallback: { go: "no" }
            """).Replace("process: sequential", "process: sequential\ndecision: { mode: required }", StringComparison.Ordinal));

        // Deduced, a decision task with a fallback would run either way: the crew's word wins.
        Assert.Equal(CrewDecisionMode.Required, usage.Mode);
        Assert.True(usage.Declared);
        Assert.Equal("pick", Assert.Single(usage.DecisionTasks).Task);
    }

    [Fact]
    public void A_crew_that_states_optional_names_the_relay_of_each_decision_task()
    {
        var usage = CrewDecisionUsage.OfCrewFile(Crew(DecisionTask + "\n" + """
                  without: { agent: writer, instructions: answer from the notes }
            """).Replace("process: sequential", "process: sequential\ndecision: { mode: optional }", StringComparison.Ordinal));

        Assert.Equal(CrewDecisionMode.Optional, usage.Mode);
        Assert.True(usage.Declared);
        var task = Assert.Single(usage.DecisionTasks);
        Assert.Equal("writer", task.RelayAgent);
        Assert.True(task.AnswersWithoutModel);
        Assert.Empty(usage.TasksWithoutAnswer);
    }

    [Fact]
    public void A_crew_that_states_none_asks_nothing_and_says_so()
    {
        var usage = CrewDecisionUsage.OfCrewFile(Crew("""
              read:
                description: read
                expected_output: notes
                agent: analyst
            """).Replace("process: sequential", "process: sequential\ndecision: { mode: none }", StringComparison.Ordinal));

        Assert.Equal(CrewDecisionMode.None, usage.Mode);
        Assert.True(usage.Declared);
    }

    [Fact]
    public void What_System_One_picks_makes_a_crew_that_states_no_mode_run_either_way()
    {
        var usage = CrewDecisionUsage.OfCrewFile(Crew(
            """
              read:
                description: read
                expected_output: notes
                agent: analyst
            """,
            """
            agents:
              analyst:
                role: Analyst
                goal: read
                llm:
                  thinking: { effort: auto }
                skills: { auto: true }
                compaction: { prune: true }
              writer:
                role: Writer
                goal: write
            """));

        Assert.Equal(CrewDecisionMode.Optional, usage.Mode);
        Assert.False(usage.Declared);
        Assert.Empty(usage.DecisionTasks);
        Assert.Equal(
            [CrewSystemOneUseKind.EffortAuto, CrewSystemOneUseKind.SkillsAuto, CrewSystemOneUseKind.Prune],
            usage.OtherUses.Select(use => use.Kind));
        Assert.All(usage.OtherUses, use => Assert.Equal("analyst", use.Holder));
    }

    [Fact]
    public void A_crew_without_decision_asks_nothing()
    {
        var usage = CrewDecisionUsage.OfCrewFile(Crew("""
              read:
                description: read
                expected_output: notes
                agent: analyst
            """));

        Assert.Equal(CrewDecisionMode.None, usage.Mode);
        Assert.Empty(usage.DecisionTasks);
        Assert.Empty(usage.ToolHolders);
    }

    [Fact]
    public void A_decision_task_with_a_fallback_runs_with_or_without_the_model()
    {
        var usage = CrewDecisionUsage.OfCrewFile(Crew("""
              read:
                description: read
                expected_output: notes
                agent: analyst
              pick:
                description: is it ready
                expected_output: an answer
                dependencies: [read]
                decision:
                  questions:
                    go: { noul: the work is ready }
                    route: { choice: { fix: a defect, add: a request } }
                  fallback: { go: "no", route: fix }
              write:
                description: write
                expected_output: a report
                agent: writer
                dependencies: [pick]
                when: { pick.go: ">= 0.5" }
            """));

        Assert.Equal(CrewDecisionMode.Optional, usage.Mode);
        var task = Assert.Single(usage.DecisionTasks);
        Assert.Equal("pick", task.Task);
        Assert.Equal(["go", "route"], task.Questions);
        Assert.Equal("no", task.Fallback["go"]);
        Assert.Equal("fix", task.Fallback["route"]);
        Assert.Null(task.OtherwiseTask);
        Assert.Empty(usage.ToolHolders);
    }

    [Fact]
    public void A_decision_task_without_a_fallback_names_its_otherwise_task()
    {
        var usage = CrewDecisionUsage.OfCrewFile(Crew("""
              pick:
                description: is it ready
                expected_output: an answer
                decision:
                  questions:
                    go: { noul: the work is ready }
              ship:
                description: ship
                expected_output: a release
                agent: writer
                dependencies: [pick]
                when: { pick.go: ">= 0.7" }
              hold:
                description: hold
                expected_output: a note
                agent: analyst
                dependencies: [pick]
                when: { pick: otherwise }
            """));

        Assert.Equal(CrewDecisionMode.Optional, usage.Mode);
        var task = Assert.Single(usage.DecisionTasks);
        Assert.Empty(task.Fallback);
        Assert.Equal("hold", task.OtherwiseTask);
    }

    [Fact]
    public void A_decision_task_with_neither_a_fallback_nor_an_otherwise_task_needs_the_model()
    {
        // The engine loads it, then refuses the run at its entry once the circuit is off.
        var usage = CrewDecisionUsage.OfCrewFile(Crew("""
              pick:
                description: is it ready
                expected_output: an answer
                decision:
                  questions:
                    go: { noul: the work is ready }
              ship:
                description: ship
                expected_output: a release
                agent: writer
                dependencies: [pick]
                when: { pick.go: ">= 0.7" }
            """));

        Assert.Equal(CrewDecisionMode.Required, usage.Mode);
        Assert.False(Assert.Single(usage.DecisionTasks).AnswersWithoutModel);
        Assert.Equal(["pick"], usage.TasksWithoutAnswer);
        Assert.Empty(usage.ToolHolders);
    }

    [Fact]
    public void An_agent_or_a_task_holding_the_decide_tool_cannot_run_without_the_model()
    {
        var usage = CrewDecisionUsage.OfCrewFile(Crew(
            """
              read:
                description: read
                expected_output: notes
                agent: analyst
              write:
                description: write
                expected_output: a report
                agent: writer
                tools: [system_one_decide]
            """,
            """
            agents:
              analyst:
                role: Analyst
                goal: read
                tools: [file_read, System_One_Decide]
              writer:
                role: Writer
                goal: write
            """));

        Assert.Equal(CrewDecisionMode.Required, usage.Mode);
        Assert.Equal(["analyst", "write"], usage.ToolHolders);
    }

    [Fact]
    public void A_crew_the_engine_refuses_reads_as_asking_nothing()
    {
        // A decision task that also names an agent: the validator refuses it.
        var usage = CrewDecisionUsage.OfCrewFile(Crew("""
              pick:
                description: is it ready
                expected_output: an answer
                agent: analyst
                decision:
                  questions:
                    go: { noul: the work is ready }
                  fallback: { go: "no" }
            """));

        Assert.Same(CrewDecisionUsage.None, usage);
    }

    [Fact]
    public void A_crew_file_on_disk_is_read_as_the_run_reads_it()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "crew.yaml");
        File.WriteAllText(path, Crew("""
              pick:
                description: is it ready
                expected_output: an answer
                decision:
                  questions:
                    go: { noul: the work is ready }
                  fallback: { go: "yes" }
            """));

        var usage = CrewDecisionUsage.Of(new RunTarget { Kind = RunTargetKind.YamlFile, SelectedPath = path, RunPath = path });

        Assert.Equal(CrewDecisionMode.Optional, usage.Mode);
        Assert.Equal("pick", Assert.Single(usage.DecisionTasks).Task);
    }

    [Fact]
    public void No_target_asks_nothing_and_nothing_is_known_of_a_script()
    {
        Assert.Same(CrewDecisionUsage.None, CrewDecisionUsage.Of(null));

        // Studio does not read a script: what it asks is unknown, never « nothing ».
        var script = CrewDecisionUsage.Of(
            new RunTarget { Kind = RunTargetKind.ScriptFile, SelectedPath = "/x/crew.ork.ts", RunPath = "/x/crew.ork.ts" });
        Assert.True(script.Unknown);
        Assert.Equal(CrewDecisionMode.None, script.Mode);
    }

    /// <summary>
    /// A crew the engine refuses at load (here a decision task under the mode none) is not a crew that
    /// asks nothing: nothing is known of it, and its error is the launch's to report.
    /// </summary>
    [Fact]
    public void Nothing_is_known_of_a_crew_file_the_engine_refuses()
    {
        var path = Path.Combine(_root, "refused.yaml");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, Crew(DecisionTask).Replace("process: sequential", "process: sequential\ndecision: { mode: none }", StringComparison.Ordinal));

        var usage = CrewDecisionUsage.Of(new RunTarget { Kind = RunTargetKind.YamlFile, SelectedPath = path, RunPath = path });

        Assert.True(usage.Unknown);
        Assert.Equal(CrewDecisionMode.None, usage.Mode);
    }

    [Fact]
    public void The_profiles_a_crew_names_are_listed_without_the_default_and_auto()
    {
        var usage = CrewDecisionUsage.OfCrewFile(Crew(
            """
              read:
                description: read
                expected_output: notes
                agent: analyst
                llm_override: { profile: guarded }
            """,
            """
            agents:
              analyst:
                role: Analyst
                goal: read
                llm: { profile: Strong }
              writer:
                role: Writer
                goal: write
                llm: { profile: default }
            """));

        Assert.Equal(CrewDecisionMode.None, usage.Mode);
        Assert.Equal(
            [new CrewNamedProfile("analyst", "Strong"), new CrewNamedProfile("read", "guarded")],
            usage.NamedProfiles);
    }

    /// <summary>A team folder as Studio promotes it: its crew under <c>crew/</c>, an agent and a task per file.</summary>
    private string Team(string slug, string agentTools = "", string? decisionTask = null)
    {
        var crew = Path.Combine(_root, slug, "crew");
        Directory.CreateDirectory(Path.Combine(crew, "agents"));
        Directory.CreateDirectory(Path.Combine(crew, "tasks"));
        File.WriteAllText(Path.Combine(crew, "crew.yaml"), "name: triage\ngoal: triage\nprocess: sequential\n");
        File.WriteAllText(Path.Combine(crew, "agents", "writer.yaml"), "role: Writer\ngoal: write\n" + agentTools);
        File.WriteAllText(Path.Combine(crew, "tasks", "draft.yaml"), "description: draft\nexpected_output: a draft\nagent: writer\n");
        if (decisionTask is not null)
            File.WriteAllText(Path.Combine(crew, "tasks", "pick.yaml"), decisionTask);
        return Path.Combine(_root, slug);
    }

    [Fact]
    public void A_team_folder_reads_as_its_crew_for_its_card()
    {
        var plain = Team("plain");
        var required = Team("required", agentTools: "tools: [system_one_decide]\n");
        var optional = Team("optional", decisionTask:
            "description: is it ready\nexpected_output: an answer\ndependencies: [draft]\n"
            + "decision:\n  questions:\n    go: { noul: the draft is ready }\n  fallback: { go: \"no\" }\n");

        Assert.Equal(CrewDecisionMode.None, CrewDecisionUsage.ModeOfTeam(plain));
        Assert.Equal(CrewDecisionMode.Required, CrewDecisionUsage.ModeOfTeam(required));
        Assert.Equal(CrewDecisionMode.Optional, CrewDecisionUsage.ModeOfTeam(optional));
        // The card's pre-scan reads the tool's name in any case, and a quoted or spaced key.
        Assert.Equal(CrewDecisionMode.Required, CrewDecisionUsage.ModeOfTeam(Team("cased", agentTools: "tools: [System_One_Decide]\n")));
        Assert.Equal(CrewDecisionMode.Optional, CrewDecisionUsage.ModeOfTeam(Team("spaced", decisionTask:
            "description: is it ready\nexpected_output: an answer\ndependencies: [draft]\n"
            + "decision :\n  questions:\n    go: { noul: the draft is ready }\n  fallback: { go: \"no\" }\n")));
        Assert.Equal(CrewDecisionMode.None, CrewDecisionUsage.ModeOfTeam(Path.Combine(_root, "missing")));
        Assert.Equal(CrewDecisionMode.None, CrewDecisionUsage.ModeOfTeam(null));
    }

    /// <summary>
    /// System One routes an agent's tasks: a relay no task is assigned to runs on the default profile
    /// even on <c>llm.profile: auto</c>, and the engine asks no ladder for it. An agent with a task does.
    /// </summary>
    [Fact]
    public void A_relay_without_a_task_of_its_own_is_not_a_profile_to_pick()
    {
        const string agents = """
            agents:
              analyst:
                role: Analyst
                goal: read
              judge:
                role: Judge
                goal: judge
                llm: { profile: auto }
            """;
        var relayOnly = CrewDecisionUsage.OfCrewFile(Crew(DecisionTask + "\n" + """
                  without: { agent: judge }
            """, agents).Replace("process: sequential", "process: sequential\ndecision: { mode: optional }", StringComparison.Ordinal));

        Assert.Equal(CrewDecisionMode.Optional, relayOnly.Mode);
        Assert.Equal("judge", Assert.Single(relayOnly.DecisionTasks).RelayAgent);
        Assert.DoesNotContain(relayOnly.OtherUses, use => use.Kind == CrewSystemOneUseKind.RouteAuto);

        var routed = CrewDecisionUsage.OfCrewFile(Crew(
            """
              read:
                description: read
                expected_output: notes
                agent: judge
            """,
            agents));
        Assert.Equal(new CrewSystemOneUse(CrewSystemOneUseKind.RouteAuto, "judge"), Assert.Single(routed.OtherUses));
    }

    [Fact]
    public void Thinking_switched_on_by_System_One_is_one_of_its_picks()
    {
        var usage = CrewDecisionUsage.OfCrewFile(Crew(
            """
              read:
                description: read
                expected_output: notes
                agent: analyst
            """,
            """
            agents:
              analyst:
                role: Analyst
                goal: read
                llm:
                  thinking: { enabled: auto }
            """));

        Assert.Equal(CrewDecisionMode.Optional, usage.Mode);
        Assert.Contains(usage.OtherUses, use => use is { Kind: CrewSystemOneUseKind.ThinkingAuto, Holder: "analyst" });
    }
}
