using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orkeon.Application.Configuration;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// An enumeration setting is written by its name, never as a number (Orkeon #464): the walk of a
/// declared section refuses a number, a comma list where one name is expected, and a number inside the
/// comma list of a <c>[Flags]</c> enumeration, in a value, a list item and a dictionary value, a
/// nullable one included, in sentences that never hold the value (#465). The engine is the oracle:
/// every row walks the same section with the engine's own walk (<c>SettingsValidation.UnknownKeys</c>,
/// private to Orkeon.Hosting, reached through reflection over the package) and with Studio's
/// <see cref="SettingsBinding"/>, and the two must say the same sentences, in the same order.
/// </summary>
/// <remarks>
/// No section the engine declares carries a <c>[Flags]</c> enumeration, nor an enumeration in a
/// dictionary: a test-only shape declares them, which the engine's walk reads as it reads any declared
/// section. A value and a list item of a real section (<c>Guardian:Llm:FailMode</c>,
/// <c>Guardian:Llm:Phases</c>) are compared through the host's start in
/// <c>EngineStartAndLoadOracleTests</c>.
/// </remarks>
public sealed class EnumerationByNameTests
{
    private const string Rights = "write one or more of None, Read, Write, separated by commas.";
    private const string Modes = "write one of Closed, Open.";

    [Theory]
    // A value: a [Flags] one (its sentence lists None, as the account binders' own now do), a nullable one, a required one.
    [InlineData(""" "Rights": "Read, 2" """, "Witness:Rights holds a number where WitnessRights takes names: " + Rights)]
    [InlineData(""" "Rights": "3" """, "Witness:Rights holds a number where WitnessRights takes names: " + Rights)]
    [InlineData(""" "MaybeRights": "1" """, "Witness:MaybeRights holds a number where WitnessRights takes names: " + Rights)]
    [InlineData(""" "Mode": "1" """, "Witness:Mode is a number where a name is expected: " + Modes)]
    [InlineData(""" "Mode": "-1" """, "Witness:Mode is a number where a name is expected: " + Modes)]
    [InlineData(""" "Mode": "Closed, Open" """, "Witness:Mode is a list where a single name is expected: " + Modes)]
    [InlineData(""" "Required": "0" """, "Witness:Required is a number where a name is expected: " + Modes)]
    [InlineData(""" "Required": "Open,Closed" """, "Witness:Required is a list where a single name is expected: " + Modes)]
    // A list item: nullable or not, a [Flags] one.
    [InlineData(""" "Modes": [ "Open", "1" ] """, "Witness:Modes:1 is a number where a name is expected: " + Modes)]
    [InlineData(""" "Modes": [ "Open, Closed" ] """, "Witness:Modes:0 is a list where a single name is expected: " + Modes)]
    [InlineData(""" "RequiredModes": [ "2" ] """, "Witness:RequiredModes:0 is a number where a name is expected: " + Modes)]
    [InlineData(""" "RightsList": [ "Read", "Write, 1" ] """, "Witness:RightsList:1 holds a number where WitnessRights takes names: " + Rights)]
    [InlineData(""" "RightsList": [ "4" ] """, "Witness:RightsList:0 holds a number where WitnessRights takes names: " + Rights)]
    // A dictionary value: nullable or not, a [Flags] one.
    [InlineData(""" "ByName": { "a": "Open", "b": "1" } """, "Witness:ByName:b is a number where a name is expected: " + Modes)]
    [InlineData(""" "ByName": { "a": "Open, Closed" } """, "Witness:ByName:a is a list where a single name is expected: " + Modes)]
    [InlineData(""" "MaybeByName": { "a": "0" } """, "Witness:MaybeByName:a is a number where a name is expected: " + Modes)]
    [InlineData(""" "RightsByName": { "a": "Read, 2" } """, "Witness:RightsByName:a holds a number where WitnessRights takes names: " + Rights)]
    [InlineData(""" "RightsByName": { "a": "3" } """, "Witness:RightsByName:a holds a number where WitnessRights takes names: " + Rights)]
    public void A_number_or_a_list_where_a_name_is_expected_is_refused_as_the_engine_refuses_it(string witness, string expected)
    {
        var studio = StudioRefusals(witness);

        Assert.Equal(EngineRefusals(witness), studio);
        Assert.Equal([expected], studio);
    }

    [Fact]
    public void Every_shape_at_once_is_refused_as_the_engine_refuses_it_in_its_order()
    {
        const string Witness = """
            "Rights": "1", "MaybeRights": "Read, 2", "Mode": "1", "Required": "Open, Closed",
            "Modes": [ "0", "Open, Closed" ], "RequiredModes": [ "1" ], "RightsList": [ "2" ],
            "ByName": { "a": "1" }, "MaybeByName": { "a": "Open, Closed" }, "RightsByName": { "a": "Write, 1" }
            """;

        var studio = StudioRefusals(Witness);

        Assert.Equal(11, studio.Count);
        Assert.Equal(EngineRefusals(Witness), studio);
    }

