using System.Collections.Immutable;
using NCMarket.Core;
using NCMarket.Core.Models;

namespace NCMarket.Tests;

/// <summary>
/// The bucket identity a valuation is built on. Everything downstream — which listings
/// are comparable, which range comes out — follows from what this key considers the same
/// piece, so the interesting cases are the ones where two pieces must <em>not</em> land
/// in the same bucket.
/// </summary>
public sealed class ValuationKeyTests
{
    private const int Atk = 2;
    private const int Def = 3;
    private const int Hit = 5;

    private static ValuationKey Key(
        int[]? options = null,
        ElementalType element = ElementalType.Fire,
        int level = 0,
        bool skill = true,
        bool custom = false,
        int grade = 8) =>
        new(EquipmentType.Weapon, grade, element, level,
            (options ?? new[] { Atk, Def }).ToImmutableSortedSet(), skill, custom);

    [Fact]
    public void A_listing_is_read_into_the_key_of_its_bucket()
    {
        var product = TestData.Product(
            grade: 8,
            itemSubType: (int)EquipmentType.Weapon,
            level: 7,
            elementalType: (int)ElementalType.Fire,
            optionStats: new[] { Atk, Def },
            hasSkill: true);

        Assert.Equal(Key(level: 7), ValuationKey.Of(product));
    }

    /// <summary>
    /// The base stat is what the item is, not what an option rolled. Counting it would
    /// put every weapon in a bucket with one more option than it has, and the sets the
    /// bot is asked about would match nothing.
    /// </summary>
    [Fact]
    public void The_base_stat_of_a_piece_is_not_one_of_its_options()
    {
        var product = TestData.Product(optionStats: new[] { Def });

        Assert.Contains(product.StatModels, s => s is { Type: Atk, Additional: false });
        Assert.Equal(new[] { Def }, ValuationKey.Of(product).OptionStats);
    }

    /// <summary>
    /// Two options landing on the same stat come back merged into one row, and what the
    /// owner reads off the piece is one stat: the key carries the set, so it says the
    /// same thing.
    /// </summary>
    [Fact]
    public void The_same_stat_rolled_twice_is_one_option()
    {
        var product = TestData.Product(optionStats: new[] { Def, Def, Hit });

        Assert.Equal(new[] { Def, Hit }, ValuationKey.Of(product).OptionStats);
    }

    [Fact]
    public void A_piece_with_no_option_has_an_empty_set_and_not_a_missing_one()
    {
        var key = ValuationKey.Of(TestData.Product());

        Assert.Empty(key.OptionStats);
        Assert.Equal("-", key.OptionStatsText());
    }

    /// <summary>
    /// The set is compared by its contents. Left to the compiler it would be compared by
    /// reference, and two keys built from the same options would be two buckets — which
    /// stays invisible until every valuation answers on a bucket of one.
    /// </summary>
    [Fact]
    public void Two_keys_built_from_the_same_options_are_one_bucket()
    {
        var first = Key(new[] { Atk, Def });
        var second = Key(new[] { Def, Atk });

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Single(new HashSet<ValuationKey> { first, second });
    }

    [Fact]
    public void Every_field_of_the_key_tells_two_buckets_apart()
    {
        var key = Key();

        Assert.NotEqual(key, key with { Type = EquipmentType.Ring });
        Assert.NotEqual(key, key with { Grade = 7 });
        Assert.NotEqual(key, key with { Element = ElementalType.Water });
        Assert.NotEqual(key, key with { Level = 7 });
        Assert.NotEqual(key, key with { HasSkill = false });
        Assert.NotEqual(key, key with { ByCustomCraft = true });
        Assert.NotEqual(key, Key(new[] { Atk, Hit }));
        Assert.NotEqual(key, Key(new[] { Atk }));
    }

    /// <summary>
    /// The stat names are <see cref="GameEnums.StatTypeName"/>'s, and sorted, so the same
    /// options read the same way whatever order they were given in.
    /// </summary>
    [Fact]
    public void The_options_read_back_as_their_stat_names()
    {
        Assert.Equal("ATK/DEF/HIT", Key(new[] { Hit, Atk, Def }).OptionStatsText());
    }

    [Fact]
    public void A_key_says_what_it_is()
    {
        Assert.Equal(
            "Weapon grado 8 · Fire · +0 · ATK/DEF · con skill",
            Key().ToString());

        Assert.Equal(
            "Weapon grado 6 · Wind · +7 · ATK · senza skill · custom craft",
            Key(new[] { Atk }, ElementalType.Wind, level: 7, skill: false, custom: true, grade: 6)
                .ToString());
    }
}
