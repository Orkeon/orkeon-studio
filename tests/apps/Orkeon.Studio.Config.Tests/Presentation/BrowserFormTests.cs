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

/// <summary>The profile, the lifetime and the sharing on the browser screen.</summary>
public sealed class BrowserFormProfileLifetimeTests
{
    [Fact]
    public void Load_and_apply_write_the_three_keys_away_from_the_defaults_and_remove_them_back()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var form = new BrowserForm();
        form.LoadFrom(document);
        Assert.Equal("", form.Profile);
        Assert.Null(form.KeepsBrowserOpen);
        Assert.Null(form.SharedInstance);

        form.Profile = "agents";
        form.KeepsBrowserOpen = true;
        form.SharedInstance = true;
        Assert.Empty(form.ApplyTo(document));
        Assert.Equal("agents", document.GetString("Orkeon:Tools:Browser:Profile"));
        Assert.Equal("Host", document.GetString("Orkeon:Tools:Browser:Lifetime"));
        Assert.True(document.GetBoolean("Orkeon:Tools:Browser:SharedInstance"));

        form.LoadFrom(document);
        Assert.True(form.KeepsBrowserOpen);
        form.Profile = " ";
        form.KeepsBrowserOpen = false;
        form.SharedInstance = false;
        Assert.Empty(form.ApplyTo(document));
        Assert.Null(document.GetNode("Orkeon:Tools:Browser"));
    }

    [Fact]
    public void An_unknown_lifetime_is_refused_as_the_run_refuses_it_until_the_switch_replaces_it()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Browser": { "Lifetime": "forever" } } } }""");
        var form = new BrowserForm();
        form.LoadFrom(document);
        Assert.Null(form.KeepsBrowserOpen);
        Assert.Equal("forever", form.LifetimeAsWritten);

        var error = Assert.Single(form.ApplyTo(document));
        Assert.Contains("Orkeon:Tools:Browser:Lifetime", error, StringComparison.Ordinal);
        Assert.DoesNotContain("forever", error, StringComparison.Ordinal);
        Assert.Equal("forever", document.GetString("Orkeon:Tools:Browser:Lifetime"));

        form.LifetimeAsWritten = null;
        form.KeepsBrowserOpen = false;
        Assert.Empty(form.ApplyTo(document));
        Assert.Null(document.GetNode("Orkeon:Tools:Browser"));
    }

    [Theory]
    [InlineData("""{ "Orkeon": { "Tools": { "Browser": { "Profile": "disposable" } } } }""")]
    [InlineData("""{ "Orkeon": { "Tools": { "Browser": { "Profile": "Disposable", "Lifetime": "RUN" } } } }""")]
    [InlineData("""{ "Orkeon": { "Tools": { "Browser": { "Profile": "  agents  ", "Lifetime": "host" } } } }""")]
    [InlineData("""{ "Orkeon": { "Tools": { "Browser": { "Lifetime": "run", "SharedInstance": false } } } }""")]
    [InlineData("""{ "Orkeon": { "Tools": { "Browser": { "SharedInstance": "false" } } } }""")]
    [InlineData("""{ "Orkeon": { "Tools": { "Firefox": { "Profile": "disposable", "Lifetime": "Host", "SharedInstance": true } } } }""")]
    public void An_untouched_form_leaves_the_file_as_it_was(string json)
    {
        var document = AppSettingsDocument.Parse(json);
        var before = document.ToJson();
        var form = new BrowserForm();
        form.LoadFrom(document);

        Assert.Empty(form.ApplyTo(document));
        Assert.Equal(before, document.ToJson());
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("run")]
    public void An_unset_lifetime_switch_removes_the_key(string lifetime)
    {
        var document = AppSettingsDocument.Parse($$"""{ "Orkeon": { "Tools": { "Browser": { "Lifetime": "{{lifetime}}" } } } }""");
        var form = new BrowserForm();
        form.LoadFrom(document);
        Assert.NotNull(form.KeepsBrowserOpen);

        form.KeepsBrowserOpen = null;
        Assert.Empty(form.ApplyTo(document));
        Assert.Null(document.GetNode("Orkeon:Tools:Browser"));
    }

    [Fact]
    public void A_switch_set_off_over_no_key_writes_nothing()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var form = new BrowserForm();
        form.LoadFrom(document);
        form.KeepsBrowserOpen = false;
        form.SharedInstance = false;

        Assert.Empty(form.ApplyTo(document));
        Assert.Null(document.GetNode("Orkeon:Tools:Browser"));
    }

    [Theory]
    [InlineData("SharedInstance")]
    [InlineData("Headless")]
    public void An_unreadable_switch_cleared_to_unset_removes_the_value(string key)
    {
        var document = AppSettingsDocument.Parse($$"""{ "Orkeon": { "Tools": { "Browser": { "{{key}}": "yes", "Engine": "edge" } } } }""");
        var form = new BrowserForm();
        form.LoadFrom(document);

        form.SharedInstanceAsWritten = null;
        form.HeadlessAsWritten = null;
        Assert.Empty(form.ApplyTo(document));
        Assert.Null(document.GetNode($"Orkeon:Tools:Browser:{key}"));
        Assert.Equal("edge", document.GetString("Orkeon:Tools:Browser:Engine"));
    }

    [Fact]
    public void Under_a_former_section_a_change_renames_it_and_the_errors_then_name_the_new_one()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Firefox": { "Profile": "  agents " } } } }""");
        var form = new BrowserForm();
        form.LoadFrom(document);
        Assert.Equal("agents", form.Profile);

        form.SharedInstance = true;
        Assert.Empty(form.ApplyTo(document));
        Assert.Null(document.GetNode("Orkeon:Tools:Firefox"));
        // Moved as written: the run trims it, so the form does not rewrite it.
        Assert.Equal("  agents ", document.GetString("Orkeon:Tools:Browser:Profile"));
        Assert.False(form.UsesLegacySection);

        form.SharedInstanceAsWritten = "yes";
        Assert.StartsWith("Orkeon:Tools:Browser:SharedInstance", Assert.Single(form.ApplyTo(document)), StringComparison.Ordinal);
    }

    [Fact]
    public void After_an_apply_the_profile_shows_as_the_file_keeps_it()
    {
        var document = AppSettingsDocument.CreateEmpty();
        var form = new BrowserForm();
        form.LoadFrom(document);
        form.Profile = "  agents  ";

        Assert.Empty(form.ApplyTo(document));
        Assert.Equal("agents", form.Profile);
    }

    [Fact]
    public void A_shared_switch_that_reads_as_no_boolean_is_refused_until_set()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Tools": { "Browser": { "SharedInstance": "yes" } } } }""");
        var form = new BrowserForm();
        form.LoadFrom(document);
        Assert.Equal("yes", form.SharedInstanceAsWritten);

        Assert.Contains("SharedInstance", Assert.Single(form.ApplyTo(document)), StringComparison.Ordinal);

        form.SharedInstanceAsWritten = null;
        form.SharedInstance = true;
        Assert.Empty(form.ApplyTo(document));
        Assert.True(document.GetBoolean("Orkeon:Tools:Browser:SharedInstance"));
    }
}
