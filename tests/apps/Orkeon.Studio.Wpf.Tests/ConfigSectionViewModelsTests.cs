using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Wpf.ViewModels.Config;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// What the <c>Llm</c> section's view model keeps once the form no view showed is gone (GAP-36,
/// decision 4): it reaches the document through a delegate, and the RAG's profile is written like
/// any field. Its other members — the election, the mirror, the hand-written entries — are the
/// model-settings screen's, and <c>HostLlmProfilesScreenTests</c> drives them from there.
/// </summary>
public sealed class LlmSectionViewModelTests
{
    [Fact]
    public void Should_WriteTheRagProfileIntoTheDocument_And_RemoveItForTheDefault()
    {
        var document = AppSettingsDocument.Parse("{}");
        var changes = 0;
        var section = new LlmSectionViewModel(() => document, () => changes++);

        section.RagLlmProfile = " claude ";

        Assert.Equal("claude", document.GetString(RagSection.LlmProfilePath));
        Assert.Equal(1, changes);

        section.RagLlmProfile = "default";

        Assert.Null(document.GetNode(RagSection.LlmProfilePath));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Should_SeeTheNewFile_When_TheDocumentIsSwapped()
    {
        // The section reaches the document through a delegate, which is what lets "open another
        // file" keep the bindings the screen already holds.
        var document = AppSettingsDocument.Parse("""{"Llm":{"Model":"first"}}""");
        var section = new LlmSectionViewModel(() => document, () => { });
        Assert.True(section.Exists);

        document = AppSettingsDocument.Parse("""{"Orkeon":{"Rag":{"LlmProfile":"second"}}}""");

        Assert.False(section.Exists);
        Assert.Equal("second", section.RagLlmProfile);
    }
}

public sealed class RagSectionViewModelTests
{
    private static RagSectionViewModel Build(AppSettingsDocument document) =>
        new(() => document, () => { });

    [Theory]
    [InlineData("fast")]
    [InlineData("balanced")]
    [InlineData("quality")]
    [InlineData("adaptive")]
    [InlineData("corrective")]
    public void Should_OfferEveryKnownProfile_In_TheClosedList(string profile)
    {
        // The combo box is populated from RagProfilePresets, so it cannot drift from the runtime.
        Assert.Contains(profile, RagSectionViewModel.KnownProfiles);
    }

    [Fact]
    public void Should_AcceptAKnownProfile()
    {
        var section = Build(AppSettingsDocument.CreateEmpty());

        section.Profile = "balanced";

        Assert.True(section.HasValidProfile);
    }

    [Fact]
    public void Should_RejectAnUnknownProfile()
    {
        var section = Build(AppSettingsDocument.CreateEmpty());

        section.Profile = "turbo";

        Assert.False(section.HasValidProfile);
    }

    [Fact]
    public void Should_StayOff_When_OnlyOneHalfOfTheWebFallbackIsEnabled()
    {
        var section = Build(AppSettingsDocument.CreateEmpty());

        section.CorrectiveWebFallbackEnabled = true;

        Assert.False(section.WebFallbackFullyEnabled);
        Assert.Contains("transport", section.WebFallbackStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Should_TurnOn_When_BothHalvesOfTheWebFallbackAreEnabled()
    {
        var section = Build(AppSettingsDocument.CreateEmpty());

        section.CorrectiveWebFallbackEnabled = true;
        section.WebFallbackEnabled = true;

        Assert.True(section.WebFallbackFullyEnabled);
    }

    [Fact]
    public void Should_WriteTheNestedKeys_When_SwitchesAreToggled()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var section = Build(document);

        section.HybridRetrievalEnabled = true;

        Assert.True(document.GetBoolean("Orkeon:Rag:Retrieval:Hybrid:Enabled"));
    }
}

public sealed class LoggingSectionViewModelTests
{
    [Fact]
    public void Should_LoadEveryCategory_From_TheDocument()
    {
        var document = AppSettingsDocument.Parse(
            """{"Logging":{"LogLevel":{"Default":"Information","Orkeon":"Debug"}}}""");

        var section = new LoggingSectionViewModel(() => document, () => { });

        Assert.Equal(2, section.Categories.Count);
        Assert.Equal("Information", section.DefaultLevel);
    }

