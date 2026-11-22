using System.Text.Json;
using Orkeon.Domain.FileSystem;
using Orkeon.Studio.Core.FileSystem;
using Orkeon.Studio.Wpf.ViewModels.Mounts;
using Orkeon.Studio.Wpf.ViewModels.Teams;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The two v2 modals (RC2-FEAT-05, F-01/F-03): the team-mounts rows and their save; the
/// agent editor's blueprint mutations — all pure ViewModel, no window. The folder picker
/// left with STUDIO-19: the OS folder dialog replaced it.
/// </summary>
public sealed class ModalDialogsTests
{
    // ── Team mounts ──

    [Fact]
    public void The_team_mounts_save_keeps_checked_rows_and_added_folders_only()
    {
        (string Directory, IReadOnlyList<string> Mounts)? saved = null;
        var dialog = new TeamMountsDialogViewModel(saveMounts: (dir, mounts) => saved = (dir, mounts));
        var refreshed = false;

        dialog.Open("/teams/veille", "Veille", ["/a:/docs:ro", "/b:/output:rw"], onSaved: _ => refreshed = true);
        Assert.Equal(2, dialog.Rows.Count);
        Assert.All(dialog.Rows, row => Assert.True(row.IsChecked));

        dialog.Rows[0].IsChecked = false;
        dialog.AddMount(MountDefinition.Parse("/c:/archives:ro"));
        dialog.SaveCommand.Execute(null);

        Assert.False(dialog.IsOpen);
        Assert.True(refreshed);
        Assert.Equal(["/b:/output:rw", "/c:/archives:ro"], saved!.Value.Mounts);
        Assert.Equal("/teams/veille", saved.Value.Directory);
    }

    /// <summary>Same fix as the chooser's: a one-word pill, the full label as tooltip, the id tail on the row.</summary>
    [Fact]
    public void A_team_mount_row_carries_a_one_word_rights_pill_and_the_id_tail()
    {
        var id = Orkeon.Domain.Common.MountId.Create();
        var dialog = new TeamMountsDialogViewModel(saveMounts: (_, _) => { }, declaredMounts: () => [$"{id}|/data/out:/output:rw"]);
        dialog.Open("/teams/veille", "Veille", ["./input:/workspace:ro", $"{id}|/data/out:/output:rw"]);

        Assert.Equal("read", dialog.Rows[0].RightsBadge);
        Assert.Equal("", dialog.Rows[0].ShortId);
        Assert.Equal("write", dialog.Rows[1].RightsBadge);
        Assert.Equal("Read / write (create and delete allowed)", dialog.Rows[1].RightsLabel);
        Assert.Equal(id.ToString()[^6..], dialog.Rows[1].ShortId);
        Assert.Equal("/data/out", dialog.Rows[1].PhysicalPath);
    }

