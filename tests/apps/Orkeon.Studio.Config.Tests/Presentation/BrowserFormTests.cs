using Orkeon.Studio.Config.Presentation;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Config.Tests.Presentation;

/// <summary>Orkeon #440 — the browser screen: the engine from a closed list, the window, the former section.</summary>
public sealed class BrowserFormTests
{
    [Fact]
    public void Load_and_apply_write_the_engine_and_the_window_away_from_the_defaults()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var form = new BrowserForm();
        form.LoadFrom(document);
        Assert.Equal("firefox", form.Engine);
        Assert.Equal(0, form.EngineChoiceIndex);

        form.SelectEngine(1);
        form.Headless = true;

        Assert.Empty(form.ApplyTo(document));
        Assert.Equal("chrome", document.GetString("Orkeon:Tools:Browser:Engine"));
        Assert.True(document.GetBoolean("Orkeon:Tools:Browser:Headless"));
        Assert.Empty(form.ApplyTo(document));
    }

    [Fact]
    public void Safari_without_a_window_is_refused_and_nothing_is_written()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var form = new BrowserForm();
        form.LoadFrom(document);
        form.Engine = "safari";
        form.Headless = true;

        Assert.Contains("Safari", Assert.Single(form.ApplyTo(document)), StringComparison.Ordinal);
        Assert.Null(document.GetNode("Orkeon:Tools:Browser"));
    }

    [Fact]
    public void An_engine_the_runtime_does_not_know_is_kept_as_written_among_the_choices()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Browser": { "Engine": "opera" } } } }""");
        var form = new BrowserForm();
        form.LoadFrom(document);

        Assert.Equal(["firefox", "chrome", "edge", "brave", "safari", "opera"], form.EngineChoices);
        Assert.Equal(5, form.EngineChoiceIndex);
        Assert.Empty(form.ApplyTo(document));
        Assert.Equal("opera", document.GetString("Orkeon:Tools:Browser:Engine"));
    }

    [Fact]
    public void A_former_section_is_said_and_renamed_by_a_value_written()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Firefox": { "ProfilePath": "/p" } } } }""");
        var form = new BrowserForm();
        form.LoadFrom(document);
        Assert.True(form.UsesLegacySection);

        Assert.Empty(form.ApplyTo(document));
        Assert.True(form.UsesLegacySection);

        form.Engine = "edge";
        Assert.Empty(form.ApplyTo(document));
        Assert.False(form.UsesLegacySection);
        Assert.Equal("/p", document.GetString("Orkeon:Tools:Browser:ProfilePath"));
        Assert.Null(document.GetNode("Orkeon:Tools:Firefox"));
    }

    [Fact]
    public void An_engine_written_in_another_case_and_a_second_apply_leave_the_file_as_it_was()
    {
        const string Json = """{ "Orkeon": { "Tools": { "Browser": { "Engine": "Chrome", "Headless": true } } } }""";
        var document = AppSettingsDocument.Parse(Json);
        var form = new BrowserForm();
        form.LoadFrom(document);
        Assert.Equal("chrome", form.Engine);
        var before = document.ToJson();

        Assert.Empty(form.ApplyTo(document));
        Assert.Empty(form.ApplyTo(document));

        Assert.Equal(before, document.ToJson());
        Assert.Equal("Chrome", document.GetString("Orkeon:Tools:Browser:Engine"));
    }

    [Fact]
    public void An_unreadable_switch_is_refused_on_the_path_the_file_holds()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Firefox": { "Headless": "yes" } } } }""");
        var form = new BrowserForm();
        form.LoadFrom(document);
        Assert.Equal("yes", form.HeadlessAsWritten);

        Assert.Contains("Orkeon:Tools:Firefox:Headless", Assert.Single(form.ApplyTo(document)), StringComparison.Ordinal);
        Assert.Equal("yes", document.GetString("Orkeon:Tools:Firefox:Headless"));
    }

    [Fact]
    public void The_engine_of_a_former_section_is_not_carried_into_the_new_one()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Firefox": { "Engine": "safari", "Headless": true } } } }""");
        var form = new BrowserForm();
        form.LoadFrom(document);
        Assert.Equal("firefox", form.Engine);
        Assert.True(form.Headless);

        form.SelectEngine(1);
        Assert.Empty(form.ApplyTo(document));

        Assert.Equal("chrome", document.GetString("Orkeon:Tools:Browser:Engine"));
        Assert.True(document.GetBoolean("Orkeon:Tools:Browser:Headless"));
        Assert.Null(document.GetNode("Orkeon:Tools:Firefox"));
    }
}
