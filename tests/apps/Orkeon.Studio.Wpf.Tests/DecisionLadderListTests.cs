using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Profiles;
using Orkeon.Studio.Wpf.Tests.Doubles;
using Orkeon.Studio.Wpf.ViewModels.Config;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// The decision ladder as a list: each rung is a row named by its setting, added from the profiles
/// the settings know, moved and removed in place, and always the ladder the text spells; the
/// compaction's profile is picked from the same profiles.
/// </summary>
public sealed class DecisionLadderListTests
{
    private static readonly ModelProfileSet TwoModels = ModelProfileSet.Empty.UpsertConnection(new ModelConnection
    {
        Name = "mydeepseek",
        Provider = "DeepSeek",
        BaseUrl = "https://api.deepseek.com",
        Models = [new ConnectionModel { Alias = "flash", Model = "deepseek-v4-flash" }, new ConnectionModel { Alias = "pro", Model = "deepseek-v4-pro" }],
    }).Set;

    private static (DecisionSectionViewModel Form, AppSettingsDocument Document) Form(string json = "{}")
    {
        var document = AppSettingsDocument.Parse(json);
        var form = new DecisionSectionViewModel(() => document, () => { }, keyStore: new FakeApiKeyStore())
        {
            ProfileSet = () => TwoModels,
        };
        return (form, document);
    }

    [Fact]
    public void The_choices_are_the_default_then_each_model_by_its_setting_name_then_what_the_file_defines()
    {
        var (form, _) = Form("""{ "Llm": { "Profiles": { "local-gpu": { "Model": "qwen3" } } } }""");

        Assert.Equal(["default", "mydeepseek-flash", "mydeepseek-pro", "local-gpu"], form.RungChoices.Select(c => c.Id));
        Assert.Equal(["default", "mydeepseek.flash", "mydeepseek.pro", "local-gpu"], form.RungChoices.Select(c => c.Label));
        Assert.True(form.HasNoRung);
    }

    [Fact]
    public void Adding_moving_and_removing_rungs_writes_the_ladder_the_text_spells()
    {
        var (form, document) = Form();

        form.RungToAdd = form.RungChoices.Single(c => c.Label == "mydeepseek.pro");
        form.AddRungCommand.Execute(null);
        form.RungToAdd = form.RungChoices.Single(c => c.Label == "mydeepseek.flash");
        form.AddRungCommand.Execute(null);

        Assert.Equal(["mydeepseek-pro", "mydeepseek-flash"], document.Decision.RouteLadder);
        Assert.DoesNotContain(form.RungChoices, c => c.Id == "mydeepseek-pro");
        Assert.Null(form.RungToAdd);
        Assert.False(form.AddRungCommand.CanExecute(null));
        Assert.Equal("mydeepseek.pro", form.Rungs[0].Label);
        Assert.True(form.Rungs[0].ShowsId);
        Assert.False(form.Rungs[0].MoveUpCommand.CanExecute(null));
        Assert.False(form.Rungs[1].MoveDownCommand.CanExecute(null));

        form.Rungs[1].MoveUpCommand.Execute(null);
        Assert.Equal("mydeepseek-flash, mydeepseek-pro", form.RouteLadderText);

        form.Rungs[0].RemoveCommand.Execute(null);
        Assert.Equal(["mydeepseek-pro"], document.Decision.RouteLadder);
        form.Rungs[0].RemoveCommand.Execute(null);
        Assert.False(document.ContainsPath(DecisionSection.RouteLadderPath));
        Assert.True(form.HasNoRung);
    }

    [Fact]
    public void A_rung_no_profile_defines_stays_shown_and_marked()
    {
        var (form, _) = Form("""{ "Orkeon": { "Decision": { "Route": { "Ladder": [ "default", "gone" ] } } } }""");

        Assert.Equal(["default", "gone"], form.Rungs.Select(r => r.Label));
        Assert.False(form.Rungs[0].IsUnknown);
        Assert.True(form.Rungs[1].IsUnknown);
        Assert.Contains("gone", form.RouteLadderNote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_typed_ladder_and_the_list_are_the_same_ladder()
    {
        var (form, _) = Form();

        form.RouteLadderText = "mydeepseek-flash, default";

        Assert.Equal(["mydeepseek.flash", "default"], form.Rungs.Select(r => r.Label));
        Assert.Equal(["mydeepseek-pro"], form.RungChoices.Select(c => c.Id));
    }

    [Fact]
    public void The_compaction_picks_its_profile_among_the_same_profiles_and_keeps_an_unknown_one()
    {
        var document = AppSettingsDocument.Parse("""{ "Orkeon": { "Compaction": { "Profile": "elsewhere" } } }""");
        var compaction = new CompactionSectionViewModel(() => document, () => { }) { ProfileSet = () => TwoModels };

        Assert.Equal(["", "default", "mydeepseek-flash", "mydeepseek-pro", "elsewhere"], compaction.ProfileChoices);

        compaction.Profile = "mydeepseek-pro";
        Assert.Equal(["", "default", "mydeepseek-flash", "mydeepseek-pro"], compaction.ProfileChoices);
    }
}
