using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orkeon.Studio.Core.Configuration;

namespace Orkeon.Studio.Core.Tests.Configuration;

/// <summary>
/// An enumeration setting is written by its name, never as a number (Orkeon #464): the walk of a
/// declared section refuses a number, a comma list where one name is expected, and a number inside the
/// comma list of a <c>[Flags]</c> enumeration, in a value, a list and a dictionary, in the engine's
/// sentences (<c>SettingsValidation.NotAMemberName</c>), which never hold the value. No section Studio
/// walks declares a <c>[Flags]</c> enumeration, so a test-only one does, as the engine's own tests do.
/// </summary>
public sealed class EnumerationByNameTests
{
    [Theory]
    [InlineData(""" "Rights": "Read, 2" """, "Witness:Rights holds a number where WitnessRights takes names: write one or more of None, Read, Write, separated by commas.")]
    [InlineData(""" "Rights": "3" """, "Witness:Rights holds a number where WitnessRights takes names: write one or more of None, Read, Write, separated by commas.")]
    [InlineData(""" "Mode": "1" """, "Witness:Mode is a number where a name is expected: write one of Closed, Open.")]
    [InlineData(""" "Mode": "-1" """, "Witness:Mode is a number where a name is expected: write one of Closed, Open.")]
    [InlineData(""" "Mode": "Closed, Open" """, "Witness:Mode is a list where a single name is expected: write one of Closed, Open.")]
    [InlineData(""" "Required": "0" """, "Witness:Required is a number where a name is expected: write one of Closed, Open.")]
    [InlineData(""" "Modes": [ "Open", "1" ] """, "Witness:Modes:1 is a number where a name is expected: write one of Closed, Open.")]
    [InlineData(""" "ByName": { "a": "Open", "b": "1" } """, "Witness:ByName:b is a number where a name is expected: write one of Closed, Open.")]
    [InlineData(""" "ByName": { "a": "Open, Closed" } """, "Witness:ByName:a is a list where a single name is expected: write one of Closed, Open.")]
    public void A_number_or_a_list_where_a_name_is_expected_is_refused_in_the_engines_sentence(string witness, string expected)
    {
        Assert.Equal([expected], Refusals(witness));
    }

    [Fact]
    public void A_name_in_any_letter_case_and_a_flags_list_of_names_are_accepted()
    {
        Assert.Empty(Refusals(""" "Rights": "Read, write", "Mode": "open", "Required": "CLOSED", "Modes": [ "Closed" ], "ByName": { "a": "Open" } """));
    }

    [Fact]
    public void A_name_no_member_has_is_left_to_the_binder_and_never_echoed()
    {
        // The walk judges the spelling, not the name: a name no member has is the binder's to refuse,
        // in its sentence without the value (Orkeon #465).
        Assert.Empty(Refusals(""" "Mode": "Ajar" """));

        var refusals = Refusals(""" "Mode": "Closed, sk-test-0123456789abcdef", "Modes": [ "sk-test-0123, Open" ] """);
        Assert.Equal(2, refusals.Count);
        Assert.All(refusals, refusal => Assert.DoesNotContain("sk-test-0123", refusal, StringComparison.Ordinal));
    }

    private static List<string> Refusals(string witness)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($$"""{ "Witness": { {{witness}} } }"""));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        // The walk is internal to Studio's Core: reached through reflection, as the engine's own internals are.
        var walk = typeof(AppSettingsDocument).Assembly.GetType("Orkeon.Studio.Core.Configuration.SettingsBinding", throwOnError: true)!
            .GetMethod("UnknownKeys", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var found = (System.Collections.IEnumerable)walk.Invoke(null, [configuration, "Witness", typeof(WitnessOptions), Array.CreateInstance(walk.GetParameters()[3].ParameterType.GetElementType()!, 0)])!;
        return [.. found.Cast<object>().Select(item => (string)item.GetType().GetProperty("Problem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(item)!)];
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

        public WitnessMode? Mode { get; set; }

        public WitnessMode Required { get; set; }

        public List<WitnessMode?> Modes { get; } = [];

        public Dictionary<string, WitnessMode> ByName { get; } = [];
    }
}
