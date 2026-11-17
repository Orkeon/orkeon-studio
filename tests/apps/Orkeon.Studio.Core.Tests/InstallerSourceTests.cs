using System.Xml.Linq;
using Orkeon.Tests.Shared.Produced;

namespace Orkeon.Studio.Core.Tests;

/// <summary>
/// The WiX sources of the MSI. Only the release workflow builds the MSI, on a tag: a source
/// that is not well-formed XML (a double hyphen in a comment, say) must fail here first.
/// </summary>
public sealed class InstallerSourceTests
{
    [Fact]
    public void ShouldKeepEveryWixSourceWellFormed()
    {
        var directory = Path.Combine(ProducedFile.RepositoryRoot(), "installers", "msi");
        var sources = Directory.EnumerateFiles(directory, "*.wx?", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(sources);
        Assert.All(sources, source =>
        {
            var failure = Record.Exception(() => XDocument.Load(source));
            Assert.True(failure is null, $"{Path.GetFileName(source)}: {failure?.Message}");
        });
    }
}
