using Orkeon.Studio.Core.Tools;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Config;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// STUDIO-21 — the Tools tab: one key row per key a tool needs, on the same store as the
/// profile keys, and the catalogue by family with what each tool needs said in the user's
/// words.
/// </summary>
public sealed class ToolsSettingsViewModelTests
{
    [Fact]
    public void One_row_per_tool_key_and_storing_lands_in_the_environment_and_wipes_the_field()
    {
        var store = new FakeApiKeyStore();
        var tools = new ToolsSettingsViewModel(store);

        Assert.True(tools.HasSecrets);
        Assert.Equal([ToolCatalog.TavilyKeyEnv, ToolCatalog.BraveKeyEnv, ToolCatalog.OpenAiImageKeyEnv], tools.Secrets.Select(s => s.EnvName));
        var tavily = tools.Secrets[0];
        // UX lot 02: the row is titled by what the key serves, never by the tool's identifier.
        Assert.Equal("Web search (Tavily)", tavily.UsedBy);
        Assert.Equal(["Web search (Tavily)", "Web search (Brave)", "Image generation (OpenAI)"], tools.Secrets.Select(s => s.UsedBy));
        Assert.True(tavily.HasConsoleUrl);
        Assert.False(tavily.HasKey);
        Assert.Equal("no key detected", tavily.StatusText);

        tavily.KeyInput = " tvly-abc ";
        Assert.True(tavily.StoreCommand.CanExecute(null));
        tavily.StoreCommand.Execute(null);

        Assert.Equal("tvly-abc", store.Saved[ToolCatalog.TavilyKeyEnv]);
        Assert.Equal("", tavily.KeyInput);
        Assert.True(tavily.HasKey);
        Assert.Equal("key remembered", tavily.StatusText);
    }

    [Fact]
    public void The_catalogue_lists_every_family_with_its_tools_and_says_what_the_needy_ones_need()
    {
        var tools = new ToolsSettingsViewModel(new FakeApiKeyStore());

        Assert.Equal(ToolCatalog.Families.Count, tools.Families.Count);
        Assert.Equal(ToolCatalog.All.Count(), tools.ToolCount);

        var search = Assert.Single(tools.Families, f => f.Key == ToolCatalog.SearchFamily);
        Assert.Equal("Search and knowledge", search.Label);
        Assert.True(search.HasRequirements);
        Assert.Contains(search.Tools, t => t.Name == "web_search" && t.NeedsSomething);
        Assert.Contains(search.Tools, t => t.Name == "cache_search" && !t.NeedsSomething);
        var webSearch = Assert.Single(search.Requirements, r => r.Name == "web_search");
        Assert.Equal("needs the key ORKEON_TAVILY_API_KEY, above", webSearch.Text);
        Assert.True(webSearch.IsKey);
        var brave = Assert.Single(search.Requirements, r => r.Name == "brave_search");
        Assert.Equal("present only once the key BRAVE_API_KEY is remembered, above", brave.Text);
        Assert.Contains(search.Tools, t => t.Name == "rag_ingest" && !t.NeedsSomething);
        var ragSearch = Assert.Single(search.Requirements, r => r.Name == "rag_search");
        Assert.Equal("searches a collection the team's rag: block or rag_ingest fills, or one the store chosen in Settings › Document search (RAG) keeps (expert mode)", ragSearch.Text);
        Assert.False(ragSearch.IsKey);
        Assert.Equal(
            "needs a golden dataset, a YAML file in an authorized folder; it fills the collection from the dataset's corpus itself",
            Assert.Single(search.Requirements, r => r.Name == "rag_eval").Text);

        var web = Assert.Single(tools.Families, f => f.Key == ToolCatalog.WebFamily);
        var imageGeneration = Assert.Single(web.Requirements);
        Assert.Equal("image_generation", imageGeneration.Name);
        Assert.Equal($"needs the key {ToolCatalog.OpenAiImageKeyEnv}, above", imageGeneration.Text);
        Assert.True(imageGeneration.IsKey);

        var data = Assert.Single(tools.Families, f => f.Key == ToolCatalog.DataFamily);
        Assert.All(data.Requirements, r => Assert.Equal("the connection parameters are given at the call, by the agent", r.Text));

        var code = Assert.Single(tools.Families, f => f.Key == ToolCatalog.CodeFamily);
        Assert.Equal("expert setting below: Orkeon:Tools:Shell", Assert.Single(code.Requirements).Text);

        var session = Assert.Single(tools.Families, f => f.Key == ToolCatalog.SessionFamily);
        Assert.Equal(
            "present only with Orkeon:CognitiveMemory:Enabled = true written by hand in the settings file, and held only by a team with memory: true",
            Assert.Single(session.Requirements, r => r.Name == "memory_recall").Text);

        var decision = Assert.Single(tools.Families, f => f.Key == ToolCatalog.DecisionFamily);
        Assert.Equal("Decision models", decision.Label);
        Assert.Equal(
            "present only once a decision provider is set in the Decision models tab: the key TYPESAFE_API_KEY or LIQUID_API_KEY, "
                + "or Orkeon:Decision:Provider, or an Orkeon:TypeSafe or Orkeon:Liquid section in the settings file; "
                + "without the key of TypeSafe or Liquid, the engine turns the decision models off and withholds the tool",
            Assert.Single(decision.Requirements, r => r.Name == "system_one_decide").Text);

        var mcp = Assert.Single(tools.Families, f => f.Key == ToolCatalog.McpFamily);
        Assert.Equal("MCP servers", mcp.Label);
        var promptGet = Assert.Single(mcp.Requirements, r => r.Name == "mcp_prompt_get");
        Assert.Equal("present only while Settings › MCP (expert mode) is on and declares a server", promptGet.Text);
        Assert.False(promptGet.IsKey);

        var collaboration = Assert.Single(tools.Families, f => f.Key == ToolCatalog.CollaborationFamily);
        Assert.Equal(
            "offered only in the act of a script agent (.ork.ts); a YAML team, as Studio writes them, cannot list it",
            Assert.Single(collaboration.Requirements).Text);

        var events = Assert.Single(tools.Families, f => f.Key == ToolCatalog.EventsFamily);
        Assert.False(events.HasRequirements);
        Assert.Equal("7 tools, nothing to configure", events.QuietLine);
    }

