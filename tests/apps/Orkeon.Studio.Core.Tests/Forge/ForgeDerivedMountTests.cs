using Orkeon.Studio.Core.Events;
using Orkeon.Studio.Core.Forge;

namespace Orkeon.Studio.Core.Tests.Forge;

/// <summary>
/// The blueprint→mount derivation Studio shows as chips and writes into an adopted team's
/// sidecar. It is the second implementation of <c>ForgePromoter.DerivedMounts</c> — the
/// CLI's — and Studio.Core deliberately does not reference the CLI, so this suite is the pin.
/// <para>
/// The guards below were added to the CLI copy alone. A deliverable naming a reserved root or
/// a traversal segment therefore reached the sidecar through Studio and nowhere else, and the
/// team it produced was one Studio could launch and the runner refused at start — the failure
/// the guards exist to prevent, arriving through the one door that had none.
/// </para>
/// </summary>
public sealed class ForgeDerivedMountTests
{
    private static IReadOnlyList<ForgeDerivedMount> Derive(string toolsJson, string deliverablesJson)
    {
        var json = """{"v":2,"seq":1,"ts":"t","kind":"blueprint.ready","iteration":1,"blueprint":{"crew":{"name":"t","goal":"g"},"agents":[{"key":"a","role":"A","goal":"G","tools":TOOLS}],"tasks":TASKS}}"""
            .Replace("TOOLS", toolsJson, StringComparison.Ordinal)
            .Replace("TASKS", deliverablesJson, StringComparison.Ordinal);

        var model = new ForgeSessionModel();
        Assert.True(OrkeonEventParser.TryParse(json, out var parsed), json);
        model.Feed(parsed!);
        return model.DerivedMounts;
    }

    private static ForgeSessionModel Feed(string json)
    {
        var model = new ForgeSessionModel();
        Assert.True(OrkeonEventParser.TryParse(json, out var parsed), json);
        model.Feed(parsed!);
        return model;
    }

    private static string Tasks(params string[] deliverables) =>
        "[" + string.Join(",", deliverables.Select((d, i) =>
            $$"""{"key":"t{{i}}","description":"d","expectedOutput":"e","agent":"a","deliverable":"{{d}}"}""")) + "]";

    [Fact]
    public void A_deliverable_root_becomes_a_write_mount()
    {
        var mounts = Derive("[]", Tasks("/output/rapport.md", "/archive/2026.csv"));

        Assert.Equal(["/output", "/archive"], mounts.Where(m => m.IsReadWrite).Select(m => m.VirtualPath));
    }

    /// <summary>
    /// A reading tool and a deliverable that land on the SAME root yield ONE mount, and it is
    /// writable — a read-only one would leave the team unable to write the deliverable it was
    /// built to produce.
    /// <para>
    /// Neither implementation got this right and they failed differently. Studio deduped only
    /// against read-WRITE entries, so a read-only <c>/workspace</c> stood beside a read-write
    /// one: two chips for one root, and <c>SidecarMounts</c> keeps the read-only
    /// one — the chip promising a write was a lie. The CLI deduped on the name alone and
    /// dropped the write entirely, so the run reported success and produced nothing. Every
    /// case here used either a reading tool OR a deliverable under a different root; none
    /// combined them.
    /// </para>
    /// </summary>
    [Fact]
    public void A_reading_tool_and_a_deliverable_on_one_root_yield_one_writable_mount()
    {
        var mounts = Derive("""["file_read"]""", Tasks("/workspace/rapport.md"));

        var workspace = Assert.Single(mounts, m => m.VirtualPath == "/workspace");
        Assert.True(workspace.IsReadWrite, "A deliverable under /workspace makes the mount writable.");
    }

    [Fact]
    public void A_reading_tool_implies_the_workspace_read_mount()
    {
        var mounts = Derive("""["file_read"]""", Tasks("/output/r.md"));

        Assert.Contains(mounts, m => m.VirtualPath == "/workspace" && !m.IsReadWrite);
    }

