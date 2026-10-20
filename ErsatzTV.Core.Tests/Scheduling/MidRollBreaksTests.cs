using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Scheduling;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

[TestFixture]
public class MidRollBreaksTests
{
    [Test]
    public void Every_15_Minutes_On_A_50_Minute_Episode()
    {
        MidRollBreaks.TryParse("@coupures toutes=15 debut=10 fin=5", out MidRollBreaks.Settings s, out string rest).ShouldBeTrue();
        rest.ShouldBe(string.Empty);

        List<MediaChapter> chapters = MidRollBreaks.BuildChapters(s, TimeSpan.Zero, TimeSpan.FromMinutes(50));

        // coupures à 10, 25 et 40 minutes
        chapters.Select(c => c.EndTime.TotalMinutes).ShouldBe([10, 25, 40, 50]);
    }

    [Test]
    public void Two_Breaks_Spread_Evenly()
    {
        MidRollBreaks.TryParse("@coupures nombre=2 debut=20 fin=20", out MidRollBreaks.Settings s, out _).ShouldBeTrue();
        List<MediaChapter> chapters = MidRollBreaks.BuildChapters(s, TimeSpan.Zero, TimeSpan.FromMinutes(100));
        chapters.Select(c => c.EndTime.TotalMinutes).ShouldBe([20, 80, 100]);
    }

    [Test]
    public void Short_Items_Get_No_Break()
    {
        MidRollBreaks.TryParse("@coupures toutes=15", out MidRollBreaks.Settings s, out _).ShouldBeTrue();
        MidRollBreaks.BuildChapters(s, TimeSpan.Zero, TimeSpan.FromSeconds(30)).ShouldBeEmpty();
    }

    [Test]
    public void Remainder_Is_Kept_And_Plain_Expressions_Are_Untouched()
    {
        MidRollBreaks.TryParse("@coupures toutes=10; num > 1", out _, out string rest).ShouldBeTrue();
        rest.ShouldBe("num > 1");
        MidRollBreaks.TryParse("last_mid_filler > 600", out _, out _).ShouldBeFalse();
        MidRollBreaks.StripDirective("last_mid_filler > 600").ShouldBe("last_mid_filler > 600");
    }
}