    /// <summary>
    /// STUDIO-67 — an e-mail account is declared in Settings › E-mail, so the line sends the
    /// reader to that tab: neither to the settings file nor to a command line. It is not a key of
    /// the card above.
    /// </summary>
    [Fact]
    public void The_email_family_says_its_mailbox_tools_need_an_account_declared_in_the_email_tab()
    {
        var tools = new ToolsSettingsViewModel(new FakeApiKeyStore());

        var email = Assert.Single(tools.Families, f => f.Key == ToolCatalog.EmailFamily);
        Assert.Equal("E-mail", email.Label);
        Assert.Equal(13, email.Tools.Count);
        Assert.Equal(12, email.Requirements.Count);
        Assert.All(email.Requirements, r =>
        {
            Assert.Equal("needs an e-mail account, declared in Settings › E-mail", r.Text);
            Assert.DoesNotContain("Orkeon:Tools", r.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("orkeon email", r.Text, StringComparison.Ordinal);
            Assert.False(r.IsKey);
        });
        Assert.Contains(email.Tools, t => t.Name == "email_parser" && !t.NeedsSomething);
        Assert.DoesNotContain(email.Requirements, r => r.Name == "email_parser");
    }

    /// <summary>
    /// DRIVE-05 — Studio has no drive tab yet: every drive tool's line names the section the
    /// accounts are declared under and the command that signs one in.
    /// </summary>
    [Fact]
    public void The_drive_family_says_each_tool_needs_an_account_declared_in_the_drive_tab()
    {
        var tools = new ToolsSettingsViewModel(new FakeApiKeyStore());

        var drive = Assert.Single(tools.Families, f => f.Key == ToolCatalog.DriveFamily);
        Assert.Equal("Drive", drive.Label);
        Assert.Equal(11, drive.Tools.Count);
        Assert.Equal(11, drive.Requirements.Count);
        Assert.All(drive.Requirements, r =>
        {
            Assert.Equal("needs a drive account, declared in Settings › Drive", r.Text);
            Assert.False(r.IsKey);
        });
    }

    /// <summary>
    /// Every chat tool's line names the tab the accounts are declared in.
    /// </summary>
    [Fact]
    public void The_chat_family_says_each_tool_needs_an_account_declared_in_the_chat_tab()
    {
        var tools = new ToolsSettingsViewModel(new FakeApiKeyStore());

        var chat = Assert.Single(tools.Families, f => f.Key == ToolCatalog.ChatFamily);
        Assert.Equal("Microsoft Teams", chat.Label);
        Assert.Equal(10, chat.Tools.Count);
        Assert.Equal(10, chat.Requirements.Count);
        Assert.All(chat.Requirements, r =>
        {
            Assert.Equal("needs a Teams account, declared in Settings › Microsoft Teams", r.Text);
            Assert.False(r.IsKey);
        });
    }

    /// <summary>STUDIO-67 — the line names the tab the way each language's tab strip does.</summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void The_email_line_names_the_tab_as_the_tab_strip_spells_it(string culture)
    {
        var strings = new FakeResxStudioStrings(culture);
        var tools = new ToolsSettingsViewModel(new FakeApiKeyStore(), strings);

        var email = Assert.Single(tools.Families, f => f.Key == ToolCatalog.EmailFamily);

        Assert.All(email.Requirements, r =>
        {
            Assert.Contains(strings["Studio.Shell.Mails"], r.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("orkeon email", r.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("{0}", r.Text, StringComparison.Ordinal);
        });
    }

    /// <summary>The mcp_prompt_get line names the MCP tab the way each language's tab strip does.</summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void The_mcp_line_names_the_tab_as_the_tab_strip_spells_it(string culture)
    {
        var strings = new FakeResxStudioStrings(culture);
        var tools = new ToolsSettingsViewModel(new FakeApiKeyStore(), strings);

        var mcp = Assert.Single(tools.Families, f => f.Key == ToolCatalog.McpFamily);
        var line = Assert.Single(mcp.Requirements).Text;

        Assert.Contains("› " + strings["Studio.Shell.Mcp"], line, StringComparison.Ordinal);
        Assert.Contains(strings["Studio.Shell.Expert"].ToLowerInvariant(), line.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The lines that point past the novice's tabs say so in every language: the RAG tab is an
    /// expert one, the decision line points at its own tab, and the memory settings are written by
    /// hand. The decision line names each setting, section and key the engine resolves a provider from.
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void The_lines_past_the_novice_tabs_say_where_and_name_what_the_engine_reads(string culture)
    {
        var strings = new FakeResxStudioStrings(culture);
        var tools = new ToolsSettingsViewModel(new FakeApiKeyStore(), strings);
        var lines = tools.Families.SelectMany(f => f.Requirements).ToDictionary(r => r.Name, r => r.Text);

        var rag = lines["rag_search"];
        Assert.Contains(strings["Studio.Settings.RagTab"], rag, StringComparison.Ordinal);
        Assert.Contains(strings["Studio.Shell.Expert"].ToLowerInvariant(), rag.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains(strings["Studio.Settings.TabDecision"], lines["system_one_decide"], StringComparison.Ordinal);
        foreach (var name in new[] { "Orkeon:Decision:Provider", "Orkeon:TypeSafe", "Orkeon:Liquid", "TYPESAFE_API_KEY", "LIQUID_API_KEY" })
            Assert.Contains(name, lines["system_one_decide"], StringComparison.Ordinal);
        Assert.Contains("Orkeon:CognitiveMemory:Enabled = true", lines["memory_recall"], StringComparison.Ordinal);
        Assert.Contains("memory: true", lines["memory_recall"], StringComparison.Ordinal);
        Assert.Contains(".ork.ts", lines["spawn_agent"], StringComparison.Ordinal);
        Assert.All(lines.Values, line => Assert.DoesNotContain("{0}", line, StringComparison.Ordinal));
    }

    /// <summary>The chat line names the Microsoft Teams tab the way each language's tab strip does.</summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void The_chat_line_names_the_tab_as_the_tab_strip_spells_it(string culture)
    {
        var strings = new FakeResxStudioStrings(culture);
        var tools = new ToolsSettingsViewModel(new FakeApiKeyStore(), strings);

        var chat = Assert.Single(tools.Families, f => f.Key == ToolCatalog.ChatFamily);

        Assert.All(chat.Requirements, r =>
        {
            Assert.Contains(strings["Studio.Shell.MicrosoftTeams"], r.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("chat login", r.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("{0}", r.Text, StringComparison.Ordinal);
        });
    }

    /// <summary>The drive line names the Drive tab the way each language's tab strip does.</summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("zh-Hans")]
    public void The_drive_line_names_the_tab_as_the_tab_strip_spells_it(string culture)
    {
        var strings = new FakeResxStudioStrings(culture);
        var tools = new ToolsSettingsViewModel(new FakeApiKeyStore(), strings);

        var drive = Assert.Single(tools.Families, f => f.Key == ToolCatalog.DriveFamily);

        Assert.All(drive.Requirements, r =>
        {
            Assert.Contains(strings["Studio.Shell.Drive"], r.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("drive login", r.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("{0}", r.Text, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A family the label switch forgets must not pass for another one: it shows its raw key,
    /// and this is what fails on it.
    /// </summary>
    [Fact]
    public void Every_family_wears_a_label_of_its_own()
    {
        var tools = new ToolsSettingsViewModel(new FakeApiKeyStore());

        Assert.All(tools.Families, f => Assert.NotEqual(f.Key, f.Label));
        Assert.Equal(tools.Families.Count, tools.Families.Select(f => f.Label).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_tool_key_row_is_retitled_in_the_new_language_when_the_language_switches()
    {
        var strings = new FakeSwitchableStudioStrings("fr");
        var tools = new ToolsSettingsViewModel(new FakeApiKeyStore(), strings);
        var tavily = tools.Secrets[0];
        var raised = new List<string?>();
        tavily.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        strings.Switch();

        Assert.Contains(nameof(SecretRowViewModel.UsedBy), raised);
        Assert.Equal(strings[Orkeon.Studio.Core.Localization.StudioStringKeys.ToolSecretWebSearch], tavily.UsedBy);
        Assert.NotEqual("Web search (Tavily)", tavily.UsedBy);
    }

    [Theory]
    [InlineData("web_scraper", "Web scraper")]
    [InlineData("pdf-reader", "Pdf reader")]
    [InlineData("ocr", "Ocr")]
    public void A_tool_Studio_has_no_label_for_is_humanised_never_raw(string toolId, string expected)
    {
        var tools = new ToolsSettingsViewModel(new FakeApiKeyStore());

        Assert.Equal(expected, tools.ToolLabel(toolId));
    }
}
