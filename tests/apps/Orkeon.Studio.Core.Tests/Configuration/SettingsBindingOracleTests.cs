using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Validation;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// Studio reads a settings document without the file: it flattens the JSON tree itself, then binds it
/// as the engine does. The JSON configuration provider of .NET, the one a run loads the file with, is
/// the oracle of that flattening: the same keys, in the same spelling, with the same values, and the
/// same refusal of a file that writes a value twice at one key.
/// </summary>
public sealed class SettingsBindingOracleTests
{
    public static TheoryData<string> Documents => new()
    {
        "{}",
        """{ "A": 1 }""",
        """{ "A": { "B": "x", "C": true, "D": false, "E": null, "F": 1.50, "G": 1e3, "H": [], "I": {}, "J": "", "K": " " } }""",
        """{ "A": [1, "a", null, [], {}, [2], { "b": true }] }""",
        """{ "A": [[]], "B": [{}], "C": { "D": {} }, "E": { "F": [] } }""",
        // Two objects that differ only by letter case merge.
        """{ "A": { "b": 1 }, "a": { "c": 2 } }""",
        """{ "A": { "B": {} }, "a": { "b": { "c": 1 } } }""",
        """{ "A": { "x": 1 }, "a": 2 }""",
        """{ "a": 2, "A": { "x": 1 } }""",
        """{ "a": 1, "A": {} }""",
        """{ "a": 1, "A": [] }""",
        """{ "A": [], "a": ["b"] }""",
        """{ "A": {}, "a": { "b": 1 } }""",
        // A value written at a key that already holds one: the provider refuses the file.
        """{ "A": 1, "a": 2 }""",
        """{ "A": { "b": 1 }, "a": { "B": 2 } }""",
        """{ "A": {}, "a": 1 }""",
        """{ "A": null, "a": 1 }""",
        """{ "a": 1, "A": null }""",
        """{ "A": [], "a": 1 }""",
        """{ "A": ["a"], "A:0": "b" }""",
        """{ "A:0": "b", "A": ["a"] }""",
        """{ "A": { "B": 1 }, "A:B": 2 }""",
        """{ "A": ["a"], "a": ["b", "c"] }""",
        """{ "X": { "A": 1, "a": 2 }, "Y": { "B": 1, "b": 2 } }""",
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void A_document_is_flattened_as_the_json_provider_flattens_the_file(string json)
    {
        var (studio, duplicateKey) = Flatten(json);

        IConfigurationRoot? provider = null;
        string? refusal = null;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            provider = new ConfigurationBuilder().AddJsonStream(stream).Build();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            refusal = (ex.InnerException ?? ex).Message;
        }

        var document = AppSettingsDocument.Parse(json);
        if (provider is null)
        {
            Assert.NotNull(duplicateKey);
            Assert.Equal(refusal, document.LoadRefusal()?.Problem);
            Assert.Equal(duplicateKey, document.LoadRefusal()?.Path);
            return;
        }

        Assert.Null(duplicateKey);
        Assert.Null(document.LoadRefusal());
        Assert.Equal(Pairs(provider), Pairs(new ConfigurationBuilder().AddInMemoryCollection(studio).Build()));
    }

    /// <summary>
    /// A file the configuration does not load is refused once by the settings check, at the key the
    /// configuration names, and first by the section that holds the key and by the decision status.
    /// </summary>
    [Fact]
    public void A_file_the_configuration_does_not_load_is_refused_at_the_key_it_names()
    {
        var document = AppSettingsDocument.Parse(
            """{ "Orkeon": { "Compaction": { "Enabled": true, "enabled": false, "TriggerShare": 2 }, "Skills": { "Libary": "/x" } } }""");

        var refusal = Assert.NotNull(document.LoadRefusal());
        Assert.Equal(("Orkeon:Compaction:enabled", "A duplicate key 'Orkeon:Compaction:enabled' was found."), refusal);
        Assert.Equal(refusal, document.Compaction.Problems()[0]);
        Assert.DoesNotContain(refusal, document.Skills.Problems());
        Assert.Equal(2, document.Compaction.Problems().Count);

        var status = document.Decision.Resolve(_ => null);
        Assert.Equal(refusal.Problem, status.Refusal);
        Assert.Equal(DecisionRefusalStage.File, status.RefusalStage);

        var errors = new AppSettingsValidator().Validate(document).Where(message => message.Severity == ValidationSeverity.Error).ToList();
        Assert.Single(errors, message => message.Text == refusal.Problem && message.Path == "Orkeon:Compaction:enabled");
        Assert.Equal(3, errors.Count);
    }