    /// <summary>
    /// Orkeon #392: a task's own <c>tools</c> are its agent's for that task, and the CLI's
    /// <c>DerivedMounts</c> mounts the read root when an agent's OR a task's tools read. Studio read
    /// the agents' alone, so a plan whose task granted <c>file_read</c> got <c>/workspace</c> from
    /// the CLI and no read mount in the chips or the adopted team's sidecar.
    /// </summary>
    [Theory]
    [InlineData("file_read")]
    [InlineData("directory_read")]
    public void A_reading_tool_a_task_grants_implies_the_workspace_read_mount_read_by_its_agent(string tool)
    {
        var mounts = Derive("""["file_write"]""",
            $$"""[{"key":"t0","description":"d","expectedOutput":"e","agent":"a","tools":["{{tool}}"],"deliverable":"/output/r.md"}]""");

        var workspace = Assert.Single(mounts, m => m.VirtualPath == "/workspace");
        Assert.False(workspace.IsReadWrite);
        Assert.Equal(["A"], workspace.Agents!);
    }

    [Fact]
    public void The_tools_a_task_grants_are_the_teams_and_its_agents_as_the_trial_bench_counts_them()
    {
        var model = Feed("""{"v":2,"seq":1,"ts":"t","kind":"blueprint.ready","iteration":1,"blueprint":{"crew":{"name":"t","goal":"g"},"agents":[{"key":"w","role":"Writer","goal":"G","tools":["file_write"]},{"key":"r","role":"Reviewer","goal":"G","tools":[]}],"tasks":[{"key":"t0","description":"d","expectedOutput":"e","agent":"w","tools":["file_read","web_search","file_write"]},{"key":"t1","description":"d","expectedOutput":"e","agent":"w","tools":["web_search"]},{"key":"t2","description":"d","expectedOutput":"e","agent":"ghost","tools":["calculator"]}]}}""");

        var proposal = model.Proposal!;
        Assert.Equal(["file_write", "file_read", "web_search", "calculator"], proposal.Tools);
        var writer = proposal.Agents[0];
        Assert.Equal(["file_write"], writer.Tools);
        // A task naming no agent the plan declares may be run by any of them, as the engine picks.
        Assert.Equal(["file_read", "web_search", "calculator"], writer.TaskTools);
        Assert.Equal(["calculator"], proposal.Agents[1].TaskTools);
    }

    /// <summary>
    /// A task's tools and its reading go to every agent that may run it, as the CLI's
    /// <c>ForgeBlueprintCompiler.CandidatesOf</c> reads a plan (Orkeon #392): the named agent in a
    /// sequential crew, every worker but the manager in a hierarchical or consensual one, every
    /// agent in an autonomous one, whatever agent the task names.
    /// </summary>
    [Theory]
    [InlineData("sequential", "", "A")]
    [InlineData("hierarchical", "m", "A,B")]
    [InlineData("consensual", "m", "A,B")]
    [InlineData("hierarchical", "", "M,A,B")]
    [InlineData("autonomous", "m", "M,A,B")]
    public void A_tasks_tools_and_reading_go_to_every_agent_that_may_run_it(string process, string manager, string expected)
    {
        var json = """{"v":2,"seq":1,"ts":"t","kind":"blueprint.ready","iteration":1,"blueprint":{"crew":{"name":"t","goal":"g","process":"PROCESS"},MANAGER"agents":[{"key":"m","role":"M","goal":"G","tools":[]},{"key":"a","role":"A","goal":"G","tools":[]},{"key":"b","role":"B","goal":"G","tools":[]}],"tasks":[{"key":"t0","description":"d","expectedOutput":"e","agent":"a","tools":["file_read"],"deliverable":"/output/r.md"}]}}"""
            .Replace("PROCESS", process, StringComparison.Ordinal)
            .Replace("MANAGER", manager.Length > 0 ? $"\"manager\":\"{manager}\"," : "", StringComparison.Ordinal);

        var model = Feed(json);

        var roles = expected.Split(',');
        Assert.Equal(roles, model.Proposal!.Agents.Where(a => a.TaskTools.Contains("file_read")).Select(a => a.Role));
        Assert.Equal(roles, Assert.Single(model.DerivedMounts, m => m.VirtualPath == "/workspace").Agents);
        Assert.Equal(roles, Assert.Single(model.DerivedMounts, m => m.VirtualPath == "/output").Agents);
    }

    [Fact]
    public void An_agent_missing_its_key_that_holds_a_reading_tool_still_mounts_the_read_root()
    {
        var model = Feed("""{"v":2,"seq":1,"ts":"t","kind":"blueprint.ready","iteration":1,"blueprint":{"crew":{"name":"t","goal":"g"},"agents":[{"role":"R","tools":["file_read"]}],"tasks":[]}}""");

        var workspace = Assert.Single(model.DerivedMounts, m => m.VirtualPath == "/workspace");
        Assert.False(workspace.IsReadWrite);
        Assert.Empty(workspace.Agents!);
    }