    [Fact]
    public void The_summary_speaks_plainly_for_none_and_for_some()
    {
        var dialog = new TeamMountsDialogViewModel(saveMounts: (_, _) => { });
        dialog.Open("/teams/x", "X", ["/a:/docs:ro"]);

        Assert.Contains("/docs", dialog.Summary, StringComparison.Ordinal);
        dialog.Rows[0].IsChecked = false;
        // Default strings are the English registry.
        Assert.Contains("neither read nor write", dialog.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Red means one thing: this folder is outside what the machine allows. It used to also
    /// mean «the team made this folder itself», which the launcher explicitly refuses to
    /// warn about — so a team's own /output read red here and green on Run, about the
    /// very same folder.
    /// </summary>
    [Fact]
    public void A_folder_outside_the_allowed_ones_reads_red_and_the_teams_own_does_not()
    {
        var dialog = new TeamMountsDialogViewModel(
            saveMounts: (_, _) => { },
            declaredMounts: () => ["/data/docs:/docs:ro"]);

        dialog.Open("/teams/veille", "Veille",
        [
            "/data/docs:/docs:ro",                  // declared in the settings
            "/teams/veille/output:/output:rw",      // the team's own, bound at adoption
            "/elsewhere:/archives:ro",              // genuinely outside the allow-list
        ]);

        Assert.False(dialog.Rows.Single(r => r.VirtualPath == "/docs").IsUndeclared);
        Assert.False(dialog.Rows.Single(r => r.VirtualPath == "/output").IsUndeclared);
        Assert.True(dialog.Rows.Single(r => r.VirtualPath == "/archives").IsUndeclared);

        dialog.Rows.Single(r => r.VirtualPath == "/archives").IsChecked = false;
        Assert.Equal(["/data/docs:/docs:ro", "/teams/veille/output:/output:rw"], dialog.CheckedMounts);
    }

    // ── Agent editor ──

    private const string Blueprint =
        """{"crew":{"name":"veille"},"agents":[{"key":"collecteur","role":"Chercheur","goal":"Trouver","tools":["web_scrape","file_read"]},{"key":"redacteur","role":"Rédacteur","goal":"Écrire","tools":["file_write"]}],"tasks":[{"key":"t1","description":"d","agent":"collecteur"}]}""";

    private const string TaskBlueprint =
        """{"agents":[{"key":"lecteur","role":"Lecteur"},{"key":"redacteur","role":"Rédacteur"}],"tasks":[{"key":"lire","description":"Lire","expectedOutput":"x","agent":"lecteur"},{"key":"juger","description":"Juger","dependencies":["lire"],"decision":{"questions":{"ok":{"options":["oui","non"]}}}},{"key":"ecrire","description":"Écrire","expectedOutput":"y","agent":"redacteur","dependencies":["lire"],"tools":["pdf_reader"],"when":{"juger.ok":["oui"]}}]}""";

    [Fact]
    public void Editing_a_task_rewrites_its_fields_and_keeps_its_condition()
    {
        var editor = new TaskEditorViewModel();
        string? sent = null;
        editor.Open(TaskBlueprint, "ecrire", ["/a:/output:rw"], json => sent = json);

        Assert.False(editor.IsNew);
        Assert.Equal("Écrire", editor.Description);
        Assert.Equal("redacteur", editor.Agent!.Key);
        Assert.Equal(["lire", "juger"], editor.Dependencies.Select(option => option.Key));
        Assert.True(editor.Dependencies[0].IsChecked);
        Assert.True(editor.Tools.Chips.Single(chip => chip.Tool == "pdf_reader").IsSelected);
        Assert.True(editor.CanRemove);

        editor.Agent = editor.Agents[0];
        editor.Deliverable = "/output/note.md";
        editor.Dependencies[1].IsChecked = true;
        editor.Tools.Chips.Single(chip => chip.Tool == "pdf_reader").ToggleCommand.Execute(null);
        editor.SaveCommand.Execute(null);

        Assert.False(editor.IsOpen);
        using var document = JsonDocument.Parse(sent!);
        var task = document.RootElement.GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("key").GetString() == "ecrire");
        Assert.Equal("lecteur", task.GetProperty("agent").GetString());
        Assert.Equal("/output/note.md", task.GetProperty("deliverable").GetString());
        Assert.Equal(["lire", "juger"], task.GetProperty("dependencies").EnumerateArray().Select(d => d.GetString()));
        Assert.False(task.TryGetProperty("tools", out _));
        Assert.Equal("oui", task.GetProperty("when").GetProperty("juger.ok")[0].GetString());
    }

    [Fact]
    public void A_new_task_needs_what_to_do_and_what_is_expected_and_is_appended()
    {
        var editor = new TaskEditorViewModel();
        string? sent = null;
        editor.Open(TaskBlueprint, null, [], json => sent = json);

        Assert.True(editor.IsNew);
        Assert.False(editor.CanRemove);
        Assert.Equal("lecteur", editor.Agent!.Key);
        Assert.All(editor.Dependencies, option => Assert.False(option.IsChecked));
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.Description = "Relire le tout";
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.ExpectedOutput = "Des remarques";
        Assert.True(editor.SaveCommand.CanExecute(null));
        editor.SaveCommand.Execute(null);

        using var document = JsonDocument.Parse(sent!);
        var last = document.RootElement.GetProperty("tasks").EnumerateArray().Last();
        Assert.Equal("Relire le tout", last.GetProperty("description").GetString());
        Assert.Equal("lecteur", last.GetProperty("agent").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("tasks").GetArrayLength());
    }

    [Fact]
    public void A_task_a_condition_reads_cannot_be_removed_and_says_which()
    {
        var editor = new TaskEditorViewModel();
        editor.Open(TaskBlueprint, "juger", [], _ => Assert.Fail("nothing may be sent"));

        Assert.False(editor.CanRemove);
        Assert.False(editor.RemoveCommand.CanExecute(null));
        Assert.Contains("Écrire", editor.RemoveTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_a_task_drops_it_from_the_dependencies_of_the_others()
    {
        var editor = new TaskEditorViewModel();
        string? sent = null;
        editor.Open(TaskBlueprint, "lire", [], json => sent = json);
        editor.RemoveCommand.Execute(null);

        using var document = JsonDocument.Parse(sent!);
        var tasks = document.RootElement.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(["juger", "ecrire"], tasks.Select(t => t.GetProperty("key").GetString()));
        Assert.All(tasks, task => Assert.False(task.TryGetProperty("dependencies", out _)));
    }

    [Fact]
    public void Editing_an_agent_rewrites_role_goal_and_tools_in_place()
    {
        var editor = new AgentEditorViewModel();
        string? sent = null;
        editor.Open(Blueprint, "redacteur", ["/a:/docs:ro"], json => sent = json);

        Assert.False(editor.IsNew);
        Assert.Equal("Rédacteur", editor.Name);
        Assert.Equal("Écrire", editor.WhatItDoes);
        // The palette is the union of the blueprint's tools; the agent's own are selected.
        Assert.Equal(["file_read", "file_write", "web_scrape"], editor.Chips.Select(c => c.Tool));
        Assert.True(editor.Chips.Single(c => c.Tool == "file_write").IsSelected);
        Assert.False(editor.Chips.Single(c => c.Tool == "web_scrape").IsSelected);

        editor.Name = "Relecteur";
        editor.WhatItDoes = "Relire et vérifier";
        editor.Chips.Single(c => c.Tool == "file_read").ToggleCommand.Execute(null);
        editor.SaveCommand.Execute(null);

        Assert.False(editor.IsOpen);
        using var document = JsonDocument.Parse(sent!);
        var agent = document.RootElement.GetProperty("agents").EnumerateArray()
            .Single(a => a.GetProperty("key").GetString() == "redacteur");
        Assert.Equal("Relecteur", agent.GetProperty("role").GetString());
        Assert.Equal("Relire et vérifier", agent.GetProperty("goal").GetString());
        Assert.Equal(["file_read", "file_write"], agent.GetProperty("tools").EnumerateArray().Select(t => t.GetString()));
        // The other agent is untouched.
        Assert.Equal(2, document.RootElement.GetProperty("agents").GetArrayLength());
    }

    [Fact]
    public void A_tool_the_team_does_not_hold_yet_is_given_from_the_rest_of_the_catalogue()
    {
        var editor = new AgentEditorViewModel();
        string? sent = null;
        editor.Open(Blueprint, "redacteur", ["/a:/docs:ro"], json => sent = json);

        Assert.True(editor.Tools.HasMore);
        Assert.False(editor.Tools.ShowMore);
        Assert.DoesNotContain(editor.Tools.MoreFamilies.SelectMany(f => f.Chips), c => c.Tool is "file_read" or "shell_command");
        editor.Tools.ToggleMoreCommand.Execute(null);
        Assert.True(editor.Tools.ShowMore);

        editor.Tools.MoreFamilies.SelectMany(f => f.Chips).Single(c => c.Tool == "pdf_reader").ToggleCommand.Execute(null);
        Assert.Contains("pdf_reader", editor.KeyLine, StringComparison.Ordinal);
        editor.SaveCommand.Execute(null);

        using var document = JsonDocument.Parse(sent!);
        var agent = document.RootElement.GetProperty("agents").EnumerateArray()
            .Single(a => a.GetProperty("key").GetString() == "redacteur");
        Assert.Equal(["file_write", "pdf_reader"], agent.GetProperty("tools").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public void A_tool_a_task_grants_is_among_the_team_chips()
    {
        var editor = new AgentEditorViewModel();
        editor.Open(
            """{"agents":[{"key":"a","role":"A","goal":"g","tools":["file_read"]}],"tasks":[{"key":"t","description":"d","agent":"a","tools":["grep"]}]}""",
            "a", [], _ => { });

        Assert.Equal(["file_read", "grep"], editor.Chips.Select(c => c.Tool));
    }

    [Fact]
    public void A_capability_chip_says_what_the_tool_does_on_the_team_folders_and_keeps_its_id()
    {
        // UX lot 02: « read /docs », never « file_read »; the identifier stays the tooltip and
        // the expert's line. A tool Studio has no words for keeps its identifier.
        var editor = new AgentEditorViewModel();
        editor.Open(Blueprint, "redacteur", ["/a:/docs:ro", "/b:/output:rw"], _ => { });

        Assert.Equal(["read /docs", "write /output", "browse a web page"], editor.Chips.Select(c => c.Label));
        Assert.Equal(["file_read", "file_write", "web_scrape"], editor.Chips.Select(c => c.Tool));
        Assert.Equal("folder-open", editor.Chips[0].Icon);

        editor.Open(Blueprint, "redacteur", [], _ => { });
        Assert.Equal("write", editor.Chips.Single(c => c.Tool == "file_write").Label.Trim());
    }

    [Theory]
    [InlineData(@"C:\Users\me\Docs", "Docs")]
    [InlineData(@"C:\Users\me\Docs\", "Docs")]
    [InlineData("/home/me/output/", "output")]
    [InlineData("Docs", "Docs")]
    [InlineData(@"C:\", "C:")]
    [InlineData("", "")]
    public void A_folder_reads_by_its_own_name(string path, string expected) =>
        Assert.Equal(expected, FolderNames.Of(path));

    [Fact]
    public void Adding_an_agent_appends_a_sluggged_unique_key()
    {
        var editor = new AgentEditorViewModel();
        string? sent = null;
        editor.Open(Blueprint, agentKey: null, [], json => sent = json);

        Assert.True(editor.IsNew);
        editor.Name = "Vérificateur Web";
        editor.SaveCommand.Execute(null);

        using var document = JsonDocument.Parse(sent!);
        var agents = document.RootElement.GetProperty("agents");
        Assert.Equal(3, agents.GetArrayLength());
        Assert.Equal("verificateur-web", agents[2].GetProperty("key").GetString());
        Assert.Equal("Vérificateur Web", agents[2].GetProperty("role").GetString());
    }

    /// <summary>
    /// A new agent's key is its name through the shared folder slug rule (STUDIO-24): the key
    /// a short name always had, a numeric suffix on a clash, and the team fallback for a name
    /// that keeps no ASCII letter or digit.
    /// </summary>
    [Theory]
    [InlineData("Analyste données & graphiques", "analyste-donnees-graphiques")]
    [InlineData("Rédacteur", "redacteur-2")]
    [InlineData("???", FolderSlug.TeamFallback)]
    [InlineData("每日监控", FolderSlug.TeamFallback)]
    public void A_new_agent_key_is_its_name_through_the_shared_slug_rule(string name, string key)
    {
        var editor = new AgentEditorViewModel();
        string? sent = null;
        editor.Open(Blueprint, agentKey: null, [], json => sent = json);

        editor.Name = name;
        editor.SaveCommand.Execute(null);

        using var document = JsonDocument.Parse(sent!);
        Assert.Equal(key, document.RootElement.GetProperty("agents")[2].GetProperty("key").GetString());
    }

    /// <summary>
    /// The expert key line previews the key while the name is still blank — spaces included,
    /// which used to throw from the slug rule instead.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_new_agent_without_a_name_yet_previews_the_agent_key(string name)
    {
        var editor = new AgentEditorViewModel();
        editor.Open(Blueprint, agentKey: null, [], _ => { });

        editor.Name = name;

        Assert.StartsWith("id: agent ·", editor.KeyLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_an_agent_drops_its_entry_and_only_its_entry()
    {
        var editor = new AgentEditorViewModel();
        string? sent = null;
        editor.Open(Blueprint, "collecteur", [], json => sent = json);
        editor.RemoveCommand.Execute(null);

        using var document = JsonDocument.Parse(sent!);
        var agents = document.RootElement.GetProperty("agents");
        Assert.Equal(1, agents.GetArrayLength());
        Assert.Equal("redacteur", agents[0].GetProperty("key").GetString());
        // Tasks are left alone — the engine's validation owns referential integrity.
        Assert.Equal(1, document.RootElement.GetProperty("tasks").GetArrayLength());
    }

    [Fact]
    public void An_empty_name_blocks_the_save()
    {
        var editor = new AgentEditorViewModel();
        editor.Open(Blueprint, "collecteur", [], _ => { });
        editor.Name = "  ";
        Assert.False(editor.CanSave);
    }

    /// <summary>
    /// The symptom the owner reported: the agent editor's « Sur quel dossier » line showed
    /// raw mount strings, so a novice editing an agent read <c>C:\Users\…</c> where only a
    /// VFS mount point belongs. The line names what the agent addresses, and its rights.
    /// </summary>
    [Fact]
    public void The_agent_editor_names_mounts_the_way_agents_address_them()
    {
        var editor = new AgentEditorViewModel();
        editor.Open(
            Blueprint,
            "collecteur",
            [@"C:\Users\demo\Factures:/factures:ro", @"C:\Users\demo\Sorties:/output:rw"],
            _ => { });

        Assert.DoesNotContain(@"C:\", editor.ScopeInfo, StringComparison.Ordinal);
        Assert.Contains("/factures", editor.ScopeInfo, StringComparison.Ordinal);
        Assert.Contains("/output", editor.ScopeInfo, StringComparison.Ordinal);
        // Rights travel with the path — read-only and writable read differently.
        Assert.NotEqual(
            editor.ScopeInfo.Split(" · ")[0],
            editor.ScopeInfo.Split(" · ")[1]);
    }

    [Fact]
    public void An_unreadable_mount_is_named_as_such_rather_than_dumped()
    {
        var editor = new AgentEditorViewModel();
        editor.Open(Blueprint, "collecteur", [@"C:\Users\demo\Factures"], _ => { });

        Assert.DoesNotContain(@"C:\", editor.ScopeInfo, StringComparison.Ordinal);
    }

    [Fact]
    public void No_folder_reads_as_a_dash()
    {
        var editor = new AgentEditorViewModel();
        editor.Open(Blueprint, "collecteur", [], _ => { });

        Assert.Equal("—", editor.ScopeInfo);
    }
}