    [Fact]
    public void Should_RewriteTheMap_When_ACategoryIsRenamed()
    {
        var document = AppSettingsDocument.Parse("""{"Logging":{"LogLevel":{"Old":"Debug"}}}""");
        var section = new LoggingSectionViewModel(() => document, () => { });

        section.Categories[0].Category = "New";

        Assert.Null(document.GetString("Logging:LogLevel:Old"));
        Assert.Equal("Debug", document.GetString("Logging:LogLevel:New"));
    }

    [Fact]
    public void Should_DropTheCategory_When_ARowIsRemoved()
    {
        var document = AppSettingsDocument.Parse("""{"Logging":{"LogLevel":{"Orkeon":"Debug"}}}""");
        var section = new LoggingSectionViewModel(() => document, () => { });

        section.RemoveCategory(section.Categories[0]);

        Assert.Null(document.GetString("Logging:LogLevel:Orkeon"));
    }

    [Fact]
    public void Should_OfferTheClosedLevelList()
    {
        Assert.Contains("Warning", LoggingSectionViewModel.KnownLevels);
        Assert.Contains("None", LoggingSectionViewModel.KnownLevels);
    }
}

/// <summary>STUDIO-21 — the shell allow-list form never writes the empty array the runtime reads as "block everything".</summary>
public sealed class ShellToolsSectionViewModelTests
{
    private static (ShellToolsSectionViewModel Section, AppSettingsDocument Document, Func<int> Changes) Build(string json = "{}")
    {
        var document = AppSettingsDocument.Parse(json);
        var changes = 0;
        var section = new ShellToolsSectionViewModel(() => document, () => changes++);
        return (section, document, () => changes);
    }

    [Fact]
    public void The_switch_is_written_only_when_on_and_the_lists_one_command_per_line()
    {
        var (section, document, changes) = Build();

        section.AllowInterpreters = true;
        section.ExtraAllowedCommandsText = "git\r\n dotnet \n\n";
        section.AllowedCommandsText = "";

        Assert.True(document.GetBoolean("Orkeon:Tools:Shell:AllowInterpreters"));
        Assert.Equal(["git", "dotnet"], document.GetStringArray("Orkeon:Tools:Shell:ExtraAllowedCommands"));
        Assert.Null(document.GetNode("Orkeon:Tools:Shell:AllowedCommands"));
        Assert.False(section.ReplacesDefaults);
        Assert.Equal(2, changes());

        section.AllowInterpreters = false;
        Assert.Null(document.GetNode("Orkeon:Tools:Shell:AllowInterpreters"));
    }

    [Fact]
    public void A_file_that_replaces_the_defaults_reads_back_as_lines()
    {
        var (section, _, _) = Build("""{ "Orkeon": { "Tools": { "Shell": { "AllowedCommands": ["ls", "cat"] } } } }""");

        Assert.True(section.Exists);
        Assert.True(section.ReplacesDefaults);
        Assert.Equal("ls" + Environment.NewLine + "cat", section.AllowedCommandsText);
        Assert.Equal("", section.ExtraAllowedCommandsText);
    }
}

/// <summary>
/// STUDIO-21 — the MCP form: rows over the servers of the file, every keystroke written in
/// place, a fresh identifier per added server, and the row's own problem said the way the
/// validator will refuse the save.
/// </summary>
public sealed class McpSectionViewModelTests
{
    private static (McpSectionViewModel Section, AppSettingsDocument Document, Func<int> Changes) Build(string json = "{}")
    {
        var document = AppSettingsDocument.Parse(json);
        var changes = 0;
        var section = new McpSectionViewModel(() => document, () => changes++);
        return (section, document, () => changes);
    }

