using Orkeon.Studio.Core.Launch;
using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Teams;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Launch;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// A team reaching outside the Settings > Allowed folders list does not start.
/// <para>
/// The settings are the list of what this machine allows. Letting such a team run meant
/// discovering the refusal from a run that failed halfway, with the reason buried in a log —
/// the screen now says which folder before anything is spawned, and names the way out.
/// </para>
/// </summary>
public sealed class LaunchAllowedFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orkeon-allowed-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string NewTeam(string slug, params string[] mounts)
    {
        var team = Path.Combine(_root, slug);
        Directory.CreateDirectory(team);
        TeamCatalog.SaveMetadata(team, new StudioTeamMetadata { Name = slug, Mounts = mounts });
        return team;
    }

    private static LaunchTabViewModel Launcher(string teamPath, params string[] declared) =>
        Launcher(teamPath, () => declared);

    private static LaunchTabViewModel Launcher(string teamPath, Func<IReadOnlyList<string>> declared) =>
        Launcher(teamPath, new FakeProcessLauncher(), declared);

    private static LaunchTabViewModel Launcher(
        string teamPath, FakeProcessLauncher process, Func<IReadOnlyList<string>> declared, FakeAppSettingsStore? settings = null)
    {
        // A promoted team resolves as a multi-file crew directory: the folder plus its agents/.
        var probe = new FakeTargetProbe()
            .WithDirectory(teamPath)
            .WithDirectory(Path.Combine(teamPath, "agents"));

        var tab = new LaunchTabViewModel(new LaunchTabDependencies
        {
            ProcessRunner = new OrkeonProcessRunner(
                process, new OrkeonBinaryLocator(FakeExecutableProbe.WithOrkeonInstalled())),
            TargetProbe = probe,
            Directories = new FakeDirectoryProbe(teamPath),
            HistoryStore = new FakeLaunchHistoryStore(),
            SettingsStore = settings ?? new FakeAppSettingsStore(),
            DeclaredMounts = declared,
        });

        tab.Target.Select(teamPath);
        return tab;
    }

    [Fact]
    public void A_team_using_only_declared_folders_launches()
    {
        var team = NewTeam("veille", "/data/docs:/docs:ro");
        var tab = Launcher(team, "/data/docs:/docs:ro");

        Assert.False(tab.IsBlockedByUndeclaredFolders);
        Assert.Empty(tab.UndeclaredTeamFolders);
        Assert.True(tab.RunCommand.CanExecute(null));
    }

    [Fact]
    public void A_team_using_a_folder_the_settings_do_not_allow_is_refused_and_told_why()
    {
        var team = NewTeam("veille", "/data/docs:/docs:ro", "/elsewhere/archives:/archives:ro");
        var tab = Launcher(team, "/data/docs:/docs:ro");

        Assert.True(tab.IsBlockedByUndeclaredFolders);
        Assert.Equal(["/archives"], tab.UndeclaredTeamFolders);
        Assert.Equal(
            [("/docs", false), ("/archives", true)],
            tab.FolderRows.Select(row => (row.VirtualPath, row.IsUndeclared)));
        Assert.False(tab.RunCommand.CanExecute(null));
        // The dry run is a run too: it spawns the same engine against the same mounts.
        Assert.False(tab.ValidateCommand.CanExecute(null));

        // The message names the folder — and, ADR-008, not the folder on this machine.
        Assert.Contains("/archives", tab.UndeclaredFoldersMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("/elsewhere", tab.UndeclaredFoldersMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void The_teams_own_output_and_input_never_refuse_it()
    {
        // They are created inside the team at adoption and are never declared; counting them
        // would make every adopted team unlaunchable.
        var team = NewTeam(
            "veille",
            $"{Path.Combine(_root, "veille", "output")}:/output:rw",
            $"{Path.Combine(_root, "veille", "input")}:/workspace:ro");
        var tab = Launcher(team);

        Assert.False(tab.IsBlockedByUndeclaredFolders);
        Assert.True(tab.RunCommand.CanExecute(null));
    }

    [Fact]
    public void Declaring_the_folder_in_the_settings_unblocks_the_very_same_launcher()
    {
        var team = NewTeam("veille", "/elsewhere/archives:/archives:ro");
        var declared = new List<string>();
        var tab = Launcher(team, () => declared);

        Assert.True(tab.IsBlockedByUndeclaredFolders);

        // The user went to the settings and declared it — the list is read live, not snapshotted.
        declared.Add("/elsewhere/archives:/archives:ro");
        tab.RefreshTeamDescription();

        Assert.False(tab.IsBlockedByUndeclaredFolders);
        Assert.True(tab.RunCommand.CanExecute(null));
    }

    [Fact]
    public void The_refusal_offers_the_settings_rather_than_leaving_the_user_to_find_them()
    {
        var team = NewTeam("veille", "/elsewhere/archives:/archives:ro");
        var tab = Launcher(team);
        var asked = 0;
        tab.OpenAllowedFoldersRequested += (_, _) => asked++;

        tab.OpenAllowedFoldersCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    /// <summary>
    /// STUDIO-15 D-05. The chooser copies the settings entry verbatim into the sidecar, so an
    /// adopted team carries the machine's own defaults: laid as <c>--mount</c> they met their
    /// twin from the settings and the run failed on "Duplicate virtual paths". An entry the
    /// settings already hold is in force without a word from Studio; only the team's own
    /// folders go on the command line.
    /// </summary>
    [Fact]
    public void The_launch_command_never_carries_a_settings_entry_twice()
    {
        var team = NewTeam(
            "veille",
            "/data/docs:/docs:ro",
            $"{Path.Combine(_root, "veille", "output")}:/output:rw");
        var tab = Launcher(team, "/data/docs:/docs:ro", "/data/archives:/archives:ro");

        Assert.Equal(["/data/docs:/docs:ro", "/data/archives:/archives:ro"], tab.Mounts.SettingsMounts);
        var arguments = tab.BuildArguments().ToList();
        var mountIndex = arguments.IndexOf("--mount");
        Assert.True(mountIndex >= 0);
        var mountValues = arguments.Skip(mountIndex + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal));
        Assert.Equal([$"{Path.Combine(_root, "veille", "output")}:/output:rw"], mountValues);
        Assert.DoesNotContain("/data/docs:/docs:ro", arguments);
    }

    /// <summary>
    /// VFS-90: a settings declaration the team names by id goes on the command line as
    /// <c>--mount-id</c> — no path, the machine's own entry as it stands today, even when the
    /// sidecar's copy is stale.
    /// </summary>
    [Fact]
    public void A_settings_declaration_the_team_names_is_selected_by_id_not_laid_as_a_path()
    {
        var id = Orkeon.Domain.Common.MountId.Create();
        var team = NewTeam("veille", $"{id}|/old/place:/docs:ro", "./output:/output:rw");
        var tab = Launcher(team, $"{id}|/data/docs:/docs:ro", "/data/archives:/archives:ro");

        Assert.False(tab.IsBlockedByUndeclaredFolders);
        Assert.False(tab.IsBlockedByUnknownMountIds);
        var arguments = tab.BuildArguments().ToList();
        var idIndex = arguments.IndexOf("--mount-id");
        Assert.True(idIndex >= 0);
        Assert.Equal(id.ToString(), arguments[idIndex + 1]);
        Assert.DoesNotContain(arguments, a => a.Contains("/old/place", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, a => a.Contains("/data/docs", StringComparison.Ordinal));
        // The declared folder sits outside the team: the flag follows (D-12), by the entry's folder.
        Assert.True(tab.Mounts.AllowExternalMounts);
        // The chips and the meta line read the declaration, not the stale copy.
        Assert.Contains($"{id}|/data/docs:/docs:ro", tab.Mounts.TeamMounts);
    }

    /// <summary>
    /// VFS-90 D-06: a team naming a declaration this machine does not have — an import, or an
    /// entry removed since — is refused before anything is spawned, with the id and the way out.
    /// </summary>
    [Fact]
    public void A_team_naming_a_declaration_missing_here_is_refused_with_the_id()
    {
        var unknown = Orkeon.Domain.Common.MountId.Create();
        var team = NewTeam("veille", $"{unknown}|/home/elsewhere/docs:/docs:ro");
        var tab = Launcher(team, "/data/docs:/docs:ro");

        Assert.True(tab.IsBlockedByUnknownMountIds);
        Assert.Equal([unknown.ToString()], tab.UnknownTeamMountIds);
        Assert.Contains(unknown.ToString(), tab.UnknownMountIdsMessage, StringComparison.Ordinal);
        Assert.False(tab.RunCommand.CanExecute(null));
        Assert.False(tab.ValidateCommand.CanExecute(null));
        Assert.DoesNotContain(tab.BuildArguments(), a => a.Contains("/home/elsewhere", StringComparison.Ordinal));
    }

    /// <summary>
    /// The effective table on a shared root: the entry the team selects reads as kept, the
    /// other as not mounted — what the runner will do, said before it does it.
    /// </summary>
    [Fact]
    public void The_effective_table_says_which_entry_of_a_shared_root_the_team_keeps()
    {
        var a = Orkeon.Domain.Common.MountId.Create();
        var b = Orkeon.Domain.Common.MountId.Create();
        var team = NewTeam("veille", $"{b}|/srv/b:/output:rw");
        var tab = Launcher(team, $"{a}|/srv/a:/output:rw", $"{b}|/srv/b:/output:rw");

        var rows = tab.Mounts.EffectiveMounts.Where(r => r.Origin == MountOrigin.Settings).ToList();
        Assert.Equal(EffectiveMountSelection.NotSelected, rows[0].Selection);
        Assert.Equal(EffectiveMountSelection.SelectedById, rows[1].Selection);
        Assert.Contains("selected by id among 2", rows[1].OriginDisplay, StringComparison.Ordinal);
        Assert.Contains("not mounted for this run", rows[0].OriginDisplay, StringComparison.Ordinal);
        Assert.Contains("--mount-id", tab.BuildArguments());
    }

    /// <summary>
    /// D-04. A team may associate a declared folder under a name the settings spend on another
    /// folder: that is a replacement the runner performs by root, and the table says so.
    /// </summary>
    [Fact]
    public void The_effective_mounts_table_names_the_settings_entry_a_team_folder_replaces()
    {
        var team = NewTeam("veille", "/data/docs:/archives:ro");
        var tab = Launcher(team, "/data/docs:/docs:ro", "/data/archives:/archives:ro");

        // Laid: the settings hold the folder under another name, so this is the team's own mount.
        Assert.Contains("/data/docs:/archives:ro", tab.BuildArguments());

        var rows = tab.Mounts.EffectiveMounts;
        var replaced = Assert.Single(rows, r => r.OverridesSettings);
        Assert.Equal("/data/docs:/archives:ro", replaced.Value);
        Assert.Equal("/data/archives:/archives:ro", replaced.ReplacedSettingsMount);
        Assert.Contains("/data/archives:/archives:ro", replaced.OriginDisplay, StringComparison.Ordinal);
        // One row per root: the declared /docs entry stays, as the settings' own.
        Assert.Single(rows, r => r.Value == "/data/docs:/docs:ro" && r.Origin == MountOrigin.Settings);
        Assert.Equal(1, tab.Mounts.OverriddenCount);
    }

    /// <summary>
    /// STUDIO-14 D-12. The engine whitelists a <c>--mount</c> base path for the file tools only
    /// under <c>--allow-external-mounts</c>, and a team reading a declared folder elsewhere on
    /// the disk was refused file by file unless the user found the expert checkbox. The flag
    /// now follows the sidecar: a team folder outside the team turns it on.
    /// </summary>
    [Fact]
    public void A_team_folder_outside_the_team_turns_allow_external_mounts_on()
    {
        // A declared folder, under the team's own name for it: laid as --mount, and outside the team.
        var team = NewTeam("veille", "/data/docs:/sources:ro");
        var tab = Launcher(team, "/data/docs:/docs:ro");

        Assert.True(tab.Mounts.AllowExternalMounts);
        var arguments = tab.BuildArguments();
        Assert.Contains("/data/docs:/sources:ro", arguments);
        Assert.Contains("--allow-external-mounts", arguments);
        // Not a refusal: the folder is declared, the flag is what lets the tools reach it.
        Assert.False(tab.IsBlockedByUndeclaredFolders);
    }

    /// <summary>
    /// The other half of D-12: a team-relative folder (<c>./output</c>) resolves under the team,
    /// which is the launch's working directory, so the tools reach it without any flag. The
    /// launch carries the resolved absolute path, never the <c>./</c> spelling — the runtime
    /// would resolve that against its own cwd and nothing else.
    /// </summary>
    [Fact]
    public void A_relative_in_team_folder_launches_absolute_and_needs_no_flag()
    {
        var team = NewTeam("veille", "./output:/output:rw", "./input:/workspace:ro");
        var tab = Launcher(team);

        Assert.False(tab.Mounts.AllowExternalMounts);
        Assert.False(tab.IsBlockedByUndeclaredFolders);

        var arguments = tab.BuildArguments().ToList();
        Assert.DoesNotContain("--allow-external-mounts", arguments);
        var mountIndex = arguments.IndexOf("--mount");
        Assert.True(mountIndex >= 0);
        var mountValues = arguments.Skip(mountIndex + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            [$"{Path.Combine(team, "output")}:/output:rw", $"{Path.Combine(team, "input")}:/workspace:ro"],
            mountValues);
        Assert.DoesNotContain(mountValues, m => m.StartsWith("./", StringComparison.Ordinal));
    }

    /// <summary>
    /// Replay is refused for the same reason a fresh run is.
    /// <para>
    /// A replay deliberately re-runs a RECORDED argument list, so that it cannot drift from what
    /// actually ran. But "Settings &gt; Allowed folders" is machine policy and it changes: without
    /// this gate, revoking a folder left every past launch of that team one click away from
    /// running against it anyway, from the one button whose whole promise is that it changes
    /// nothing. The history panel's one-click path goes through the same command, and
    /// <c>AsyncRelayCommand.ExecuteAsync</c> checks <c>CanExecute</c> before doing anything, so
    /// gating the command gates both ways in.
    /// </para>
    /// </summary>
    [Fact]
    public void A_past_launch_cannot_be_replayed_against_a_folder_since_revoked()
    {
        var team = NewTeam("veille", "/elsewhere/archives:/archives:ro");
        var declared = new List<string> { "/elsewhere/archives:/archives:ro" };
        var tab = Launcher(team, () => declared);

        Assert.False(tab.IsBlockedByUndeclaredFolders);
        Assert.True(tab.ReplayCommand.CanExecute(null));

        // The folder is taken off the machine's allow-list after that launch was recorded.
        declared.Clear();
        tab.RefreshTeamDescription();

        Assert.True(tab.IsBlockedByUndeclaredFolders);
        Assert.False(tab.ReplayCommand.CanExecute(null));
    }

    /// <summary>
    /// A per-entity crew in the team folder: a writer asking /output rw, and a task naming it. Not
    /// <paramref name="assigned"/>, the task names nobody and a second agent joins the crew: the
    /// engine's selector then picks who runs it (the only agent of a crew would always be picked).
    /// </summary>
    private static void WriteCrew(string team, string scope = "filesystem:\n  - path: /output\n    access: rw\n", bool assigned = true)
    {
        File.WriteAllText(Path.Combine(team, "crew.yaml"), "name: veille\ngoal: watch\n");
        Directory.CreateDirectory(Path.Combine(team, "agents"));
        File.WriteAllText(Path.Combine(team, "agents", "writer.yaml"), "role: Writer\n" + scope);
        if (!assigned)
            File.WriteAllText(Path.Combine(team, "agents", "spare.yaml"), "role: Spare\n");
        Directory.CreateDirectory(Path.Combine(team, "tasks"));
        File.WriteAllText(
            Path.Combine(team, "tasks", "draft.yaml"),
            "description: draft\nexpected_output: a draft\n" + (assigned ? "agent: writer\n" : ""));
    }

    private const string PinnedSettings = "/etc/veille/appsettings.json";

    private static string SettingsJson(params string[] mounts) =>
        """{"Orkeon":{"FileSystem":{"Mounts":[""" + string.Join(",", mounts.Select(mount => $"\"{mount}\"")) + "]}}}";

    /// <summary>
    /// A launcher whose run carries <c>--settings</c>: the pinned file holds <paramref name="mounts"/>,
    /// and it is the very file the CLI will read, so its mounts are known (#349). The machine's list
    /// declares them too, as the team's folders must be.
    /// </summary>
    private static LaunchTabViewModel PinnedLauncher(
        string team, FakeAppSettingsStore settings, FakeProcessLauncher process, params string[] mounts)
    {
        settings.Files[PinnedSettings] = SettingsJson(mounts);
        var tab = Launcher(team, process, () => mounts, settings);
        tab.Options.SettingsPath = PinnedSettings;
        tab.Options.SettingsMode = SettingsSelectionMode.ExplicitPath;
        return tab;
    }

    private static LaunchTabViewModel PinnedLauncher(string team, params string[] mounts) =>
        PinnedLauncher(team, new FakeAppSettingsStore(), new FakeProcessLauncher(), mounts);

    [Fact]
    public void An_agent_scope_the_teams_folders_do_not_grant_refuses_the_launch_before_the_engine_does()
    {
        // #349: the engine stops the run when it starts this agent; the screen says so first.
        var team = NewTeam("veille", "/data/out:/output:ro");
        WriteCrew(team);
        var tab = PinnedLauncher(team, "/data/out:/output:ro");

        Assert.True(tab.IsBlockedByFileSystemScope);
        Assert.Equal("Writer", Assert.Single(tab.FileSystemScopeRefusals).Agent);
        Assert.Contains("/output", tab.FileSystemScopeMessage, StringComparison.Ordinal);
        Assert.False(tab.HasFileSystemScopeWarning);
        Assert.False(tab.RunCommand.CanExecute(null));
        // A check loads the crew and starts no agent: it passes, and stays offered.
        Assert.True(tab.ValidateCommand.CanExecute(null));
    }

    [Fact]
    public void A_task_that_must_write_a_file_its_agent_cannot_write_is_a_warning_before_the_run()
    {
        // Orkeon #392: the engine runs the crew, the file is never written and the task still succeeds.
        var team = NewTeam("veille", "/data/out:/output:rw");
        WriteCrew(team);
        File.AppendAllText(Path.Combine(team, "tasks", "draft.yaml"), "deliverable:\n  path: /output/draft.md\n");
        var tab = PinnedLauncher(team, "/data/out:/output:rw");

        Assert.True(tab.HasDeliverableWriterWarning);
        Assert.Contains("'draft'", tab.DeliverableWriterWarning, StringComparison.Ordinal);
        Assert.Contains("'writer'", tab.DeliverableWriterWarning, StringComparison.Ordinal);
        Assert.Contains("/output/draft.md", tab.DeliverableWriterWarning, StringComparison.Ordinal);
        // A text file: the fix names file_write, and final_message as the other way.
        Assert.Contains("Give file_write to the agent", tab.DeliverableWriterWarning, StringComparison.Ordinal);
        Assert.Contains("source: final_message", tab.DeliverableWriterWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("engine asks", tab.DeliverableWriterWarning, StringComparison.Ordinal);
        // A warning: the run stays offered.
        Assert.True(tab.RunCommand.CanExecute(null));

        // The tool given, the warning goes when the team is read again, and the screen is told.
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        File.AppendAllText(Path.Combine(team, "agents", "writer.yaml"), "tools: [file_write]\n");
        tab.RefreshTeamDescription();
        Assert.False(tab.HasDeliverableWriterWarning);
        Assert.Equal("", tab.DeliverableWriterWarning);
        Assert.Contains(nameof(tab.HasDeliverableWriterWarning), raised);
        Assert.Contains(nameof(tab.DeliverableWriterWarning), raised);
    }

    [Fact]
    public async Task A_launch_reads_the_crew_again_for_the_deliverable_warning()
    {
        // The crew edited on disk after the team was picked: the launch judges the files as they are.
        var team = NewTeam("veille", "/data/out:/output:rw");
        WriteCrew(team);
        var tab = PinnedLauncher(team, "/data/out:/output:rw");
        Assert.False(tab.HasDeliverableWriterWarning);

        await File.AppendAllTextAsync(
            Path.Combine(team, "tasks", "draft.yaml"),
            "deliverable:\n  path: /output/draft.md\n",
            TestContext.Current.CancellationToken);
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        await tab.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(tab.HasDeliverableWriterWarning);
        Assert.Contains(nameof(tab.HasDeliverableWriterWarning), raised);
    }

    [Fact]
    public void Two_tasks_without_the_tool_are_both_named()
    {
        var team = NewTeam("veille", "/data/out:/output:rw");
        WriteCrew(team);
        File.AppendAllText(Path.Combine(team, "tasks", "draft.yaml"), "deliverable:\n  path: /output/draft.md\n");
        File.WriteAllText(
            Path.Combine(team, "tasks", "polish.yaml"),
            "description: polish\nexpected_output: a text\nagent: writer\ndeliverable:\n  path: /output/final.md\n");
        var tab = PinnedLauncher(team, "/data/out:/output:rw");

        // The heading, then one line per task.
        var lines = tab.DeliverableWriterWarning.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("Orkeon 1.2 CLI", lines[0], StringComparison.Ordinal);
        Assert.Contains("/output/draft.md", lines[1], StringComparison.Ordinal);
        Assert.Contains("/output/final.md", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_binary_file_names_the_tool_of_its_format_and_a_text_format_as_the_other_way()
    {
        // file_write writes text: it does not count for a .docx (Orkeon 1.2).
        var team = NewTeam("veille", "/data/out:/output:rw");
        WriteCrew(team);
        File.AppendAllText(Path.Combine(team, "agents", "writer.yaml"), "tools: [file_write]\n");
        File.AppendAllText(Path.Combine(team, "tasks", "draft.yaml"), "deliverable:\n  path: /output/draft.docx\n");
        File.WriteAllText(
            Path.Combine(team, "tasks", "print.yaml"),
            "description: print\nexpected_output: a pdf\nagent: writer\ndeliverable:\n  path: /output/draft.pdf\n");
        var tab = PinnedLauncher(team, "/data/out:/output:rw");

        var lines = tab.DeliverableWriterWarning.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("Give docx_writer to the agent meant to write it", lines[1], StringComparison.Ordinal);
        Assert.Contains("deliver a text format (.md, .html, .csv)", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("final_message", lines[1], StringComparison.Ordinal);
        // No built-in tool writes a PDF: the fix is a tool from elsewhere.
        Assert.Contains("an MCP server's, a plugin's", lines[2], StringComparison.Ordinal);
        Assert.Contains("deliver a text format", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_no_agent_of_a_round_robin_can_write_names_them_all()
    {
        var team = NewTeam("veille", "/data/out:/output:rw");
        WriteCrew(team, assigned: false);
        File.AppendAllText(Path.Combine(team, "tasks", "draft.yaml"), "deliverable:\n  path: /output/draft.md\n");
        var tab = PinnedLauncher(team, "/data/out:/output:rw");

        Assert.Contains("none of the agents that may run it (", tab.DeliverableWriterWarning, StringComparison.Ordinal);
        Assert.Contains("spare", tab.DeliverableWriterWarning, StringComparison.Ordinal);
        Assert.Contains("writer", tab.DeliverableWriterWarning, StringComparison.Ordinal);

        // One agent able to write is enough: the round-robin may pick it.
        File.AppendAllText(Path.Combine(team, "agents", "spare.yaml"), "tools: [file_write]\n");
        tab.RefreshTeamDescription();
        Assert.False(tab.HasDeliverableWriterWarning);
    }

    [Fact]
    public void A_decision_task_with_a_deliverable_is_said_to_write_no_file()
    {
        var team = NewTeam("veille", "/data/out:/output:rw");
        WriteCrew(team);
        File.AppendAllText(Path.Combine(team, "agents", "writer.yaml"), "tools: [file_write]\n");
        File.WriteAllText(
            Path.Combine(team, "tasks", "pick.yaml"),
            "description: pick\nexpected_output: a choice\ndecision: { questions: {} }\ndeliverable:\n  path: /output/choice.md\n");
        var tab = PinnedLauncher(team, "/data/out:/output:rw");

        var line = Assert.Single(tab.DeliverableWriterWarning.Split('\n').Skip(1));
        Assert.Contains("'pick' is a decision task", line, StringComparison.Ordinal);
        Assert.Contains("/output/choice.md", line, StringComparison.Ordinal);
        Assert.Contains("a decision writes no file", line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_the_run_may_not_start_is_a_warning_not_a_refusal()
    {
        var team = NewTeam("veille", "/data/out:/output:ro");
        WriteCrew(team, assigned: false);
        var tab = PinnedLauncher(team, "/data/out:/output:ro");

        Assert.False(tab.IsBlockedByFileSystemScope);
        Assert.True(tab.HasFileSystemScopeWarning);
        Assert.Contains("/output", tab.FileSystemScopeWarning, StringComparison.Ordinal);
        Assert.True(tab.RunCommand.CanExecute(null));
    }

    [Fact]
    public void The_only_agent_of_a_crew_is_started_by_a_task_that_names_nobody()
    {
        // The round-robin over one agent always picks it: the refusal is as sure as a named agent's.
        var team = NewTeam("veille", "/data/out:/output:ro");
        WriteCrew(team, assigned: false);
        File.Delete(Path.Combine(team, "agents", "spare.yaml"));
        var tab = PinnedLauncher(team, "/data/out:/output:ro");

        Assert.True(tab.IsBlockedByFileSystemScope);
        Assert.False(tab.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_crew_edited_on_disk_after_the_tab_loaded_is_read_again_at_launch()
    {
        // The writer only reads /output when the tab loads: nothing is refused.
        var team = NewTeam("veille", "/data/out:/output:ro");
        WriteCrew(team, scope: "filesystem:\n  - /output\n");
        var process = new FakeProcessLauncher();
        var tab = PinnedLauncher(team, new FakeAppSettingsStore(), process, "/data/out:/output:ro");
        Assert.False(tab.IsBlockedByFileSystemScope);
        Assert.True(tab.RunCommand.CanExecute(null));

        // Edited on disk, the tab still open: the writer now asks rw where the folder is read-only.
        WriteCrew(team);

        Assert.Null(await tab.RunAsync(TestContext.Current.CancellationToken));
        Assert.Empty(process.Requests);
        Assert.True(tab.IsBlockedByFileSystemScope);
        Assert.Contains("/output", tab.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(tab.FileSystemScopeMessage, tab.StatusMessage);
        Assert.False(tab.RunCommand.CanExecute(null));

        // The check that comes first does not run either: the run it guards is refused.
        tab.Options.ValidateFirst = true;
        Assert.Null(await tab.RunAsync(TestContext.Current.CancellationToken));
        Assert.Empty(process.Requests);

        // A check alone starts no agent: it launches, until the declaration is malformed.
        Assert.NotNull(await tab.ValidateAsync(TestContext.Current.CancellationToken));
        Assert.Single(process.Requests);

        WriteCrew(team, scope: "filesystem: []\n");
        Assert.Null(await tab.ValidateAsync(TestContext.Current.CancellationToken));
        Assert.Single(process.Requests);
        // The engine's own reason, without the sentence of a blocked run.
        Assert.StartsWith("Agent 'writer' filesystem: ", tab.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_check_names_the_malformed_declaration_alone()
    {
        // Two agents: one malformed, one whose scope the mounts refuse at start. A check fails on the
        // first alone, and its line does not list the second, which a check passes.
        var team = NewTeam("veille", "/data/out:/output:ro");
        WriteCrew(team);
        await File.WriteAllTextAsync(
            Path.Combine(team, "agents", "broken.yaml"), "role: Broken\nfilesystem: []\n", TestContext.Current.CancellationToken);
        var process = new FakeProcessLauncher();
        var tab = PinnedLauncher(team, new FakeAppSettingsStore(), process, "/data/out:/output:ro");
        Assert.Equal(2, tab.FileSystemScopeRefusals.Count);

        Assert.Null(await tab.ValidateAsync(TestContext.Current.CancellationToken));

        Assert.Empty(process.Requests);
        Assert.StartsWith("Agent 'broken' filesystem: ", tab.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("/output", tab.StatusMessage, StringComparison.Ordinal);
        // The alert of the blocked run still lists both.
        Assert.Contains("/output", tab.FileSystemScopeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_malformed_scope_refuses_the_check_too()
    {
        // Refused as the crew loads, whatever the mounts: it blocks in automatic mode too.
        var team = NewTeam("veille", "/data/out:/output:rw");
        WriteCrew(team, scope: "filesystem: []\n");
        var tab = Launcher(team, "/data/out:/output:rw");

        Assert.True(tab.IsBlockedByFileSystemScope);
        Assert.Contains("Agent 'writer' filesystem", tab.FileSystemScopeMessage, StringComparison.Ordinal);
        Assert.False(tab.RunCommand.CanExecute(null));
        Assert.False(tab.ValidateCommand.CanExecute(null));
    }

    [Fact]
    public void Settings_Studio_cannot_read_turn_the_refusal_into_a_warning()
    {
        var team = NewTeam("veille", "/data/out:/output:ro");
        WriteCrew(team);
        var tab = PinnedLauncher(team, "/data/out:/output:ro");
        Assert.True(tab.IsBlockedByFileSystemScope);

        // A pinned file this machine does not have: the CLI may mount what the panel cannot show.
        tab.Options.SettingsPath = Path.Combine(team, "missing-appsettings.json");

        Assert.False(tab.IsBlockedByFileSystemScope);
        Assert.True(tab.HasFileSystemScopeWarning);
        Assert.True(tab.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Settings_the_cli_resolves_itself_turn_the_refusal_into_a_warning()
    {
        // No --settings: the CLI takes an appsettings.json beside the crew, or one above it, before
        // the per-user file the panel lists. That file may mount /output rw: the run is not blocked.
        var team = NewTeam("veille", "/data/out:/output:ro");
        WriteCrew(team);
        var process = new FakeProcessLauncher();
        var tab = Launcher(team, process, () => ["/data/out:/output:ro"]);

        Assert.Null(tab.ResolvedSettingsPath);
        Assert.False(tab.IsBlockedByFileSystemScope);
        Assert.True(tab.HasFileSystemScopeWarning);
        Assert.Contains("/output", tab.FileSystemScopeWarning, StringComparison.Ordinal);
        Assert.True(tab.RunCommand.CanExecute(null));

        Assert.NotNull(await tab.RunAsync(TestContext.Current.CancellationToken));
        var request = Assert.Single(process.Requests);
        Assert.DoesNotContain("--settings", request.Arguments);
    }

    [Fact]
    public async Task A_mount_added_to_the_settings_file_offers_the_run_again_at_the_next_check()
    {
        var team = NewTeam("veille", "/data/out:/output:ro");
        WriteCrew(team);
        var settings = new FakeAppSettingsStore();
        var process = new FakeProcessLauncher();
        var tab = PinnedLauncher(team, settings, process, "/data/out:/output:ro");
        Assert.False(tab.RunCommand.CanExecute(null));

        // Edited in an editor, the tab still open: the file now grants what the writer asks.
        settings.Files[PinnedSettings] = SettingsJson("/data/out:/output:rw");
        Assert.False(tab.RunCommand.CanExecute(null));

        // « Validate » stays offered while a scope blocks « Run », and reads the file again.
        Assert.True(tab.ValidateCommand.CanExecute(null));
        Assert.NotNull(await tab.ValidateAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["/data/out:/output:rw"], tab.Mounts.SettingsMounts);
        Assert.False(tab.IsBlockedByFileSystemScope);
        Assert.True(tab.RunCommand.CanExecute(null));
    }

    [Fact]
    public void A_mount_added_to_the_settings_file_is_read_when_the_target_is_read_again()
    {
        var team = NewTeam("veille", "/data/out:/output:ro");
        WriteCrew(team);
        var settings = new FakeAppSettingsStore();
        var tab = PinnedLauncher(team, settings, new FakeProcessLauncher(), "/data/out:/output:ro");
        Assert.True(tab.IsBlockedByFileSystemScope);

        // The target detected again (its « detect » button, the team picked again).
        settings.Files[PinnedSettings] = SettingsJson("/data/out:/output:rw");
        tab.Target.DetectCommand.Execute(null);
        Assert.False(tab.IsBlockedByFileSystemScope);
        Assert.True(tab.RunCommand.CanExecute(null));

        // The shell's refresh of the team (a save of the settings, a change of its folders).
        settings.Files[PinnedSettings] = SettingsJson("/data/out:/output:ro");
        tab.RefreshTeamDescription();
        Assert.True(tab.IsBlockedByFileSystemScope);
    }

    [Fact]
    public void An_agent_scope_inside_the_teams_folders_launches()
    {
        var team = NewTeam("veille", "/data/out:/output:rw");
        WriteCrew(team);
        var tab = PinnedLauncher(team, "/data/out:/output:rw");

        Assert.False(tab.IsBlockedByFileSystemScope);
        Assert.False(tab.HasFileSystemScopeWarning);
        Assert.Equal("", tab.FileSystemScopeMessage);
        Assert.True(tab.RunCommand.CanExecute(null));
    }

}
