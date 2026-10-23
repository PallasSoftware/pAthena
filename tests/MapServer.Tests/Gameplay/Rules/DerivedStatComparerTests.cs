using Athena.Net.MapServer.Gameplay.Rules;

namespace Athena.Net.MapServer.Tests.Gameplay.Rules;

// Pure server-side before/after derived-stat diff (DerivedStatComparer's own doc comment explains why this
// stops short of building any 0x00B0 packet - PROTOCOL-PENDING-CAPTURE, no client wire evidence for the
// equip/unequip stat-resend sequence specifically).
public sealed class DerivedStatComparerTests
{
    private static readonly DerivedStats Baseline = new(BaseAttack: 10, Hit: 50, Flee: 40, PerfectFlee: 0, Critical: 10, SoftDefense: 5, SoftMagicDefense: 3, HardDefense: 2, MagicAttackMin: 0, MagicAttackMax: 0);

    [Fact]
    public void Diff_IdenticalStats_ReturnsNoChanges()
    {
        Assert.Empty(DerivedStatComparer.Diff(Baseline, Baseline));
    }

    [Fact]
    public void Diff_OneFieldChanged_ReturnsExactlyThatChange()
    {
        var after = Baseline with { HardDefense = 15 }; // e.g. equipping Cotton_Shirt's own DEF.
        var changes = DerivedStatComparer.Diff(Baseline, after);
        Assert.Equal([new DerivedStatChange(DerivedStatField.HardDefense, 2, 15)], changes);
    }

    [Fact]
    public void Diff_SeveralFieldsChanged_ReturnsAllOfThemInDeclarationOrder()
    {
        var after = Baseline with { BaseAttack = 25, Critical = 12, MagicAttackMax = 30 };
        var changes = DerivedStatComparer.Diff(Baseline, after);
        Assert.Equal(
        [
            new DerivedStatChange(DerivedStatField.BaseAttack, 10, 25),
            new DerivedStatChange(DerivedStatField.Critical, 10, 12),
            new DerivedStatChange(DerivedStatField.MagicAttackMax, 0, 30),
        ], changes);
    }

    [Fact]
    public void Diff_EveryFieldCanBeDetectedIndependently()
    {
        foreach (var field in Enum.GetValues<DerivedStatField>())
        {
            var after = field switch
            {
                DerivedStatField.BaseAttack => Baseline with { BaseAttack = Baseline.BaseAttack + 1 },
                DerivedStatField.Hit => Baseline with { Hit = Baseline.Hit + 1 },
                DerivedStatField.Flee => Baseline with { Flee = Baseline.Flee + 1 },
                DerivedStatField.PerfectFlee => Baseline with { PerfectFlee = Baseline.PerfectFlee + 1 },
                DerivedStatField.Critical => Baseline with { Critical = Baseline.Critical + 1 },
                DerivedStatField.SoftDefense => Baseline with { SoftDefense = Baseline.SoftDefense + 1 },
                DerivedStatField.SoftMagicDefense => Baseline with { SoftMagicDefense = Baseline.SoftMagicDefense + 1 },
                DerivedStatField.HardDefense => Baseline with { HardDefense = Baseline.HardDefense + 1 },
                DerivedStatField.MagicAttackMin => Baseline with { MagicAttackMin = Baseline.MagicAttackMin + 1 },
                DerivedStatField.MagicAttackMax => Baseline with { MagicAttackMax = Baseline.MagicAttackMax + 1 },
                _ => throw new InvalidOperationException($"Unhandled {field}."),
            };
            var changes = DerivedStatComparer.Diff(Baseline, after);
            Assert.Single(changes);
            Assert.Equal(field, changes[0].Field);
        }
    }
}
