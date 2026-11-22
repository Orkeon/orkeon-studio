using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Orkeon.Studio.Wpf.Tests;

/// <summary>
/// #22: every CheckBox of Studio is a switch. The implicit CheckBox style of the theme is based on
/// <c>Switch</c>, no other style targets a CheckBox outside the theme, and no XAML sets a style on
/// a CheckBox, so a box to tick cannot come back. The XAML is read as XML from the source tree, no
/// WPF involved, so the suite stays on the Linux runner.
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
}
