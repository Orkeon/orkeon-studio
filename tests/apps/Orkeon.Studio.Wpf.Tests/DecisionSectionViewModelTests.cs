using Orkeon.Infrastructure.Decision.SystemOne;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Config;
using Orkeon.Studio.Wpf.ViewModels.Services;
using Orkeon.Studio.Wpf.ViewModels.Shell;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The decision models form (System One): the switch and the provider write in place, each card sets
/// its provider up or removes it, and the status line says what a run of the file gets, resolved as the
/// engine resolves it over the keys in place. A key goes to the store, never to the file.
/// </summary>
public sealed class DecisionSectionViewModelTests
{
    private static (DecisionSectionViewModel Form, AppSettingsDocument Document, FakeApiKeyStore Keys) Form(string json = "{}")
    {
        var document = AppSettingsDocument.Parse(json);
        var keys = new FakeApiKeyStore();
        return (new DecisionSectionViewModel(() => document, () => { }, keyStore: keys), document, keys);
    }

    private static DecisionProviderCardViewModel Card(DecisionSectionViewModel form, DecisionProviderDescriptor descriptor) =>
        form.Providers.Single(card => card.Descriptor == descriptor);

    [Fact]
    public void An_empty_file_has_no_provider_and_the_switch_on()
    {
        var (form, _, _) = Form();

        Assert.True(form.Enabled);
        Assert.Null(form.SelectedProvider.Key);
        Assert.Equal(["TypeSafe", "Liquid", "Kev"], form.Providers.Select(card => card.Name));
        Assert.False(form.IsOn);
        Assert.Contains("No provider is set up", form.StatusText, StringComparison.Ordinal);
        Assert.Equal("Empty", form.TabState);
    }

    [Fact]
    public void The_switch_turned_off_writes_false_and_turned_back_on_removes_its_key()
    {
        var (form, document, _) = Form("""{"Orkeon":{"Liquid":{}}}""");
        Assert.True(form.IsOn);

        form.Enabled = false;

        Assert.False(document.GetBoolean(DecisionSection.EnabledPath));
        Assert.False(form.IsOn);
        Assert.Equal("Off", form.TabSubtitle);

        form.Enabled = true;

        Assert.Null(document.GetNode(DecisionSection.EnabledPath));
    }

    [Fact]
    public void A_provider_set_up_and_named_answers_and_its_fields_write_in_place()
    {
        var (form, document, _) = Form("""{"Orkeon":{"Liquid":{"ApiKey":"kept"}}}""");
        var kev = Card(form, DecisionProviderDescriptor.Kev);
        Assert.False(kev.IsConfigured);

        Card(form, DecisionProviderDescriptor.Liquid).RemoveCommand.Execute(null);
        kev.SetUpCommand.Execute(null);
        // A local server is never deduced: set up alone, it is left aside.
        Assert.False(form.IsOn);
        Assert.Contains("Orkeon:Kev", form.IgnoredText, StringComparison.Ordinal);
        Assert.True(kev.HasHint);
        form.SelectedProvider = form.ProviderChoices.Single(choice => choice.Key == DecisionProviderDescriptor.Kev.Key);
        Assert.False(kev.HasHint);
        kev.Model = " kev-latest ";
        kev.TimeoutSeconds = 0;
        kev.MaxRetries = 5;

        Assert.True(kev.IsConfigured);
        Assert.Null(document.GetNode("Orkeon:Liquid"));
        Assert.Equal("kev-latest", document.GetString("Orkeon:Kev:Model"));
        Assert.Null(document.GetNode("Orkeon:Kev:TimeoutSeconds"));
        Assert.Equal(5, document.GetInt32("Orkeon:Kev:MaxRetries"));
        Assert.True(form.IsOn);
        Assert.Contains("Kev answers the decision tasks.", form.StatusText, StringComparison.Ordinal);
        Assert.Equal("Kev", form.TabSubtitle);
    }

    [Fact]
    public void A_card_on_a_section_in_another_case_shows_writes_and_removes_it_in_place()
    {
        var (form, document, _) = Form("""{"Orkeon":{"typesafe":{"Model":"a"}}}""");
        var typeSafe = Card(form, DecisionProviderDescriptor.TypeSafe);

        Assert.True(typeSafe.IsConfigured);
        Assert.Equal("a", typeSafe.Model);

        typeSafe.Model = "b";
        typeSafe.MaxRetries = 2;

        // No twin Orkeon:TypeSafe:Model: the configuration would refuse the file on the duplicate key.
        Assert.Equal(["typesafe"], document.Root["Orkeon"]!.AsObject().Select(p => p.Key));
        Assert.Equal("b", document.GetString("Orkeon:typesafe:Model"));
        Assert.Equal(2, document.GetInt32("Orkeon:typesafe:MaxRetries"));

        typeSafe.RemoveCommand.Execute(null);

        Assert.False(typeSafe.IsConfigured);
        Assert.Empty(document.Root["Orkeon"]!.AsObject());
    }

