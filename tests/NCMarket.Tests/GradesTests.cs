using NCMarket.Core;

namespace NCMarket.Tests;

/// <summary>
/// The parser behind <c>--grade</c>. A grade misread is a rarity filter that quietly
/// selects the wrong population, which changes every median computed from it.
/// </summary>
public sealed class GradesTests
{
    [Theory]
    [InlineData("normal", Grade.Normal)]
    [InlineData("rare", Grade.Rare)]
    [InlineData("epic", Grade.Epic)]
    [InlineData("unique", Grade.Unique)]
    [InlineData("legendary", Grade.Legendary)]
    [InlineData("divinity", Grade.Divinity)]
    [InlineData("divine", Grade.Divinity)]
    [InlineData("mythic", Grade.Mythic)]
    [InlineData("transcendent", Grade.Transcendent)]
    public void A_name_names_its_grade(string text, Grade expected)
    {
        Assert.True(Grades.TryParse(text, out var grade));
        Assert.Equal(expected, grade);
    }

    /// <summary>
    /// The numeric form is the lib9c <c>Grade</c> value, the one stored in the database
    /// and returned by the market service.
    /// </summary>
    [Fact]
    public void A_number_is_the_lib9c_value_it_looks_like()
    {
        foreach (var expected in Grades.All)
        {
            Assert.True(Grades.TryParse(((int)expected).ToString(), out var grade));
            Assert.Equal(expected, grade);
        }
    }

    [Theory]
    [InlineData("LEGENDARY")]
    [InlineData("Legendary")]
    [InlineData("  legendary  ")]
    public void Case_and_surrounding_space_do_not_matter(string text)
    {
        Assert.True(Grades.TryParse(text, out var grade));
        Assert.Equal(Grade.Legendary, grade);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("common")]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("-1")]
    public void What_is_not_a_grade_is_refused_and_leaves_nothing_behind(string text)
    {
        Assert.False(Grades.TryParse(text, out var grade));
        Assert.Equal(default, grade);
    }

    /// <summary>
    /// Same guard as for the equipment types: <see cref="Grades.All"/> is the list the
    /// help text and the report columns are built from, and a grade added to the enum
    /// alone would be invisible everywhere else.
    /// </summary>
    [Fact]
    public void Every_declared_grade_is_one_the_commands_iterate_over()
    {
        Assert.Equal(Enum.GetValues<Grade>(), Grades.All);
        Assert.All(Grades.All, g => Assert.True(Grades.TryParse(g.ToString(), out _)));
    }
}
