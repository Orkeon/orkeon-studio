using System.Drawing;
using Orkeon.Studio.Config.Presentation;
using Orkeon.Studio.Config.Views;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Tests.Views;

/// <summary>
/// The section screens themselves: each row of the two screens that are lists of text fields reads and
/// writes its own property of the form, and a screen taller than the terminal scrolls to the field in use.
/// </summary>
public sealed class SectionViewTests
{
    private static List<TextField> Fields(SectionView view) => [.. view.SubViews.OfType<TextField>()];

    [Fact]
    public void Each_row_of_the_context_screen_reads_and_writes_its_own_field()
    {
        var form = new ContextForm
        {
            CompactionEnabled = "c-enabled", TriggerShare = "c-trigger", KeepRecentTurns = "c-turns", Profile = "c-profile",
            ContextWindowTokens = "c-window", PruneEnabled = "p-enabled", PruneFillShare = "p-fill",
            PruneKeepRecentMessages = "p-messages", PruneHeadChars = "p-head", SkillsLibrary = "s-library",
            MaxSkillChars = "s-skill", MaxBlockChars = "s-block",
        };
        using var view = new ContextSectionView(form);

        view.Load();
        Assert.Equal(
            ["c-enabled", "c-trigger", "c-turns", "c-profile", "c-window", "p-enabled", "p-fill", "p-messages", "p-head", "s-library", "s-skill", "s-block"],
            Fields(view).Select(field => field.Text));

        var fields = Fields(view);
        for (var index = 0; index < fields.Count; index++)
            fields[index].Text = "edited-" + index;
        view.Apply();

        Assert.Equal(
            Enumerable.Range(0, 12).Select(index => "edited-" + index),
            [
                form.CompactionEnabled, form.TriggerShare, form.KeepRecentTurns, form.Profile, form.ContextWindowTokens,
                form.PruneEnabled, form.PruneFillShare, form.PruneKeepRecentMessages, form.PruneHeadChars,
                form.SkillsLibrary, form.MaxSkillChars, form.MaxBlockChars,
            ]);
    }

    [Fact]
    public void Each_row_of_the_bridge_screen_reads_and_writes_its_own_field()
    {
        var form = new BridgeForm(_ => null)
        {
            Address = "a-address", Allow = "a-allow", Deny = "a-deny", TokenEnvVar = "a-token", HostHeader = "a-host",
            ServePrefix = "s-prefix", ServeAllow = "s-allow", ServeDeny = "s-deny", ServeTokenEnvVar = "s-token",
        };
        using var view = new BridgeSectionView(form);

        view.Load();
        Assert.Equal(
            ["a-address", "a-allow", "a-deny", "a-token", "a-host", "s-prefix", "s-allow", "s-deny", "s-token"],
            Fields(view).Select(field => field.Text));

        var fields = Fields(view);
        for (var index = 0; index < fields.Count; index++)
            fields[index].Text = "edited-" + index;
        view.Apply();

        Assert.Equal(
            Enumerable.Range(0, 9).Select(index => "edited-" + index),
            [
                form.Address, form.Allow, form.Deny, form.TokenEnvVar, form.HostHeader,
                form.ServePrefix, form.ServeAllow, form.ServeDeny, form.ServeTokenEnvVar,
            ]);
    }

    /// <summary>
    /// A screen has more rows than a small terminal shows: its content is as tall as its rows, and the
    /// part shown moves to the field in use, down and back up.
    /// </summary>
    [Fact]
    public void A_screen_taller_than_the_terminal_scrolls_to_the_field_in_use()
    {
        using var view = new ContextSectionView(new ContextForm());
        view.Layout(new Size(80, 12));

        Assert.True(view.ContentRows > view.Viewport.Height, $"{view.ContentRows} rows in {view.Viewport.Height}");
        Assert.Equal(view.ContentRows, view.GetContentSize().Height);
        Assert.Equal(0, view.Viewport.Y);

        var fields = Fields(view);
        view.BringIntoView(fields[^1]);
        Assert.True(view.Viewport.Y > 0);
        Assert.True(fields[^1].Frame.Bottom <= view.Viewport.Y + view.Viewport.Height);

        view.BringIntoView(fields[0]);
        Assert.Equal(fields[0].Frame.Y, view.Viewport.Y);
    }

    /// <summary>A screen whose rows fit is not scrolled: its content is the frame's own height.</summary>
    [Fact]
    public void A_screen_that_fits_does_not_scroll()
    {
        using var view = new BridgeSectionView(new BridgeForm(_ => null));
        view.Layout(new Size(100, 40));

        Assert.True(view.ContentRows <= view.Viewport.Height);
        Assert.Equal(view.Viewport.Height, view.GetContentSize().Height);
    }
}
