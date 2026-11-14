using Orkeon.Domain.FileSystem;
using Orkeon.Studio.Core.Launch;
using Orkeon.Studio.Core.Teams;

namespace Orkeon.Studio.Core.Tests.Launch;

/// <summary>
/// #349: the agents the engine will refuse to start with the mounts of a run, judged by the
/// engine's own check — and no verdict where Studio cannot see what the run will mount.
/// </summary>
public sealed class FileSystemScopePreflightTests
{
    private static readonly string Physical = FileSystemMount.Quote(Path.GetTempPath().TrimEnd('/', '\\'));

    private static EffectiveMount Mount(string virtualPath, string rights, int index = 0,
        EffectiveMountSelection selection = EffectiveMountSelection.InForce) =>
        new EffectiveMount(index, $"{Physical}:{virtualPath}:{rights}", MountOrigin.Settings) { Selection = selection };

    private static CrewAgentFileSystem Agent(string name, string? root, params FileSystemScopeEntry[] entries) =>
        new(name.ToLowerInvariant(), name, entries, root);

    [Fact]
    public void A_scope_inside_the_mounts_is_not_refused()
    {
        var refusals = FileSystemScopePreflight.Check(
            [Agent("Writer", "/output/drafts", new FileSystemScopeEntry("/workspace/docs"), new FileSystemScopeEntry("/output", FileAccessRights.ReadWrite))],
            [Mount("/workspace", "ro"), Mount("/output", "rw", 1)]);

        Assert.Empty(refusals);
    }

    [Fact]
    public void An_entry_asking_more_than_its_mount_is_refused_by_the_agents_name()
    {
        var refusals = FileSystemScopePreflight.Check(
            [Agent("Writer", null, new FileSystemScopeEntry("/output", FileAccessRights.ReadWrite))],
            [Mount("/output", "ro")]);

        var refusal = Assert.Single(refusals);
        Assert.Equal("Writer", refusal.Agent);
        Assert.Contains("/output", refusal.Reason);
    }

    [Fact]
    public void An_entry_under_no_mount_is_refused()
    {
        var refusal = Assert.Single(FileSystemScopePreflight.Check(
            [Agent("Reader", null, new FileSystemScopeEntry("/elsewhere"))],
            [Mount("/workspace", "rw")]));

        Assert.Contains("/elsewhere", refusal.Reason);
    }

    [Fact]
    public void A_working_root_outside_the_mounts_or_the_scope_is_refused()
    {
        var outsideMounts = Assert.Single(FileSystemScopePreflight.Check(
            [Agent("Rooted", "/nowhere")], [Mount("/workspace", "rw")]));
        var outsideScope = Assert.Single(FileSystemScopePreflight.Check(
            [Agent("Scoped", "/output", new FileSystemScopeEntry("/workspace"))], [Mount("/workspace", "rw"), Mount("/output", "rw", 1)]));

        Assert.Contains("'/nowhere' is not under any mount", outsideMounts.Reason);
        Assert.Equal("Scoped", outsideScope.Agent);
        Assert.Contains("outside its file-system scope", outsideScope.Reason);
    }

    [Fact]
    public void A_launch_mount_that_lowers_a_root_refuses_the_scope_the_settings_granted()
    {
        var effective = MountOverrideSemantics.ComputeEffectiveMounts(
            [$"{Physical}:/output:ro"],
            [$"{Physical}:/output:rw"],
            new MountAutoInjection { Mounts = [$"{Physical}:/crew:ro"] });
        var agents = new[] { Agent("Writer", null, new FileSystemScopeEntry("/output", FileAccessRights.ReadWrite)) };

        Assert.Single(FileSystemScopePreflight.Check(agents, effective));
        Assert.Empty(FileSystemScopePreflight.Check(agents, MountOverrideSemantics.ComputeEffectiveMounts(
            [], [$"{Physical}:/output:rw"], new MountAutoInjection { Mounts = [$"{Physical}:/crew:ro"] })));
    }

    [Fact]
    public void A_mount_the_run_does_not_mount_grants_nothing()
    {
        Assert.Single(FileSystemScopePreflight.Check(
            [Agent("Reader", null, new FileSystemScopeEntry("/workspace"))],
            [Mount("/workspace", "rw", selection: EffectiveMountSelection.NotSelected)]));
    }

    [Fact]
    public void A_shape_refusal_is_reported_whatever_the_mounts()
    {
        var malformed = new CrewAgentFileSystem("analyst", "Analyst", [], null, "Agent 'analyst' filesystem: the list is empty; …");

        var refusal = Assert.Single(FileSystemScopePreflight.Check([malformed], [Mount("/workspace", "rw")]));

        Assert.Equal(("analyst", malformed.Refusal), (refusal.Agent, refusal.Reason));
    }

    [Fact]
    public void What_Studio_cannot_see_gets_no_verdict()
    {
        var reader = Agent("Reader", null, new FileSystemScopeEntry("/elsewhere"));

        // Several entries of one root and nothing picks: the crew's mounts: block may.
        Assert.Empty(FileSystemScopePreflight.Check([reader],
            [Mount("/workspace", "rw", selection: EffectiveMountSelection.Conflict)]));
        // A mount string the runner refuses with its own message.
        Assert.Empty(FileSystemScopePreflight.Check([reader],
            [new EffectiveMount(0, "not a mount", MountOrigin.CommandLine)]));
        // A root the runner may mount for the agents without the prediction listing it.
        Assert.Empty(FileSystemScopePreflight.Check(
            [Agent("Coder", "/sandbox/work", new FileSystemScopeEntry("/sandbox"))], [Mount("/workspace", "rw")]));
        Assert.Empty(FileSystemScopePreflight.Check([], [Mount("/workspace", "rw")]));
    }
}