    /// <summary>
    /// The binder reads <c>True</c> and <c>False</c> for a JSON boolean and an empty text for an empty
    /// list, so that is what its refusal quotes; an empty object and a null are keys without a value,
    /// which it would read as a number's zero and a switch's off: the engine's start refuses them on a
    /// value type, in place of what a rule says of that zero (Orkeon #450), and lets a text pass.
    /// </summary>
    [Theory]
    [InlineData("""{ "Orkeon": { "Compaction": { "KeepRecentTurns": true } } }""", "Failed to convert configuration value 'True' at 'Orkeon:Compaction:KeepRecentTurns' to type 'System.Int32'.")]
    [InlineData("""{ "Orkeon": { "Compaction": { "Enabled": [] } } }""", "Failed to convert configuration value '' at 'Orkeon:Compaction:Enabled' to type 'System.Boolean'.")]
    [InlineData("""{ "Orkeon": { "Compaction": { "Prune": { "HeadChars": false } } } }""", "Failed to convert configuration value 'False' at 'Orkeon:Compaction:Prune:HeadChars' to type 'System.Int32'.")]
    [InlineData("""{ "Orkeon": { "Skills": { "MaxSkillChars": [] } } }""", "Failed to convert configuration value '' at 'Orkeon:Skills:MaxSkillChars' to type 'System.Int32'.")]
    [InlineData("""{ "Orkeon": { "Compaction": { "TriggerShare": null } } }""", "Orkeon:Compaction:TriggerShare is written without a value, which would bind it to 0: write a number, or leave the key out and its default applies.")]
    [InlineData("""{ "Orkeon": { "Skills": { "MaxSkillChars": {} } } }""", "Orkeon:Skills:MaxSkillChars is written without a value, which would bind it to 0: write a number, or leave the key out and its default applies.")]
    [InlineData("""{ "Orkeon": { "Compaction": { "Enabled": {} } } }""", "Orkeon:Compaction:Enabled is written without a value, which would bind it to false: write true or false, or leave the key out and its default applies.")]
    [InlineData("""{ "Orkeon": { "Compaction": { "Prune": { "Enabled": null } } } }""", "Orkeon:Compaction:Prune:Enabled is written without a value, which would bind it to false: write true or false, or leave the key out and its default applies.")]
    [InlineData("""{ "Orkeon": { "Compaction": { "Profile": null }, "Skills": {} } }""", null)]
    public void A_boolean_and_an_empty_list_reach_the_binder_as_the_provider_writes_them(string json, string? refusal)
    {
        var document = AppSettingsDocument.Parse(json);

        var problems = document.Compaction.Problems().Concat(document.Skills.Problems()).Select(problem => problem.Problem).ToList();

        Assert.Equal(refusal is null ? [] : [refusal], problems);
    }

    [Fact]
    public void A_switch_written_without_a_value_is_off_and_an_absent_one_keeps_the_engines_default()
    {
        var written = AppSettingsDocument.Parse("""{ "Orkeon": { "Compaction": { "enabled": null, "Prune": { "Enabled": "TRUE" } } } }""").Compaction;
        var absent = AppSettingsDocument.CreateEmpty().Compaction;

        Assert.False(written.IsEnabled);
        Assert.True(written.IsPruneEnabled);
        Assert.Equal(CompactionSection.DefaultEnabled, absent.IsEnabled);
        Assert.Equal(CompactionSection.DefaultPruneEnabled, absent.IsPruneEnabled);
    }

    /// <summary>
    /// A key under a single value, a key that is no setting under the pruning, and a share that is no
    /// finite number are refused where the engine's walk of the section meets them: the keys in their
    /// order, the pruning's among the compaction's.
    /// </summary>
    [Fact]
    public void The_keys_of_the_context_sections_are_walked_at_any_depth_in_the_hosts_order()
    {
        var document = AppSettingsDocument.Parse(
            """
            { "Orkeon": {
                "Compaction": { "Tx": 1, "TriggerShare": { "x": 1 }, "Prune": { "Hx": 1, "FillShare": "Infinity" } },
                "Skills": { "Library": { "a": 1 }, "MaxSkillChars": 10 } } }
            """);

        Assert.Equal(
            [
                // The rule names the key: the walk's 'not a finite number' is not said twice (Orkeon #450).
                "Orkeon:Compaction:Prune:FillShare is 'Infinity', which is not a share of the window: write a number above 0 and at most 1, such as 0.4.",
                "Orkeon:Compaction:Prune:Hx is not a setting: Orkeon:Compaction:Prune carries Enabled, FillShare, KeepRecentMessages, HeadChars.",
                "Orkeon:Compaction:TriggerShare:x is not a setting: Orkeon:Compaction:TriggerShare is a single value.",
                "Orkeon:Compaction:Tx is not a setting: Orkeon:Compaction carries Enabled, TriggerShare, KeepRecentTurns, Profile, Prune.",
            ],
            document.Compaction.Problems().Select(problem => problem.Problem));
        Assert.Equal(
            [("Orkeon:Skills:Library:a", "Orkeon:Skills:Library:a is not a setting: Orkeon:Skills:Library is a single value.")],
            document.Skills.Problems());
    }

    private static List<(string Key, string? Value)> Pairs(IConfiguration configuration) =>
        [.. configuration.AsEnumerable().OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value))];

    // SettingsBinding is internal to Core, which shows its internals to nobody: reached by reflection.
    private static (Dictionary<string, string?> Data, string? DuplicateKey) Flatten(string json)
    {
        var flatten = typeof(AppSettingsDocument).Assembly
            .GetType("Orkeon.Studio.Core.Configuration.SettingsBinding", throwOnError: true)!
            .GetMethod("Flatten", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] arguments = [AppSettingsDocument.Parse(json).Root, null];
        var data = (Dictionary<string, string?>)flatten.Invoke(null, arguments)!;
        return (data, (string?)arguments[1]);
    }
}
