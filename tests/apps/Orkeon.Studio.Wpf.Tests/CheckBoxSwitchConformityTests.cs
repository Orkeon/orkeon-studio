using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// #22: every CheckBox of Studio is a switch. The implicit CheckBox style of the theme is based on
/// <c>Switch</c>, no other style targets a CheckBox outside the theme, and no XAML sets a style on
/// a CheckBox, no CheckBox draws its own template, and no ToggleButton template draws a tick, so a
/// box to tick cannot come back. The XAML is read as XML from the source tree, no WPF involved, so the
/// suite stays on the Linux runner.
/// </summary>
public sealed class CheckBoxSwitchConformityTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string WpfSourceRoot([CallerFilePath] string thisFile = "")
    {
        var testsDir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(testsDir, "..", "..", "..", "src", "apps", "Orkeon.Studio.Wpf"));
    }

    private static string StudioTheme => Path.Combine(WpfSourceRoot(), "Themes", "Studio.xaml");

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(WpfSourceRoot(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static bool TargetsCheckBox(XElement style) =>
        (string?)style.Attribute("TargetType") is "CheckBox" or "{x:Type CheckBox}";

    private static IEnumerable<XElement> CheckBoxStyles(XElement root) =>
        root.Descendants().Where(e => e.Name.LocalName == "Style" && TargetsCheckBox(e));

    [Fact]
    public void The_implicit_CheckBox_style_is_the_switch()
    {
        var root = XDocument.Load(StudioTheme).Root!;
        var implicitStyle = Assert.Single(CheckBoxStyles(root), style => style.Attribute(X + "Key") is null);

        Assert.Equal("{StaticResource Switch}", (string?)implicitStyle.Attribute("BasedOn"));
        var keyed = CheckBoxStyles(root).Select(style => (string?)style.Attribute(X + "Key")).Where(key => key is not null);
        Assert.Contains("Switch", keyed);
    }

    [Fact]
    public void No_style_targets_a_CheckBox_outside_the_theme()
    {
        var offenders = XamlFiles()
            .Where(file => !string.Equals(Path.GetFullPath(file), StudioTheme, StringComparison.Ordinal))
            .Where(file => CheckBoxStyles(XDocument.Load(file).Root!).Any())
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "A CheckBox style outside Themes/Studio.xaml: " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_xaml_sets_a_style_on_a_CheckBox()
    {
        var offenders = XamlFiles()
            .SelectMany(file => XDocument.Load(file, LoadOptions.SetLineInfo).Root!.Descendants()
                .Where(e => e.Name.LocalName == "CheckBox"
                         && (e.Attribute("Style") is not null || e.Elements().Any(child => child.Name.LocalName == "CheckBox.Style")))
                .Select(e => $"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)e).LineNumber}"))
            .ToList();

        Assert.True(offenders.Count == 0, "A CheckBox that sets its own style (every CheckBox is a switch, #22): " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_CheckBox_draws_its_own_template()
    {
        var offenders = XamlFiles()
            .SelectMany(file => XDocument.Load(file, LoadOptions.SetLineInfo).Root!.Descendants()
                .Where(e => e.Name.LocalName == "CheckBox"
                         && (e.Attribute("Template") is not null || e.Elements().Any(child => child.Name.LocalName == "CheckBox.Template")))
                .Select(e => Where(file, e)))
            .ToList();

        Assert.True(offenders.Count == 0, "A CheckBox with its own template (every CheckBox is a switch, #22): " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_ToggleButton_template_draws_a_tick()
    {
        // A row to pick wears the switch (SwitchRow), never a box with a check mark (#22). A template
        // drawing a check is the template of a ToggleButton, inline or keyed in a theme.
        var offenders = XamlFiles()
            .SelectMany(file => XDocument.Load(file, LoadOptions.SetLineInfo).Root!.Descendants()
                .Where(IsToggleButtonTemplate)
                .SelectMany(template => template.Descendants().Where(IsTick))
                .Select(tick => Where(file, tick)))
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, "A ToggleButton template that draws a tick (a row is a switch, #22): " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_SwitchRow_style_draws_the_switch_track_and_knob()
    {
        var root = XDocument.Load(StudioTheme).Root!;
        var row = Assert.Single(root.Descendants(), e => e.Name.LocalName == "Style" && (string?)e.Attribute(X + "Key") == "SwitchRow");

        Assert.Equal("ToggleButton", (string?)row.Attribute("TargetType"));
        var names = row.Descendants().Select(e => (string?)e.Attribute(X + "Name")).ToList();
        Assert.Contains("track", names);
        Assert.Contains("knob", names);
        Assert.DoesNotContain(row.Descendants(), IsTick);
    }

    [Fact]
    public void A_hint_right_under_a_switch_starts_at_its_label()
    {
        // The switch's label sits 44 px in (34 px track + 10 px gap); a hint indented under it takes
        // the SwitchHint style, never the plain Hint with the 24 px indent of the former box (#22).
        // A hint flush with the switch (left margin 0) is another layout and stays allowed.
        var root = XDocument.Load(StudioTheme).Root!;
        var hint = Assert.Single(root.Descendants(), e => e.Name.LocalName == "Style" && (string?)e.Attribute(X + "Key") == "SwitchHint");
        Assert.Contains(hint.Elements(), setter => (string?)setter.Attribute("Property") == "Margin" && (string?)setter.Attribute("Value") == "44,3,0,0");

        var offenders = XamlFiles()
            .SelectMany(file => XDocument.Load(file, LoadOptions.SetLineInfo).Root!.Descendants()
                .Where(e => e.Name.LocalName == "CheckBox")
                .Select(box => box.ElementsAfterSelf().FirstOrDefault())
                .Where(next => next is not null && next.Name.LocalName == "TextBlock"
                            && (string?)next.Attribute("Style") == "{StaticResource Hint}"
                            && IndentedLeft((string?)next.Attribute("Margin")))
                .Select(next => Where(file, next!)))
            .ToList();

        Assert.True(offenders.Count == 0, "A hint indented under a switch without the SwitchHint style: " + string.Join(", ", offenders));
    }

    private static bool IndentedLeft(string? margin) =>
        margin is not null
        && double.TryParse(margin.Split(',')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var left)
        && left > 0;

    private static bool IsToggleButtonTemplate(XElement element) =>
        element.Name.LocalName == "ToggleButton.Template"
        || (element.Name.LocalName == "ControlTemplate"
            && (string?)element.Attribute("TargetType") is "ToggleButton" or "{x:Type ToggleButton}");

    private static bool IsTick(XElement element) =>
        element.Name.LocalName == "LucideIcon"
        && ((string?)element.Attribute("Kind") is "check" or "square-check" or "check-square"
            || (string?)element.Attribute(X + "Name") == "Tick");

    private static string Where(string file, XElement element) =>
        $"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber}";
}
