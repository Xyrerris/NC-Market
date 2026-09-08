using NCMarket.Core;

namespace NCMarket.Tests;

/// <summary>
/// The parser behind <c>--type</c>. It is the only thing standing between a typo on the
/// command line and a capture of the wrong slot, so what matters is both that the names
/// it accepts keep working and that the ones it does not accept keep failing.
/// </summary>
public sealed class EquipmentTypesTests
{
    [Theory]
    [InlineData("weapon", EquipmentType.Weapon)]
    [InlineData("sword", EquipmentType.Weapon)]
    [InlineData("armor", EquipmentType.Armor)]
    [InlineData("belt", EquipmentType.Belt)]
    [InlineData("necklace", EquipmentType.Necklace)]
    [InlineData("ring", EquipmentType.Ring)]
    public void A_name_names_its_type(string text, EquipmentType expected)
    {
        Assert.True(EquipmentTypes.TryParse(text, out var type));
        Assert.Equal(expected, type);
    }

    /// <summary>
    /// The numeric form is the lib9c <c>ItemSubType</c> value, which is what the market
    /// service speaks: accepting "6" and mapping it to something other than 6 would be
    /// worse than rejecting it.
    /// </summary>
    [Fact]
    public void A_number_is_the_lib9c_value_it_looks_like()
    {
        foreach (var expected in EquipmentTypes.All)
        {
            Assert.True(EquipmentTypes.TryParse(((int)expected).ToString(), out var type));
            Assert.Equal(expected, type);
        }
    }

    [Theory]
    [InlineData("WEAPON")]
    [InlineData("Weapon")]
    [InlineData("  weapon  ")]
    [InlineData("\tSwOrD\n")]
    public void Case_and_surrounding_space_do_not_matter(string text)
    {
        Assert.True(EquipmentTypes.TryParse(text, out var type));
        Assert.Equal(EquipmentType.Weapon, type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("shield")]
    [InlineData("wea pon")]
    [InlineData("5")]
    [InlineData("11")]
    [InlineData("-6")]
    [InlineData("6.0")]
    public void What_is_not_a_type_is_refused_and_leaves_nothing_behind(string text)
    {
        Assert.False(EquipmentTypes.TryParse(text, out var type));

        // Il valore in uscita di un TryParse fallito viene comunque letto da chi si
        // dimentica di controllare il booleano: deve essere default, non l'ultimo
        // tentativo riuscito.
        Assert.Equal(default, type);
    }

    /// <summary>
    /// <see cref="EquipmentTypes.All"/> is what a capture without <c>--type</c> downloads
    /// and what the sale heuristic considers exhaustive. A member declared in the enum and
    /// missing here would silently shrink both, so the two are checked against each other.
    /// </summary>
    [Fact]
    public void Every_declared_type_is_one_the_commands_iterate_over()
    {
        Assert.Equal(Enum.GetValues<EquipmentType>(), EquipmentTypes.All);
        Assert.All(EquipmentTypes.All, t => Assert.True(EquipmentTypes.TryParse(t.ToString(), out _)));
    }
}