    [Fact]
    public void Adding_a_server_writes_a_stdio_object_under_a_fresh_identifier_and_says_it_needs_a_command()
    {
        var (section, document, changes) = Build();
        Assert.False(section.HasServers);
        Assert.True(section.Enabled);

        section.AddServerCommand.Execute(null);
        section.AddServerCommand.Execute(null);

        Assert.Equal(["server-1", "server-2"], section.Servers.Select(s => s.Id));
        Assert.Equal("Stdio", document.GetString("MCP:Servers:server-1:Transport"));
        Assert.True(section.HasServers);
        Assert.Equal(2, changes());
        var row = section.Servers[0];
        Assert.True(row.HasProblem);
        Assert.Equal("A stdio server needs a command to launch.", row.Problem);

        row.Command = "npx";
        row.ArgsText = "-y\n@modelcontextprotocol/server-filesystem\n/data";
        row.EnvText = "NODE_ENV=production\nnot a pair\nTOKEN = abc";

        Assert.False(row.HasProblem);
        Assert.Equal("npx", document.GetString("MCP:Servers:server-1:Command"));
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "/data"], document.GetStringArray("MCP:Servers:server-1:Args"));
        Assert.Equal("production", document.GetStringMap("MCP:Servers:server-1:Env")["NODE_ENV"]);
        Assert.Equal("abc", document.GetStringMap("MCP:Servers:server-1:Env")["TOKEN"]);
        Assert.Equal(5, changes());
    }

    [Fact]
    public void Switching_to_http_asks_for_a_url_and_a_rename_moves_the_node()
    {
        var (section, document, _) = Build("""{ "MCP": { "Servers": { "files": { "Command": "npx", "Timeout": 7 } } } }""");
        var row = Assert.Single(section.Servers);
        Assert.False(row.HasProblem);

        row.Transport = "Sse";
        Assert.True(row.IsSse);
        Assert.Equal("An HTTP server needs an absolute http(s) URL.", row.Problem);
        row.Url = "https://mcp.example.com/rpc";
        Assert.False(row.HasProblem);

        row.IdText = "remote";
        Assert.Equal("remote", row.Id);
        Assert.Null(document.GetNode("MCP:Servers:files"));
        Assert.Equal(7, document.GetInt32("MCP:Servers:remote:Timeout"));
        Assert.Equal("https://mcp.example.com/rpc", document.GetString("MCP:Servers:remote:Url"));

        // An unusable identifier stays on screen with its problem; the document keeps the last good one.
        row.IdText = "re mote";
        Assert.Equal("remote", row.Id);
        Assert.Equal("An identifier is required: letters, digits, '.', '_' and '-'.", row.Problem);
    }

    [Fact]
    public void A_duplicate_identifier_is_refused_and_removing_a_server_drops_its_node()
    {
        var (section, document, _) = Build("""{ "MCP": { "Enabled": false, "Servers": { "a": { "Command": "x" }, "b": { "Command": "y" } } } }""");
        Assert.False(section.Enabled);
        var b = section.Servers[1];

        b.IdText = "a";
        Assert.Equal("b", b.Id);
        Assert.Equal("Another server already carries this identifier.", b.Problem);

        Assert.True(section.RemoveServerCommand.CanExecute(b));
        section.RemoveServerCommand.Execute(b);
        Assert.Equal(["a"], section.Servers.Select(s => s.Id));
        Assert.Null(document.GetNode("MCP:Servers:b"));

        section.Enabled = true;
        Assert.Null(document.GetNode("MCP:Enabled"));
    }

    [Fact]
    public void Replacing_the_document_rebuilds_the_rows()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var section = new McpSectionViewModel(() => document, () => { });
        Assert.Empty(section.Servers);

        document = AppSettingsDocument.Parse("""{ "MCP": { "Servers": { "one": { "Transport": "sse", "Url": "http://x" } } } }""");
        section.Refresh();

        var row = Assert.Single(section.Servers);
        Assert.Equal("Sse", row.Transport);
        Assert.True(row.IsSse);
    }
}

/// <summary>
/// STUDIO-22 — the limits tab: every empty field names the engine's default, and a switch bound
/// to an absent key is a plain boolean, so one click sets it and turning it back to the default
/// removes the key rather than spelling a value the engine already applies.
/// </summary>
public sealed class LimitsDefaultsTests
{
    [Fact]
    public void The_rate_limiting_watermarks_are_the_engine_defaults()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var section = new RateLimitingSectionViewModel(() => document, () => { });

