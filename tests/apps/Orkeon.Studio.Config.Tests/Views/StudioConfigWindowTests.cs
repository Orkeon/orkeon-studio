using Orkeon.Studio.Config.Presentation;
using Orkeon.Studio.Config.Tests.Doubles;
using Orkeon.Studio.Config.Views;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.FileSystem;
using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Config.Tests.Views;

[Collection(TerminalGuiTests.Name)]
public class StudioConfigWindowTests
{
    private static async Task<ConfigEditorModel> OpenAsync(string json)
    {
        var directory = Directory.CreateTempSubdirectory("studio-config-window").FullName;
        try
        {
            var path = Path.Combine(directory, "appsettings.json");
            await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
            var model = CreateModel();
            Assert.Null(await model.OpenAsync(path, TestContext.Current.CancellationToken));
            return model;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    private static ConfigEditorModel CreateModel()
    {
        var runner = new OrkeonProcessRunner(
            new FakeProcessLauncher(),
            new OrkeonBinaryLocator(new FakeExecutableProbe(), ["orkeon"]));

        return new ConfigEditorModel(new FakeDirectoryProbe("/data/in"), runner, new FakeLlmEndpointProbe());
    }

    [Fact]
    public void The_window_opens_on_the_llm_screen_with_every_section_available()
    {
        using var window = new StudioConfigWindow(CreateModel());

        var titles = window.Sections.Select(section => section.Title).ToList();

        Assert.Equal(
            ["LLM", "Rate limiting", "RAG", "Decision models", "Context & skills", "Tool bridge", "Browser", MountEditorModel.SectionTitle, "Logging", "LLM logging", "Raw JSON (read-only)"],
            titles);
        Assert.Equal(0, window.CurrentSection);
        Assert.True(window.Sections[0].Visible);
        Assert.False(window.Sections[1].Visible);
    }

    [Fact]
    public void Selecting_a_section_shows_it_and_hides_the_others()
    {
        using var window = new StudioConfigWindow(CreateModel());

        window.SelectSection(2);

        Assert.Equal(2, window.CurrentSection);
        Assert.True(window.Sections[2].Visible);
        Assert.All(window.Sections.Where((_, index) => index != 2), section => Assert.False(section.Visible));
    }

    [Fact]
    public void An_index_outside_the_section_list_is_ignored()
    {
        using var window = new StudioConfigWindow(CreateModel());

        window.SelectSection(99);

        Assert.Equal(0, window.CurrentSection);
    }

    [Fact]
    public void Validating_reads_every_screen_back_into_the_document()
    {
        var model = CreateModel();
        using var window = new StudioConfigWindow(model);

        model.Llm.Model = "llama3";
        model.Llm.BaseUrl = "http://localhost:11434";
        window.ReloadSections();

        var preflight = window.Validate();

        Assert.False(preflight.HasBlockingErrors);
        Assert.Equal("llama3", model.Document.Llm.Model);
    }

    [Fact]
    public async Task A_provider_name_the_engine_does_not_know_survives_the_window()
    {
        var model = await OpenAsync("""{"Orkeon":{"Decision":{"Provider":"acme"}}}""");
        using var window = new StudioConfigWindow(model);
        window.ReloadSections();
        window.SelectSection(window.Sections.ToList().FindIndex(section => section is DecisionSectionView));

        var preflight = window.Validate();
        window.SelectSection(window.Sections.Count - 1);

        Assert.Contains(preflight.FieldErrors, e => e.Contains("'acme'", StringComparison.Ordinal));
        Assert.Equal("acme", model.Document.GetString(DecisionSection.ProviderPath));
        Assert.Equal("acme", model.Decision.Provider);
    }

    [Fact]
    public void Validating_refreshes_the_decision_status()
    {
        var model = CreateModel();
        using var window = new StudioConfigWindow(model);
        var decision = Assert.Single(window.Sections.OfType<DecisionSectionView>());
        model.Decision.SelectProvider(DecisionForm.StandardChoices.Count - 1);
        model.Decision.Enabled = false;
        decision.Load();
        Assert.DoesNotContain("Off", decision.StatusText, StringComparison.Ordinal);

        window.Validate();

        Assert.Equal("Off: every run goes without the decision models.", decision.StatusText);
    }

    [Fact]
    public void Validating_an_empty_document_surfaces_the_win01_warning()
    {
        using var window = new StudioConfigWindow(CreateModel());

        var preflight = window.Validate();

        Assert.Contains(preflight.Messages, message => message.Code == ValidationCodes.LlmSectionMissing);
    }

    [Fact]
    public void Opening_the_raw_screen_shows_what_the_typed_screens_have_written()
    {
        var model = CreateModel();
        using var window = new StudioConfigWindow(model);

        model.Llm.Model = "llama3";
        window.ReloadSections();
        window.SelectSection(window.Sections.Count - 1);

        Assert.Contains("llama3", model.RawJson);
    }

    [Fact]
    public void The_mount_screen_is_built_over_the_session_mount_list()
    {
        var model = CreateModel();
        model.Mounts.Add(new MountDefinition { PhysicalPath = "/data/in", VirtualPath = "/workspace" });

        using var window = new StudioConfigWindow(model, new FakeDirectoryLister().WithDirectory("/data/in"));
        window.ReloadSections();

        Assert.Single(model.Mounts.Rows);
    }

    [Fact]
    public void An_empty_mount_list_warns_in_both_scopes_and_blocks_neither()
    {
        using var window = new StudioConfigWindow(CreateModel());

        var editing = window.Validate();
        var saving = window.Validate(ValidationScope.Saving);

        // STUDIO-57: a team that reads or writes no file needs no folder.
        Assert.False(editing.HasBlockingErrors);
        Assert.False(saving.HasBlockingErrors);
        Assert.Contains(
            saving.Messages,
            message => message.Code == ValidationCodes.MountsEmpty
                && message.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void The_open_picker_offers_the_json_files_of_the_folder_it_is_standing_in()
    {
        // The action used to return a folder and assume the conventional file name, which made
        // an appsettings.Development.json — or any settings file named for its crew — unopenable.
        var tree = new FakeDirectoryLister()
            .WithDirectory("/data/in")
            .WithFile("/data/in/appsettings.Development.json")
            .WithFile("/data/in/notes.txt");

        using var picker = new DirectoryPickerDialog("/data/in", tree, "*.json");

        Assert.Contains("appsettings.Development.json", picker.EntriesForTest);
        Assert.DoesNotContain("notes.txt", picker.EntriesForTest);
    }

    /// <summary>
    /// The window's own pass (every screen read back, the forms applied, the screens refreshed): the
    /// model retyped on the LLM screen takes the context window out of the file and out of the context
    /// screen's field, which a later pass would otherwise write back.
    /// </summary>
    [Fact]
    public async Task A_model_retyped_empties_the_context_window_field_of_the_context_screen()
    {
        var model = await OpenAsync("""{ "Llm": { "Model": "old-model", "ContextWindowTokens": 128000 } }""");
        using var window = new StudioConfigWindow(model);
        window.ReloadSections();
        var llm = window.Sections[0].SubViews.OfType<Terminal.Gui.Views.TextField>().First();
        var context = window.Sections.OfType<ContextSectionView>().Single();
        var windowField = context.SubViews.OfType<Terminal.Gui.Views.TextField>().ElementAt(4);
        Assert.Equal("old-model", llm.Text);
        Assert.Equal("128000", windowField.Text);

        llm.Text = "new-model";
        window.Validate();

        Assert.Equal("", windowField.Text);
        window.Validate();
        Assert.False(model.Document.ContainsPath("Llm:ContextWindowTokens"));
        Assert.Equal("new-model", model.Document.Llm.Model);
    }
}