    [Fact]
    public void A_name_in_any_letter_case_and_a_flags_list_of_names_are_accepted_as_the_engine_accepts_them()
    {
        const string Witness = """
            "Rights": "Read, write", "MaybeRights": "none", "Mode": "open", "Required": "CLOSED", "Modes": [ "Closed", null ],
            "RequiredModes": [ "open" ], "RightsList": [ "Read, Write" ], "ByName": { "a": "Open" }, "MaybeByName": { "a": "closed" },
            "RightsByName": { "a": "write" }
            """;

        Assert.Empty(EngineRefusals(Witness));
        Assert.Empty(StudioRefusals(Witness));
    }

    [Fact]
    public void A_name_no_member_has_is_left_to_the_binder_by_both_walks()
    {
        // The walk judges the spelling, not the name: a name no member has is the binder's to refuse,
        // in its sentence without the value (Orkeon #465).
        const string Witness = """ "Mode": "Ajar", "Rights": "Read, Exec", "Modes": [ "Ajar" ], "ByName": { "a": "Ajar" } """;

        Assert.Empty(EngineRefusals(Witness));
        Assert.Empty(StudioRefusals(Witness));
    }

    [Fact]
    public void A_comma_list_holding_a_secret_is_refused_by_both_walks_without_echoing_it()
    {
        const string Witness = """ "Mode": "Closed, sk-test-0123456789abcdef", "Modes": [ "sk-test-0123, Open" ], "RightsByName": { "a": "Read, 0123456789" } """;

        var studio = StudioRefusals(Witness);

        Assert.Equal(3, studio.Count);
        Assert.Equal(EngineRefusals(Witness), studio);
        Assert.All(studio, refusal => Assert.DoesNotContain("0123", refusal, StringComparison.Ordinal));
    }

    [Fact]
    public void A_number_that_is_not_finite_is_refused_as_the_engine_refuses_it_without_echoing_it()
    {
        // Orkeon #465: the sentence names the key and what it takes, never NaN or the infinity written.
        const string Witness = """ "Share": "NaN", "Ratio": "1e40", "Shares": [ "0.5", "-Infinity" ] """;

        var studio = StudioRefusals(Witness);

        // In the configuration's order of keys, as both walks list them.
        Assert.Equal(EngineRefusals(Witness), studio);
        Assert.Equal(
            [
                "Witness:Ratio is not a finite number: write a number within the type's range.",
                "Witness:Share is not a finite number: write a number within the type's range.",
                "Witness:Shares:1 is not a finite number: write a number within the type's range.",
            ],
            studio);
    }

    private static IConfiguration Configuration(string witness)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($$"""{ "Witness": { {{witness}} } }"""));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    private static List<string> StudioRefusals(string witness)
    {
        // The walk is internal to Studio's Core: reached through reflection, as the engine's own internals are.
        var walk = typeof(AppSettingsDocument).Assembly.GetType("Orkeon.Studio.Core.Configuration.SettingsBinding", throwOnError: true)!
            .GetMethod("UnknownKeys", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var found = (System.Collections.IEnumerable)walk.Invoke(null, [Configuration(witness), "Witness", typeof(WitnessOptions), Array.CreateInstance(walk.GetParameters()[3].ParameterType.GetElementType()!, 0)])!;
        return [.. found.Cast<object>().Select(item => (string)item.GetType().GetProperty("Problem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(item)!)];
    }

    /// <summary>
    /// What the engine's start walk says of the section: <c>SettingsValidation.UnknownKeys</c> over the
    /// one declaration of <c>Witness</c>, each finding a <c>(Path, Refusal, Kind)</c> tuple.
    /// </summary>
    private static List<string> EngineRefusals(string witness)
    {
        var walk = typeof(Orkeon.Hosting.RunnerHost).Assembly.GetType("Orkeon.Hosting.SettingsValidation", throwOnError: true)!
            .GetMethod("UnknownKeys", BindingFlags.NonPublic | BindingFlags.Static, [typeof(IConfiguration), typeof(List<SettingsDeclaration>)])!;
        var found = (System.Collections.IEnumerable)walk.Invoke(null, [Configuration(witness), new List<SettingsDeclaration> { new("Witness", typeof(WitnessOptions)) }])!;
        return [.. found.Cast<object>().Select(item => (string)item.GetType().GetField("Item2")!.GetValue(item)!)];
    }

    [Flags]
    private enum WitnessRights
    {
        None = 0,
        Read = 1,
        Write = 2,
    }

    private enum WitnessMode
    {
        Closed,
        Open,
    }

    private sealed class WitnessOptions
    {
        public WitnessRights Rights { get; set; }

        public WitnessRights? MaybeRights { get; set; }

        public WitnessMode? Mode { get; set; }

        public WitnessMode Required { get; set; }

        public List<WitnessMode?> Modes { get; } = [];

        public List<WitnessMode> RequiredModes { get; } = [];

        public List<WitnessRights> RightsList { get; } = [];

        public Dictionary<string, WitnessMode> ByName { get; } = [];

        public Dictionary<string, WitnessMode?> MaybeByName { get; } = [];

        public Dictionary<string, WitnessRights> RightsByName { get; } = [];

        public double Share { get; set; }

        public float? Ratio { get; set; }

        public List<double> Shares { get; } = [];
    }
}