        Assert.Equal("unlimited", section.MaxConcurrentRequestsDefault);
        Assert.Equal("60", RateLimitingSectionViewModel.GlobalRequestsPerMinuteDefault);
        Assert.Equal("30", RateLimitingSectionViewModel.ProviderRequestsPerMinuteDefault);
        Assert.Equal("20", RateLimitingSectionViewModel.AgentRequestsPerMinuteDefault);
        Assert.Equal("5", RateLimitingSectionViewModel.QueueLimitDefault);
        Assert.Equal("fast", RagSectionViewModel.ProfileDefault);
        Assert.Equal("3", RagSectionViewModel.CorrectiveMaxIterationsDefault);
        Assert.Equal("unlimited", new LlmLoggingSectionViewModel(() => document, () => { }).MaxBodyLengthCharsDefault);
    }

    [Fact]
    public void An_llm_logging_switch_reads_its_absent_key_as_on_and_one_click_turns_it_off()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var changes = 0;
        var section = new LlmLoggingSectionViewModel(() => document, () => changes++);
        Assert.True(section.FullEmbeddingLog);
        Assert.True(section.LogStreamingExchanges);

        section.FullEmbeddingLog = false;

        Assert.False(document.GetBoolean("LlmLogging:FullEmbeddingLog"));
        Assert.Equal(1, changes);

        // Back to the default: the key goes, the file spells nothing the engine already does.
        section.FullEmbeddingLog = true;
        Assert.Null(document.GetNode("LlmLogging:FullEmbeddingLog"));
        Assert.Equal(2, changes);

        // Setting the default on an absent key changes nothing and marks nothing.
        section.LogStreamingExchanges = true;
        Assert.Equal(2, changes);
    }

    [Fact]
    public void A_rag_switch_reads_its_absent_key_as_off_and_one_click_turns_it_on()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Rag": { "WebFallback": { "Enabled": false } } } }""");
        var changes = 0;
        var section = new RagSectionViewModel(() => document, () => changes++);
        Assert.False(section.HybridRetrievalEnabled);
        Assert.False(section.WebFallbackEnabled);

        section.HybridRetrievalEnabled = true;
        Assert.True(document.GetBoolean("Orkeon:Rag:Retrieval:Hybrid:Enabled"));

        section.HybridRetrievalEnabled = false;
        Assert.Null(document.GetNode("Orkeon:Rag:Retrieval:Hybrid:Enabled"));

        // An explicit false in the file reads as off; turning it on writes true.
        section.WebFallbackEnabled = true;
        Assert.True(document.GetBoolean("Orkeon:Rag:WebFallback:Enabled"));
        Assert.Equal(3, changes);
    }

    /// <summary>
    /// A check box bound to a nullable boolean starts indeterminate and swallows the first click
    /// (null becomes false, then true): no section form may expose one.
    /// </summary>
    [Fact]
    public void No_section_form_exposes_a_nullable_boolean()
    {
        var offenders = typeof(DocumentSectionViewModel).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(DocumentSectionViewModel)))
            .SelectMany(type => type.GetProperties()
                .Where(property => property.PropertyType == typeof(bool?))
                .Select(property => $"{type.Name}.{property.Name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A nullable boolean behind a check box costs the user two clicks: " + string.Join(", ", offenders));
    }
}

/// <summary>Orkeon #440 — the browser form writes the engine and the window only when they differ from the defaults.</summary>
public sealed class BrowserToolsSectionViewModelTests
{
    private static (BrowserToolsSectionViewModel Section, AppSettingsDocument Document, Func<int> Changes) Build(string json = "{}")
    {
        var document = AppSettingsDocument.Parse(json);
        var changes = 0;
        var section = new BrowserToolsSectionViewModel(() => document, () => changes++);
        return (section, document, () => changes);
    }

    [Fact]
    public void The_engine_and_the_window_are_written_only_away_from_the_defaults()
    {
        var (section, document, changes) = Build();
        Assert.Equal("firefox", section.Engine);
        Assert.False(section.Exists);

        section.Engine = "chrome";
        section.Headless = true;

        Assert.Equal("chrome", document.GetString("Orkeon:Tools:Browser:Engine"));
        Assert.True(document.GetBoolean("Orkeon:Tools:Browser:Headless"));
        Assert.Equal(2, changes());

        section.Engine = "firefox";
        section.Headless = false;
        Assert.Null(document.GetNode("Orkeon:Tools:Browser"));
    }

