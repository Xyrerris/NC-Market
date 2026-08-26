using NCMarket.Core;
using NCMarket.Core.Models;

namespace NCMarket.Tests;

/// <summary>
/// The parser behind the mandatory element of a valuation request. An element misread is
/// a bucket of a different item at grade 8, where the five elements are five items with
/// prices on different scales.
/// </summary>
public sealed class ElementalsTests
{
    [Theory]
    [InlineData("normal", ElementalType.Normal)]
    [InlineData("fire", ElementalType.Fire)]
    [InlineData("water", ElementalType.Water)]
    [InlineData("land", ElementalType.Land)]
    [InlineData("wind", ElementalType.Wind)]
    public void A_name_names_its_element(string text, ElementalType expected)
    {
        Assert.True(Elementals.TryParse(text, out var element));
        Assert.Equal(expected, element);
    }

    /// <summary>
    /// The numeric form is the lib9c <c>ElementalType</c> value, the one stored in the
    /// database and returned by the market service.
    /// </summary>
    [Fact]
    public void A_number_is_the_lib9c_value_it_looks_like()
    {
        foreach (var expected in Elementals.All)
        {
            Assert.True(Elementals.TryParse(((int)expected).ToString(), out var element));
            Assert.Equal(expected, element);
        }
    }

    [Theory]
    [InlineData("FIRE")]
    [InlineData("Fire")]
    [InlineData("  fire  ")]
    public void Case_and_surrounding_space_do_not_matter(string text)
    {
        Assert.True(Elementals.TryParse(text, out var element));
        Assert.Equal(ElementalType.Fire, element);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ice")]
    [InlineData("fuoco")]
    [InlineData("5")]
    [InlineData("-1")]
    public void What_is_not_an_element_is_refused(string text)
    {
        Assert.False(Elementals.TryParse(text, out _));
    }

    /// <summary>
    /// The names come from <see cref="GameEnums.ElementalTypeName"/> and from nowhere
    /// else: a second list would be one more place to update, and the two would be found
    /// apart the day an element is added — a request refused for a name the answer prints.
    /// </summary>
    [Fact]
    public void The_names_parsed_are_the_names_displayed()
    {
        foreach (var element in Elementals.All)
        {
            var displayed = GameEnums.ElementalTypeName((int)element);

            Assert.Equal(displayed, Elementals.Name(element));
            Assert.True(Elementals.TryParse(displayed, out var parsed));
            Assert.Equal(element, parsed);
        }
    }

    /// <summary>
    /// Same guard as for grades and equipment types: <see cref="Elementals.All"/> is the
    /// list the buttons of the guided flow are built from, and an element added to the
    /// enum alone would be invisible there.
    /// </summary>
    [Fact]
    public void Every_declared_element_is_one_the_commands_iterate_over()
    {
        Assert.Equal(Enum.GetValues<ElementalType>(), Elementals.All);
    }
}
