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
    public void A_script_or_no_target_reads_as_asking_nothing()
    {
        Assert.Same(CrewDecisionUsage.None, CrewDecisionUsage.Of(null));
        Assert.Same(CrewDecisionUsage.None, CrewDecisionUsage.Of(
            new RunTarget { Kind = RunTargetKind.ScriptFile, SelectedPath = "/x/crew.ork.ts", RunPath = "/x/crew.ork.ts" }));
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
}