    [Fact]
    public void Safari_cannot_run_headless_and_a_written_engine_shows_in_its_runtime_spelling()
    {
        var (section, _, _) = Build("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "Safari" } } } }""");

        Assert.Equal("safari", section.Engine);
        Assert.True(section.IsSafari);
        Assert.False(section.CanRunHeadless);
        Assert.Equal("Safari", BrowserToolsSectionViewModel.EngineLabel(section.Engine));
        Assert.Equal(["firefox", "chrome", "edge", "brave", "safari"], BrowserToolsSectionViewModel.Engines);
    }

    [Fact]
    public void A_former_section_is_said_and_renamed_on_demand()
    {
        var (section, document, changes) = Build("""{ "Orkeon": { "Tools": { "Firefox": { "Headless": true } } } }""");
        Assert.True(section.UsesLegacySection);
        Assert.True(section.Headless);

        section.MoveLegacySectionCommand.Execute(null);

        Assert.False(section.UsesLegacySection);
        Assert.Null(document.GetNode("Orkeon:Tools:Firefox"));
        Assert.True(document.GetBoolean("Orkeon:Tools:Browser:Headless"));
        Assert.Equal(1, changes());
    }

    [Fact]
    public void Picking_safari_turns_the_headless_switch_off_and_says_so()
    {
        var (section, document, _) = Build("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "edge", "Headless": true } } } }""");
        var raised = new List<string?>();
        section.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        section.Engine = "safari";

        Assert.False(section.Headless);
        Assert.Null(document.GetNode("Orkeon:Tools:Browser:Headless"));
        Assert.Contains(nameof(BrowserToolsSectionViewModel.Headless), raised);
        Assert.False(section.CanToggleHeadless);
    }

    [Fact]
    public void A_file_with_safari_and_headless_keeps_the_switch_usable_to_undo_it()
    {
        var (section, _, _) = Build("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "safari", "Headless": true } } } }""");

        Assert.False(section.CanRunHeadless);
        Assert.True(section.CanToggleHeadless);

        section.Headless = false;
        Assert.False(section.CanToggleHeadless);
    }

    [Fact]
    public void An_unknown_engine_is_among_the_choices_and_the_choices_carry_product_names()
    {
        var (section, _, _) = Build("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "opera" } } } }""");

        Assert.Equal("opera", section.Engine);
        Assert.Equal(["firefox", "chrome", "edge", "brave", "safari", "opera"], section.EngineChoices.Select(c => c.Id));
        Assert.Equal("Firefox", section.EngineChoices[0].Label);
        Assert.Equal("opera", section.EngineChoices[^1].Label);
    }

    [Fact]
    public void Renaming_without_a_former_section_changes_nothing()
    {
        var (section, document, changes) = Build("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "edge" } } } }""");
        var before = document.ToJson();

        section.MoveLegacySectionCommand.Execute(null);
        section.RemoveLegacySectionCommand.Execute(null);

        Assert.Equal(before, document.ToJson());
        Assert.Equal(0, changes());
    }

    [Fact]
    public void Both_sections_are_said_and_the_former_one_removed_on_demand()
    {
        var (section, document, changes) = Build("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "edge" }, "Firefox": { "Headless": true } } } }""");
        Assert.True(section.HasBothSections);
        Assert.False(section.UsesLegacySection);

        section.RemoveLegacySectionCommand.Execute(null);

        Assert.False(section.HasBothSections);
        Assert.Null(document.GetNode("Orkeon:Tools:Firefox"));
        Assert.Equal("edge", document.GetString("Orkeon:Tools:Browser:Engine"));
        Assert.Equal(1, changes());
    }

    [Fact]
    public void The_profile_the_lifetime_and_the_sharing_are_written_away_from_the_defaults()
    {
        var (section, document, changes) = Build();

        section.Profile = "agents ";
        section.KeepsBrowserOpen = true;
        section.SharedInstance = true;

        Assert.Equal("agents ", section.Profile);
        Assert.Equal("agents", document.GetString("Orkeon:Tools:Browser:Profile"));
        Assert.Equal("Host", document.GetString("Orkeon:Tools:Browser:Lifetime"));
        Assert.True(document.GetBoolean("Orkeon:Tools:Browser:SharedInstance"));
        Assert.Equal(3, changes());

        // A blank typed after the name changes nothing in the file.
        section.Profile = "agents  ";
        Assert.Equal(3, changes());

        section.Profile = "";
        section.KeepsBrowserOpen = false;
        section.SharedInstance = false;
        Assert.Null(document.GetNode("Orkeon:Tools:Browser"));
        Assert.Equal(6, changes());
    }