    [Fact]
    public void A_provider_name_in_another_case_is_selected_and_replaced_in_place()
    {
        var (form, document, _) = Form("""{"Orkeon":{"Decision":{"provider":"liquid"}}}""");

        Assert.Equal(DecisionProviderDescriptor.Liquid.Key, form.SelectedProvider.Key);
        Assert.True(form.Exists);

        form.SelectedProvider = form.ProviderChoices.Single(choice => choice.Key == DecisionProviderDescriptor.TypeSafe.Key);

        var decision = document.Root["Orkeon"]!["Decision"]!.AsObject();
        Assert.Equal(["provider"], decision.Select(p => p.Key));
        Assert.Equal(DecisionProviderDescriptor.TypeSafe.Key, document.GetString("Orkeon:Decision:provider"));
    }

    [Fact]
    public void The_status_reads_the_settings_the_environment_lays_over_the_file()
    {
        var (form, _, keys) = Form("""{"Orkeon":{"Liquid":{}}}""");
        keys.Stage(DecisionProviderDescriptor.Liquid.DefaultApiKeyVariable, "key");
        form.Refresh();
        Assert.True(form.IsOn);

        keys.Stage("ORKEON_Orkeon__Decision__Enabled", "false");
        form.Refresh();

        Assert.False(form.IsOn);
        Assert.Equal("Off", form.TabSubtitle);
    }

    [Fact]
    public void Two_providers_set_up_are_refused_until_one_is_named()
    {
        var (form, document, _) = Form("""{"Orkeon":{"TypeSafe":{},"Liquid":{}}}""");

        Assert.True(form.IsRefused);
        Assert.Contains("The engine refuses these settings", form.StatusText, StringComparison.Ordinal);
        Assert.Equal("Warning", form.TabState);

        form.SelectedProvider = form.ProviderChoices.Single(choice => choice.Key == DecisionProviderDescriptor.Liquid.Key);

        Assert.Equal(DecisionProviderDescriptor.Liquid.Key, document.GetString(DecisionSection.ProviderPath));
        Assert.False(form.IsRefused);
        Assert.True(form.IsOn);
        Assert.Contains("Liquid answers the decision tasks.", form.StatusText, StringComparison.Ordinal);
        Assert.Contains("Orkeon:TypeSafe", form.IgnoredText, StringComparison.Ordinal);
    }

    [Fact]
    public void Naming_a_provider_sets_its_section_up_and_automatic_removes_the_name()
    {
        var (form, document, _) = Form();

        form.SelectedProvider = form.ProviderChoices.Single(choice => choice.Key == DecisionProviderDescriptor.TypeSafe.Key);

        Assert.True(Card(form, DecisionProviderDescriptor.TypeSafe).IsConfigured);
        Assert.NotNull(document.GetNode("Orkeon:TypeSafe"));

        form.SelectedProvider = form.ProviderChoices[0];

        Assert.Null(document.GetNode(DecisionSection.ProviderPath));
    }

    [Fact]
    public void A_name_the_engine_does_not_know_shows_as_itself()
    {
        var (form, _, _) = Form("""{"Orkeon":{"Decision":{"Provider":"acme"}}}""");

        Assert.Equal("acme", form.SelectedProvider.Label);
        Assert.True(form.IsRefused);
    }

    [Fact]
    public void A_name_the_engine_does_not_know_is_an_entry_of_the_list_until_another_replaces_it()
    {
        var (form, document, _) = Form("""{"Orkeon":{"Decision":{"Provider":"acme"}}}""");

        // The list holds the selection, so the box shows it and hands it back unchanged.
        Assert.Contains(form.SelectedProvider, form.ProviderChoices);
        Assert.Equal("acme", form.ProviderChoices[^1].Label);
        form.SelectedProvider = form.ProviderChoices[^1];
        // A list rebuilt under its selection may hand back nothing: the name stays.
        form.SelectedProvider = null!;
        Assert.Equal("acme", document.GetString(DecisionSection.ProviderPath));

        form.SelectedProvider = form.ProviderChoices.Single(choice => choice.Key == DecisionProviderDescriptor.Kev.Key);

        Assert.Equal(DecisionProviderDescriptor.Kev.Key, document.GetString(DecisionSection.ProviderPath));
        Assert.DoesNotContain(form.ProviderChoices, choice => choice.Key == "acme");
        Assert.Equal(1 + DecisionSection.Providers.Count, form.ProviderChoices.Count);
    }

