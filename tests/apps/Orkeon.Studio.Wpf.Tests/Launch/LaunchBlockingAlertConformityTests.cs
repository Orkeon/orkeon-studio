using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Orkeon.Studio.Wpf.ViewModels.Launch;

namespace Orkeon.Studio.Wpf.Tests.Launch;

/// <summary>
/// Every refusal the launch tab knows is shown: each <c>IsBlockedBy…</c> of <see cref="LaunchTabViewModel"/>
/// has a Danger alert in the view, its message bound to the <c>…Message</c> beside it and shown on the flag,
/// so that a « Launch now » greyed out is never left unexplained (the model's Provider of Orkeon #457 among
/// them). The view is read as XML.
/// </summary>
public sealed class LaunchBlockingAlertConformityTests
{
    private static XDocument View([CallerFilePath] string thisFile = "") =>
        XDocument.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!,
            "..", "..", "..", "..", "src", "apps", "Orkeon.Studio.Wpf", "Views", "LaunchTabView.xaml")));

    private static List<string> Flags() =>
    [
        .. typeof(LaunchTabViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(bool) && property.Name.StartsWith("IsBlockedBy", StringComparison.Ordinal))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal),
    ];

    public static TheoryData<string> BlockingFlags() => new(Flags());

    [Fact]
    public void The_model_provider_is_among_the_flags_judged()
    {
        Assert.Contains("IsBlockedByLlmProvider", Flags());
    }

    [Theory]
    [MemberData(nameof(BlockingFlags))]
    public void Each_blocking_flag_shows_its_message_in_a_danger_alert(string flag)
    {
        var message = flag["IsBlockedBy".Length..] + "Message";
        Assert.NotNull(typeof(LaunchTabViewModel).GetProperty(message, BindingFlags.Public | BindingFlags.Instance));

        var alerts = View().Descendants()
            .Where(element => element.Name.LocalName == "Alert"
                && ((string?)element.Attribute("Visibility"))?.StartsWith("{Binding " + flag + ",", StringComparison.Ordinal) == true)
            .ToList();

        var alert = Assert.Single(alerts);
        Assert.Equal("Danger", (string?)alert.Attribute("Tone"));
        Assert.Equal("{Binding " + message + "}", (string?)alert.Attribute("Message"));
    }
}
