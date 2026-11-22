using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Orkeon.Studio.Config.Views;

/// <summary>
/// The one place the section screens agree on shape: caption column, field column, and
/// the three-state check box that mirrors an optional setting (unset / on / off).
/// </summary>
internal static class FormLayout
{
    /// <summary>Width of the caption column, in cells.</summary>
    public const int CaptionWidth = 30;

    /// <summary>The hot-key marker of a view that has none: a rune no text holds, so every underscore shows.</summary>
    public static readonly System.Text.Rune NoHotKey = new('\uffff');

    /// <summary>Left margin of every row.</summary>
    public const int Margin = 1;

    /// <summary>
    /// Adds a full-width line of fixed text (a heading or a hint). The label is owned by
    /// <paramref name="parent"/> and never read back, so no reference is handed out.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000",
        Justification = "Ownership transfers to the parent view: Terminal.Gui disposes the subviews " +
                        "it was given when the parent is disposed, which is why no reference is returned.")]
    public static void AddNote(View parent, int y, string text)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var label = new Label
        {
            X = Margin,
            Y = y,
            Width = Dim.Fill(Margin),
            // No hot key in a line of text: an underscore is part of a variable's name, and shows.
            HotKeySpecifier = NoHotKey,
            Text = text,
        };

        parent.Add(label);
    }

    /// <summary>
    /// The cells a line of text gets on a section screen in a terminal of 80 columns: what is left of
    /// them once the window's border, the section list, the screen's own border, its scroll bar and
    /// the two margins are taken.
    /// </summary>
    public const int NarrowWidth = 43;

    /// <summary>
    /// Adds a line of fixed text that takes one row and no more: what does not fit is cut, never
    /// wrapped onto the row below, where a field sits. Its text is written to fit
    /// <see cref="NarrowWidth"/>. The label is owned by <paramref name="parent"/> and never read back.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000",
        Justification = "Ownership transfers to the parent view: Terminal.Gui disposes the subviews " +
                        "it was given when the parent is disposed, which is why no reference is returned.")]
    public static void AddLine(View parent, int y, string text)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var label = new Label
        {
            X = Margin,
            Y = y,
            Width = Dim.Fill(Margin),
            Height = 1,
            // No hot key in a line of text: an underscore is part of a variable's name, and shows.
            HotKeySpecifier = NoHotKey,
            Text = text,
        };

        parent.Add(label);
    }

    /// <summary>
    /// Adds a line of text that takes one row and no more, as <see cref="AddLine"/> does, and whose
    /// content changes as the form is edited (<see cref="SetLine"/>): a name of the file it shows may be
    /// of any length, so what does not fit the row is cut on purpose, its end replaced by an ellipsis
    /// (<see cref="Fit"/>), again at each layout of the screen (<see cref="Refit"/>).
    /// </summary>
    public static Label AddChangingLine(View parent, int y, string text)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var label = new Label
        {
            X = Margin,
            Y = y,
            Width = Dim.Fill(Margin),
            Height = 1,
            // No hot key in a line of text: an underscore is part of a variable's name, and shows.
            HotKeySpecifier = NoHotKey,
        };

        parent.Add(label);
        SetLine(label, text);
        return label;
    }

    /// <summary>What ends a text cut to fit its row.</summary>
    public const string Ellipsis = "\u2026";

    /// <summary>
    /// <paramref name="text"/> as it fits <paramref name="width"/> cells: whole when it does, else its
    /// start and an ellipsis. Whole too while the width is not known yet (a view not laid out).
    /// </summary>
    public static string Fit(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        return width <= 0 || text.Length <= width ? text : string.Concat(text.AsSpan(0, width - 1), Ellipsis);
    }

    // What a view of a screen shows in full, kept beside it: the view itself holds what fits its row.
    private sealed class Whole
    {
        public string? Line { get; set; }

        public IReadOnlyList<string>? Items { get; set; }

        public (string Caption, Func<string?> AsWritten)? Switch { get; set; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<View, Whole> Wholes = [];

    /// <summary>Sets the text of a line made by <see cref="AddChangingLine"/>: shown as it fits the row.</summary>
    public static void SetLine(Label line, string text)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(text);
        Wholes.GetOrCreateValue(line).Line = text;
        Show(line, Fit(text, line.Viewport.Width));
    }

    /// <summary>The whole text of a line made by <see cref="AddChangingLine"/>, whatever the row shows of it.</summary>
    public static string LineOf(Label line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return Wholes.TryGetValue(line, out var whole) && whole.Line is { } text ? text : line.Text;
    }

    /// <summary>
    /// Fits again, to the width each now has, what the views of <paramref name="parent"/> show of a
    /// text that may be longer than its row: the lines of <see cref="AddChangingLine"/>, the items of
    /// <see cref="AddChoiceListBelow"/> and the captions of <see cref="TrackUnreadable"/>. A screen
    /// calls it once its views are laid out; what already fits is left alone.
    /// </summary>
    public static void Refit(View parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        foreach (var view in parent.SubViews)
        {
            if (!Wholes.TryGetValue(view, out var whole))
                continue;

            if (whole.Line is { } line)
                Show(view, Fit(line, view.Viewport.Width));
            if (whole.Switch is { } tracked && view.Frame.Height == 1)
                Show(view, FitSwitchCaption(tracked.Caption, tracked.AsWritten(), view.Viewport.Width - SwitchMarkWidth));
            if (whole.Items is { } items && view is ListView list)
                ShowItems(list, items, list.SelectedItem ?? 0);
        }
    }

    // The cells a check box takes before its caption: its mark and a space.
    private const int SwitchMarkWidth = 2;

    private static void Show(View view, string text)
    {
        if (!string.Equals(view.Text, text, StringComparison.Ordinal))
            view.Text = text;
    }

    private static void ShowItems(ListView list, IReadOnlyList<string> items, int selectedIndex)
    {
        var width = list.Viewport.Width;
        var fitted = items.Select(item => Fit(item, width)).ToList();
        if (list.Source?.ToList() is { } shown && shown.Cast<object?>().Select(item => item?.ToString()).SequenceEqual(fitted, StringComparer.Ordinal))
            return;

        list.SetSource(new ObservableCollection<string>(fitted));
        if (fitted.Count > 0)
            list.SelectedItem = Math.Clamp(selectedIndex, 0, fitted.Count - 1);
    }

    /// <summary>A caption that says the engine's default beside it: what an empty field stands for.</summary>
    public static string CaptionWithDefault(string caption, object engineDefault) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{caption} [{engineDefault}]");

    /// <summary>Adds a full-width line of text whose content changes as the form is edited.</summary>
    public static Label AddText(View parent, int y, string text)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var label = new Label
        {
            X = Margin,
            Y = y,
            Width = Dim.Fill(Margin),
            // No hot key in a line of text: an underscore is part of a variable's name, and shows.
            HotKeySpecifier = NoHotKey,
            Text = text,
        };

        parent.Add(label);
        return label;
    }

    /// <summary>Adds a captioned text field and returns the field.</summary>
    public static TextField AddField(View parent, int y, string caption, string value, bool secret = false)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var label = new Label { X = Margin, Y = y, Text = caption };
        var field = new TextField
        {
            X = Margin + CaptionWidth,
            Y = y,
            Width = Dim.Fill(Margin),
            Text = value,
            Secret = secret,
        };

        parent.Add(label, field);
        return field;
    }

    /// <summary>
    /// Adds a check box bound to an optional boolean: no check state means "no key in the
    /// file", which is not the same thing as an explicit <c>false</c>.
    /// </summary>
    public static CheckBox AddOptionalSwitch(View parent, int y, string caption, bool? value)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var checkBox = new CheckBox
        {
            X = Margin,
            Y = y,
            Width = Dim.Fill(Margin),
            // One row and no more: a caption that says what the file holds is fitted to it (Refit),
            // never wrapped onto the row below, where another switch or a field sits.
            Height = 1,
            Text = caption,
            AllowCheckStateNone = true,
            Value = ToCheckState(value),
        };

        parent.Add(checkBox);
        return checkBox;
    }

    /// <summary>Adds a captioned closed list and returns the list view.</summary>
    public static ListView AddChoiceList(
        View parent,
        int y,
        int height,
        string caption,
        IReadOnlyList<string> choices,
        int selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(choices);

        var label = new Label { X = Margin, Y = y, Text = caption };
        var list = new ListView
        {
            X = Margin + CaptionWidth,
            Y = y,
            Width = Dim.Fill(Margin),
            Height = height,
        };

        list.SetSource(new ObservableCollection<string>(choices));
        if (choices.Count > 0)
            list.SelectedItem = Math.Clamp(selectedIndex, 0, choices.Count - 1);

        parent.Add(label, list);
        return list;
    }

    /// <summary>
    /// Adds a closed list under its caption, on the whole width of the screen: beside its caption, in
    /// the field column, a list of a terminal of 80 columns is 13 cells wide and cuts its rows. An
    /// item longer than the row all the same (a name of the file) is cut on purpose, with an ellipsis
    /// (<see cref="Fit"/>). Takes <paramref name="height"/> rows and one more for the caption.
    /// </summary>
    public static ListView AddChoiceListBelow(
        View parent,
        int y,
        int height,
        string caption,
        IReadOnlyList<string> choices,
        int selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(choices);

        AddLine(parent, y, caption);
        var list = new ListView
        {
            X = Margin,
            Y = y + 1,
            Width = Dim.Fill(Margin),
            Height = height,
        };

        parent.Add(list);
        Wholes.GetOrCreateValue(list).Items = [.. choices];
        ShowItems(list, choices, selectedIndex);
        return list;
    }

    /// <summary>
    /// Replaces the contents of a list, keeping the selection in range; the items of a list made by
    /// <see cref="AddChoiceListBelow"/> are shown as they fit its rows.
    /// </summary>
    public static void SetItems(ListView list, IReadOnlyList<string> items, int selectedIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(items);

        if (Wholes.TryGetValue(list, out var whole) && whole.Items is not null)
        {
            whole.Items = [.. items];
            list.SetSource(new ObservableCollection<string>(items.Select(item => Fit(item, list.Viewport.Width))));
        }
        else
        {
            list.SetSource(new ObservableCollection<string>(items));
        }

        if (items.Count > 0)
            list.SelectedItem = Math.Clamp(selectedIndex, 0, items.Count - 1);
    }

    /// <summary>The selected index of a list, or -1 when nothing is selected.</summary>
    public static int SelectedIndex(ListView list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list.SelectedItem ?? -1;
    }

    /// <summary>
    /// A switch's caption, saying beside it that the file holds a text that reads as no boolean
    /// (STUDIO-55): the switch then shows « unset », and applying refuses the value until the switch
    /// changes state. The text itself is not shown (Orkeon #465): not of the key's type, it may be
    /// anything written there by mistake, a key included.
    /// </summary>
    public static string SwitchCaption(string caption, string? asWritten) =>
        asWritten is null ? caption : caption + UnreadableSwitchSuffix;

    /// <summary>
    /// <see cref="SwitchCaption"/> as it fits <paramref name="width"/> cells on one row: whole when it
    /// does. Else the caption gives way first, cut with an ellipsis, and the remark that the file holds
    /// no boolean is kept in sight, shorter (« : not true or false »).
    /// </summary>
    public static string FitSwitchCaption(string caption, string? asWritten, int width)
    {
        ArgumentNullException.ThrowIfNull(caption);
        var whole = SwitchCaption(caption, asWritten);
        if (asWritten is null || width <= 0 || whole.Length <= width)
            return Fit(whole, width);

        const int LeastCaption = 8;
        const string Said = " \u2014 not true or false";
        return Fit(Fit(caption, Math.Max(LeastCaption, width - Said.Length)) + Said, width);
    }

    private const string UnreadableSwitchSuffix = " \u2014 the file holds a value that is not true or false";

    /// <summary>
    /// Wires a switch to the unreadable text its form keeps (STUDIO-55): the caption says it, and the
    /// first change of state clears it through <paramref name="clear"/> — the new state, « unset »
    /// included, then replaces the value. On a switch of one row the caption is fitted to it at each
    /// layout (<see cref="Refit"/>, <see cref="FitSwitchCaption"/>).
    /// </summary>
    public static void TrackUnreadable(CheckBox checkBox, string caption, Func<string?> asWritten, Action clear)
    {
        ArgumentNullException.ThrowIfNull(checkBox);
        ArgumentNullException.ThrowIfNull(asWritten);
        ArgumentNullException.ThrowIfNull(clear);

        Wholes.GetOrCreateValue(checkBox).Switch = (caption, asWritten);
        checkBox.Text = SwitchCaption(caption, asWritten());
        checkBox.ValueChanged += (_, _) =>
        {
            if (asWritten() is null)
                return;
            clear();
            checkBox.Text = caption;
        };
    }

    /// <summary>Maps an optional boolean onto a check state.</summary>
    public static CheckState ToCheckState(bool? value) => value switch
    {
        true => CheckState.Checked,
        false => CheckState.UnChecked,
        null => CheckState.None,
    };

    /// <summary>Maps a check state back onto an optional boolean.</summary>
    public static bool? ToBoolean(CheckState state) => state switch
    {
        CheckState.Checked => true,
        CheckState.UnChecked => false,
        _ => null,
    };
}
