using Orkeon.Studio.Config.Presentation;
using Orkeon.Studio.Config.Tests.Doubles;
using Orkeon.Studio.Core.Process;
using Orkeon.Studio.Config.Views;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Config.Tests.Presentation;

/// <summary>
/// The decision models form of the terminal editor: the switch is tri-state, the provider comes from a
/// closed list (plus the name the file holds when the engine does not know it), a provider's section is
/// written or removed whole, and every key the form does not show (the timeouts, the decision profiles)
/// survives an edit while the section stays ticked.
/// </summary>
public sealed class DecisionFormTests
{
    private static DecisionForm Form(Func<string, string?>? environment = null) => new(environment ?? (_ => null));

    [Fact]
    public void Load_reads_the_switch_the_provider_and_each_section()
    {
        var document = AppSettingsDocument.Parse("""
            {"Orkeon":{"Decision":{"Enabled":false,"Provider":"liquid"},"Liquid":{"Model":"d1-ultra","TimeoutSeconds":60}}}
            """);
        var form = Form();

        form.LoadFrom(document);

        Assert.False(form.Enabled);
        Assert.Equal("liquid", form.Provider);
        Assert.Equal(2, form.ProviderChoiceIndex);
        var liquid = form.Providers.Single(p => p.Descriptor.Key == "liquid");
        Assert.True(liquid.SetUp);
        Assert.Equal("d1-ultra", liquid.Model);
        Assert.False(form.Providers.Single(p => p.Descriptor.Key == "typesafe").SetUp);
        Assert.StartsWith("Off", form.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_writes_in_place_and_keeps_what_the_form_does_not_show()
    {
        var document = AppSettingsDocument.Parse("""
            {"Orkeon":{"Decision":{"Profiles":{"triage":{"Llm":"fast"}}},"Liquid":{"TimeoutSeconds":60},"TypeSafe":{}}}
            """);
        var form = Form();
        form.LoadFrom(document);

        form.Providers.Single(p => p.Descriptor.Key == "typesafe").SetUp = false;
        var liquid = form.Providers.Single(p => p.Descriptor.Key == "liquid");
        liquid.Model = " d1.1 ";
        form.SelectProvider(0);
        form.Enabled = null;

        Assert.Empty(form.ApplyTo(document));
        Assert.Null(document.GetNode("Orkeon:TypeSafe"));
        Assert.Equal("d1.1", document.GetString("Orkeon:Liquid:Model"));
        Assert.Equal(60, document.GetInt32("Orkeon:Liquid:TimeoutSeconds"));
        Assert.NotNull(document.GetNode("Orkeon:Decision:Profiles:triage"));
        Assert.Null(document.GetNode(DecisionSection.EnabledPath));
        Assert.Equal("Liquid answers the decision tasks (deduced).", form.Status);
    }

    [Fact]
    public void Two_hosted_sections_are_refused_at_start_and_a_named_one_answers()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"TypeSafe":{},"Liquid":{}}}""");
        var form = Form();
        form.LoadFrom(document);
        Assert.StartsWith("Refused at start", form.Status, StringComparison.Ordinal);

        form.SelectProvider(1);

        Assert.Empty(form.ApplyTo(document));
        Assert.Equal("typesafe", document.GetString(DecisionSection.ProviderPath));
        Assert.Equal("TypeSafe answers the decision tasks.", form.Status);
    }

    [Fact]
    public void A_key_in_the_environment_is_a_provider()
    {
        var form = Form(name => name == "LIQUID_API_KEY" ? "secret" : null);

        form.LoadFrom(AppSettingsDocument.Parse("{}"));

        Assert.Equal("Liquid answers the decision tasks (deduced).", form.Status);
    }

    [Fact]
    public void An_unknown_name_or_an_unreadable_switch_is_refused_and_nothing_is_written()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Decision":{"Enabled":"maybe","Provider":"acme"}}}""");
        var form = Form();
        form.LoadFrom(document);
        Assert.Equal("acme", form.ProviderAsWritten);
        Assert.Equal(DecisionForm.StandardChoices.Count, form.ProviderChoiceIndex);

        var errors = form.ApplyTo(document);

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains(DecisionSection.EnabledPath, StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'acme'", StringComparison.Ordinal));
        Assert.Equal("acme", document.GetString(DecisionSection.ProviderPath));
    }

    [Fact]
    public void An_unknown_name_is_a_row_of_its_own_and_the_screen_keeps_it()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Decision":{"Provider":"acme"}}}""");
        var form = Form();
        form.LoadFrom(document);
        using var view = new DecisionSectionView(form);

        view.Load();
        Assert.Equal((DecisionForm.StandardChoices.Count + 1, DecisionForm.StandardChoices.Count), view.ProviderRowsForTest);
        Assert.Equal("acme", form.ProviderChoices[^1]);
        view.Apply();

        Assert.Equal("acme", form.Provider);
        Assert.Contains(form.ApplyTo(document), e => e.Contains("'acme'", StringComparison.Ordinal));
        Assert.Equal("acme", document.GetString(DecisionSection.ProviderPath));

        // Another row picked: the name goes, and the choice is written.
        form.SelectProvider(1);
        Assert.Empty(form.ApplyTo(document));
        Assert.Equal("typesafe", document.GetString(DecisionSection.ProviderPath));

        // A known name gets no extra row.
        form.LoadFrom(document);
        Assert.Null(form.ProviderAsWritten);
        Assert.Equal(DecisionForm.StandardChoices, form.ProviderChoices);
    }

    [Fact]
    public void Unticking_a_section_removes_it_whole_and_a_null_section_is_kept_as_written()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Liquid":{"ApiKeyEnvVar":"MY_KEY","TimeoutSeconds":60},"TypeSafe":null}}""");
        var form = Form();
        form.LoadFrom(document);
        var typesafe = form.Providers.Single(p => p.Descriptor.Key == "typesafe");
        Assert.True(typesafe.SetUp);

        form.Providers.Single(p => p.Descriptor.Key == "liquid").SetUp = false;
        form.SelectProvider(1);

        Assert.Empty(form.ApplyTo(document));
        Assert.False(document.ContainsPath("Orkeon:Liquid"));
        Assert.True(document.Root["Orkeon"]!.AsObject().ContainsKey("TypeSafe"));
        Assert.Null(document.GetNode("Orkeon:TypeSafe"));
        Assert.Contains("every key in it", DecisionForm.SectionsNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_in_another_letter_case_is_edited_and_removed_where_the_file_spells_it()
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"liquid":{"Model":"d1"}}}""");
        var form = Form();
        form.LoadFrom(document);
        var liquid = form.Providers.Single(p => p.Descriptor.Key == "liquid");
        Assert.True(liquid.SetUp);
        Assert.Equal("d1", liquid.Model);

        liquid.Model = "d2";
        Assert.Empty(form.ApplyTo(document));
        Assert.Equal("d2", document.GetString("Orkeon:liquid:Model"));
        // No twin: the configuration would read two Model keys as a duplicate and refuse the file.
        Assert.False(document.ContainsPath("Orkeon:Liquid"));

        liquid.SetUp = false;
        Assert.Empty(form.ApplyTo(document));
        Assert.Null(document.GetNode("Orkeon:liquid"));
    }

    [Fact]
    public void The_switch_and_the_provider_in_another_letter_case_are_read_and_written_in_place()
    {
        var document = AppSettingsDocument.Parse("""{"orkeon":{"decision":{"enabled":"maybe","provider":"liquid"}}}""");
        var form = Form();
        form.LoadFrom(document);

        Assert.Equal("liquid", form.Provider);
        Assert.Equal("maybe", form.EnabledAsWritten);
        Assert.NotEmpty(form.ApplyTo(document));

        form.EnabledAsWritten = null;
        form.Enabled = false;
        form.SelectProvider(1);
        Assert.Empty(form.ApplyTo(document));

        var decision = document.Root["orkeon"]!["decision"]!.AsObject();
        Assert.Equal(["enabled", "provider"], decision.Select(p => p.Key));
        Assert.Equal("typesafe", document.GetString("orkeon:decision:provider"));
        Assert.False(document.GetBoolean("orkeon:decision:enabled"));
    }

    [Fact]
    public void The_screen_keeps_the_provider_the_switch_and_the_sections()
    {
        var form = Form();
        using var view = new DecisionSectionView(form);
        var kev = form.Providers.Single(p => p.Descriptor.Key == "kev");

        form.SelectProvider(3);
        form.Enabled = false;
        kev.SetUp = true;
        kev.Model = "kev-latest";
        view.Load();

        form.SelectProvider(0);
        form.Enabled = null;
        kev.SetUp = false;
        kev.Model = "";
        view.Apply();

        Assert.Equal("kev", form.Provider);
        Assert.Equal(false, form.Enabled);
        Assert.True(kev.SetUp);
        Assert.Equal("kev-latest", kev.Model);
    }

    [Fact]
    public void The_editor_lists_the_form()
    {
        var runner = new OrkeonProcessRunner(
            new FakeProcessLauncher(), new OrkeonBinaryLocator(new FakeExecutableProbe(), ["orkeon"]));
        var model = new ConfigEditorModel(new FakeDirectoryProbe("/data/in"), runner, new FakeLlmEndpointProbe());

        Assert.Contains(model.Forms, f => f is DecisionForm);
    }

    [Fact]
    public void The_ladder_and_the_levels_are_typed_as_comma_separated_lists()
    {
        var document = AppSettingsDocument.Parse("""
            {"Llm":{"Model":"small","Profiles":{"strong":{"Model":"big"}}},
             "Orkeon":{"Decision":{"Route":{"Ladder":["default","strong"]}},"Liquid":{}}}
            """);
        var form = Form();
        form.LoadFrom(document);
        Assert.Equal("default, strong", form.RouteLadder);
        Assert.Equal("", form.EffortLevels);

        form.RouteLadder = "";
        form.EffortLevels = " low,medium ";
        Assert.Empty(form.ApplyTo(document));

        Assert.False(document.ContainsPath(DecisionSection.RouteLadderPath));
        Assert.Equal(["low", "medium"], document.Decision.EffortLevels);
    }

    [Theory]
    [InlineData("", "high, low", "cheapest first")]
    [InlineData("", "low, ultra", "is not a thinking level")]
    [InlineData("default", "", "a route needs two or more")]
    [InlineData("default, auto", "", "the name of the route itself")]
    public void A_ladder_or_levels_the_engine_refuses_are_refused_at_apply_and_nothing_is_written(string ladder, string levels, string reason)
    {
        var document = AppSettingsDocument.Parse("""{"Orkeon":{"Liquid":{}}}""");
        var before = document.ToJson();
        var form = Form();
        form.LoadFrom(document);

        form.RouteLadder = ladder;
        form.EffortLevels = levels;
        var errors = form.ApplyTo(document);

        Assert.Contains(errors, error => error.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(before, document.ToJson());
    }
}