    [Fact]
    public void A_reading_tool_granted_by_a_task_naming_no_declared_agent_still_mounts_the_read_root()
    {
        var mounts = Derive("[]",
            """[{"key":"t0","description":"d","expectedOutput":"e","agent":"ghost","tools":["file_read"]},{"key":"t1","description":"d","expectedOutput":"e","tools":["directory_read"]}]""");

        var workspace = Assert.Single(mounts, m => m.VirtualPath == "/workspace");
        Assert.False(workspace.IsReadWrite);
        // Any agent of the crew may run such a task: its only agent reads.
        Assert.Equal(["A"], workspace.Agents);
    }

    [Fact]
    public void A_task_tool_that_reads_nothing_implies_no_read_mount()
    {
        var mounts = Derive("[]",
            """[{"key":"t0","description":"d","expectedOutput":"e","agent":"a","tools":["file_write","web_search"],"deliverable":"/output/r.md"}]""");

        Assert.DoesNotContain(mounts, m => m.VirtualPath == "/workspace");
    }

    [Fact]
    public void A_confirmed_input_folder_is_read_by_the_agent_of_a_task_that_grants_a_reading_tool()
    {
        var model = new ForgeSessionModel();
        Assert.True(OrkeonEventParser.TryParse(
            """{"v":2,"seq":1,"ts":"t","kind":"brief.ready","brief":{"goal":"g","folders":[{"path":"/inpdf","role":"input"},{"path":"/outmd","role":"output"}],"acceptance":[]}}""",
            out var brief));
        model.Feed(brief!);
        Assert.True(OrkeonEventParser.TryParse(
            """{"v":2,"seq":2,"ts":"t","kind":"blueprint.ready","blueprint":{"crew":{"name":"t"},"agents":[{"key":"l","role":"Reader","tools":["file_read"]},{"key":"w","role":"Writer","tools":["file_write"]}],"tasks":[{"key":"t","description":"d","agent":"w","tools":["file_read"],"deliverable":"/outmd/x.md"}]}}""",
            out var blueprint));
        model.Feed(blueprint!);

        Assert.Equal(["/inpdf", "/outmd"], model.DerivedMounts.Select(m => m.VirtualPath));
        Assert.Equal(["Reader", "Writer"], model.DerivedMounts[0].Agents!);
        Assert.Equal(["Writer"], model.DerivedMounts[1].Agents!);
    }

    /// <summary>
    /// The runner reserves these for itself; a team carrying one is refused at start
    /// (ADR-008, decision 5). The CLI's copy skipped them; this one did not.
    /// </summary>
    [Theory]
    [InlineData("/crew")]
    [InlineData("/script")]
    [InlineData("/llm-logs")]
    [InlineData("/credentials")]
    public void A_reserved_virtual_root_is_never_derived(string reserved)
    {
        var mounts = Derive("[]", Tasks($"{reserved}/notes.md", "/output/r.md"));

        Assert.DoesNotContain(mounts, m => m.VirtualPath == reserved);
        Assert.Contains(mounts, m => m.VirtualPath == "/output");
    }

    /// <summary>
    /// The blueprint is LLM-authored: a traversal segment would put the team's own files
    /// outside the folder it was adopted into.
    /// </summary>
    [Theory]
    [InlineData("/../escaped/r.md")]
    [InlineData("/./r.md")]
    [InlineData("/out:put/r.md")]
    [InlineData("/out;put/r.md")]
    public void A_traversal_root_is_never_derived(string deliverable)
    {
        var mounts = Derive("[]", Tasks(deliverable, "/output/r.md"));

        Assert.Equal(["/output"], mounts.Where(m => m.IsReadWrite).Select(m => m.VirtualPath));
    }

    [Fact]
    public void The_same_root_is_derived_once()
    {
        var mounts = Derive("[]", Tasks("/output/a.md", "/output/b.md"));

        Assert.Single(mounts, m => m.IsReadWrite);
    }

    [Fact]
    public void A_deliverable_that_is_not_an_absolute_virtual_path_is_ignored()
    {
        var mounts = Derive("[]", Tasks("rapport.md", "/"));

        Assert.Empty(mounts);
    }
}