    [Fact]
    public async Task A_key_remembered_goes_to_the_store_and_turns_the_circuit_on()
    {
        var (form, document, keys) = Form();
        var typeSafe = Card(form, DecisionProviderDescriptor.TypeSafe);
        Assert.Equal(DecisionProviderDescriptor.TypeSafe.DefaultApiKeyVariable, typeSafe.Key.EnvName);

        typeSafe.Key.KeyInput = "secret";
        await typeSafe.Key.StoreCommand.ExecuteAsync(null);

        Assert.Equal("secret", keys.Saved[typeSafe.Key.EnvName]);
        Assert.DoesNotContain("secret", document.ToJson(), StringComparison.Ordinal);
        Assert.True(form.IsOn);
        Assert.Equal("TypeSafe", form.TabSubtitle);
    }

    [Fact]
    public void A_key_written_in_the_file_is_said()
    {
        var (form, _, _) = Form("""{"Orkeon":{"Liquid":{"ApiKey":"in-the-file"}}}""");

        Assert.True(Card(form, DecisionProviderDescriptor.Liquid).HasApiKeyInFile);
        Assert.False(Card(form, DecisionProviderDescriptor.Kev).HasApiKeyInFile);
    }

    [Fact]
    public void Another_file_is_read_again()
    {
        var document = AppSettingsDocument.Parse("{}");
        var form = new DecisionSectionViewModel(() => document, () => { }, keyStore: new FakeApiKeyStore());
        Assert.False(form.IsOn);

        document = AppSettingsDocument.Parse("""{"Orkeon":{"Decision":{"Provider":"kev"},"Kev":{"Model":"kev-2"}}}""");
        form.Refresh();

        Assert.True(form.IsOn);
        Assert.Equal("kev-2", Card(form, DecisionProviderDescriptor.Kev).Model);
    }

    [Fact]
    public void The_settings_screen_shows_the_tab_in_both_modes_and_says_its_state()
    {
        var config = new ConfigTabViewModel(new StudioServices
        {
            SettingsStore = new FakeAppSettingsStore(),
            Directories = new FakeDirectoryProbe(),
            KeyStore = new FakeApiKeyStore(),
        });
        config.SetDocument(AppSettingsDocument.Parse("""{"Orkeon":{"TypeSafe":{},"Liquid":{}}}"""));
        var screen = new SettingsScreenViewModel(
            config, new ModelProfilesViewModel(new InMemoryModelProfileStore(), config.Llm), new UiModeViewModel(UiModeViewModel.Novice));
        var raised = new List<string?>();
        screen.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        screen.ShowDecisionCommand.Execute(null);

        Assert.True(screen.IsDecisionTab);
        Assert.Equal("To fix", screen.DecisionTabSubtitle);
        Assert.Equal("Warning", screen.DecisionTabState);

        config.Decision.SelectedProvider = config.Decision.ProviderChoices.Single(c => c.Key == DecisionProviderDescriptor.TypeSafe.Key);

        Assert.Contains(nameof(SettingsScreenViewModel.DecisionTabSubtitle), raised);
        Assert.Equal("TypeSafe", screen.DecisionTabSubtitle);
        Assert.Equal("Done", screen.DecisionTabState);
        Assert.Equal(DecisionProviderDescriptor.TypeSafe.Key, config.Document.Decision.Provider);
    }

    [Fact]
    public void The_ladder_and_the_levels_are_typed_as_lists_and_remove_their_key_when_emptied()
    {
        var (form, document, _) = Form("""{"Llm":{"Model":"small","Profiles":{"strong":{"Model":"big"}}}}""");
        Assert.Equal("", form.RouteLadderText);

        form.RouteLadderText = "default ,strong";
        form.EffortLevelsText = "low; high";

        Assert.Equal("default, strong", form.RouteLadderText);
        Assert.Equal(["default", "strong"], document.Decision.RouteLadder);
        Assert.Equal(["low", "high"], document.Decision.EffortLevels);
        Assert.Equal("", form.RouteLadderNote);

        form.RouteLadderText = " ";
        form.EffortLevelsText = "";
        Assert.False(document.ContainsPath(DecisionSection.RouteLadderPath));
        Assert.False(document.ContainsPath(DecisionSection.EffortLevelsPath));
    }

    [Fact]
    public void A_rung_that_names_no_profile_is_warned_of_and_a_refused_ladder_is_said_by_the_status()
    {
        var (form, _, _) = Form();

        form.RouteLadderText = "default, strong";
        Assert.Contains("strong", form.RouteLadderNote, StringComparison.Ordinal);
        Assert.False(form.IsRefused);

        form.RouteLadderText = "default";
        Assert.True(form.IsRefused);
        Assert.Contains("a route needs two or more", form.StatusText, StringComparison.Ordinal);
        Assert.Equal("", form.RouteLadderNote);
    }
}