    [Fact]
    public void Typing_disposable_writes_no_key_and_keeps_the_text()
    {
        var (section, document, changes) = Build();

        section.Profile = "disposable";

        Assert.Equal("disposable", section.Profile);
        Assert.Null(document.GetNode("Orkeon:Tools:Browser"));
        Assert.Equal(0, changes());
    }

    [Fact]
    public void A_profile_changed_in_the_document_replaces_the_text_typed()
    {
        var (section, document, _) = Build();
        section.Profile = "agents ";
        Assert.Equal("agents ", section.Profile);

        document.BrowserTools.Profile = "other";
        section.Refresh();

        Assert.Equal("other", section.Profile);
    }

    [Fact]
    public void Typing_a_profile_renames_a_lone_former_section_and_leaves_its_engine_behind()
    {
        var (section, document, changes) = Build("""{ "Orkeon": { "Tools": { "Firefox": { "Engine": "chrome", "Headless": true } } } }""");

        section.Profile = "agents";

        Assert.Null(document.GetNode("Orkeon:Tools:Firefox"));
        Assert.Equal("agents", document.GetString("Orkeon:Tools:Browser:Profile"));
        Assert.True(document.GetBoolean("Orkeon:Tools:Browser:Headless"));
        Assert.Null(document.GetNode("Orkeon:Tools:Browser:Engine"));
        Assert.Equal(1, changes());
    }

    [Fact]
    public void An_unknown_lifetime_is_kept_while_other_fields_change_and_goes_on_demand()
    {
        var (section, document, changes) = Build("""{ "Orkeon": { "Tools": { "Browser": { "Lifetime": "forever" } } } }""");
        Assert.True(section.HasUnknownLifetime);
        Assert.False(section.KeepsBrowserOpen);

        section.SharedInstance = true;
        Assert.Equal("forever", document.GetString("Orkeon:Tools:Browser:Lifetime"));

        var raised = new List<string?>();
        section.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        section.RemoveLifetimeCommand.Execute(null);

        Assert.Null(document.GetNode("Orkeon:Tools:Browser:Lifetime"));
        Assert.False(section.HasUnknownLifetime);
        Assert.Contains(nameof(BrowserToolsSectionViewModel.HasUnknownLifetime), raised);
        Assert.Contains(nameof(BrowserToolsSectionViewModel.KeepsBrowserOpen), raised);
        Assert.Equal(2, changes());
    }

    [Fact]
    public void An_unreadable_sharing_reads_as_off_and_stays_until_ticked()
    {
        var (section, document, _) = Build("""{ "Orkeon": { "Tools": { "Browser": { "SharedInstance": "yes" } } } }""");
        Assert.False(section.SharedInstance);

        section.KeepsBrowserOpen = true;
        Assert.Equal("yes", document.GetString("Orkeon:Tools:Browser:SharedInstance"));

        section.SharedInstance = true;
        Assert.True(document.GetBoolean("Orkeon:Tools:Browser:SharedInstance"));
    }

    [Fact]
    public void Under_safari_the_profile_is_editable_only_to_clear_one_written()
    {
        var (section, _, _) = Build("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "safari" } } } }""");
        Assert.False(section.CanEditProfile);

        var (named, _, _) = Build("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "safari", "Profile": "agents" } } } }""");
        Assert.True(named.CanEditProfile);
        named.Profile = "";
        Assert.False(named.CanEditProfile);
    }

    [Fact]
    public void A_written_lifetime_reads_whatever_its_case_and_an_unknown_one_reads_as_closing()
    {
        Assert.True(Build("""{ "Orkeon": { "Tools": { "Browser": { "Lifetime": "host" } } } }""").Section.KeepsBrowserOpen);
        Assert.False(Build("""{ "Orkeon": { "Tools": { "Browser": { "Lifetime": "forever" } } } }""").Section.KeepsBrowserOpen);

        Assert.Equal("agents", Build("""{ "Orkeon": { "Tools": { "Browser": { "Profile": " agents " } } } }""").Section.Profile);
    }
}
