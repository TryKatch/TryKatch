using Shouldly;
using Trykatch.Infrastructure.Jobs;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleScheduleRegistryTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void CoalesceSelectsOnlyLatestNominalOccurrenceAfterCursorThroughHorizon()
    {
        ModuleScheduleOccurrenceBatch batch = ModuleScheduleRegistry.BuildOccurrenceBatch(
            "0 0/1 * * * ?",
            ModuleMisfirePolicy.Coalesce,
            Epoch,
            Epoch.AddMinutes(5),
            maxCatchUpOccurrences: null,
            maxCatchUpAge: null);

        batch.Occurrences.ShouldBe([Epoch.AddMinutes(5)]);
        batch.SkippedOccurrences.ShouldBe(4);
    }

    [TestMethod]
    public void CatchUpSelectsLatestBoundedOccurrencesInChronologicalOrder()
    {
        ModuleScheduleOccurrenceBatch batch = ModuleScheduleRegistry.BuildOccurrenceBatch(
            "0 0/1 * * * ?",
            ModuleMisfirePolicy.CatchUp,
            Epoch,
            Epoch.AddMinutes(5),
            maxCatchUpOccurrences: 2,
            maxCatchUpAge: TimeSpan.FromMinutes(10));

        batch.Occurrences.ShouldBe([Epoch.AddMinutes(4), Epoch.AddMinutes(5)]);
        batch.SkippedOccurrences.ShouldBe(3);
    }

    [TestMethod]
    public void CatchUpSkipsOccurrencesOutsideConfiguredAgeWindow()
    {
        ModuleScheduleOccurrenceBatch batch = ModuleScheduleRegistry.BuildOccurrenceBatch(
            "0 0/1 * * * ?",
            ModuleMisfirePolicy.CatchUp,
            Epoch,
            Epoch.AddMinutes(5),
            maxCatchUpOccurrences: 10,
            maxCatchUpAge: TimeSpan.FromMinutes(2));

        batch.Occurrences.ShouldBe([Epoch.AddMinutes(4), Epoch.AddMinutes(5)]);
        batch.SkippedOccurrences.ShouldBe(3);
    }

    [TestMethod]
    public void CatchUpRequiresExplicitSafeOccurrenceAndAgeBounds()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ModuleScheduleRegistry.BuildOccurrenceBatch(
            "0 0/1 * * * ?",
            ModuleMisfirePolicy.CatchUp,
            Epoch,
            Epoch.AddMinutes(5),
            maxCatchUpOccurrences: 0,
            maxCatchUpAge: TimeSpan.FromMinutes(1)));

        Should.Throw<ArgumentOutOfRangeException>(() => ModuleScheduleRegistry.BuildOccurrenceBatch(
            "0 0/1 * * * ?",
            ModuleMisfirePolicy.CatchUp,
            Epoch,
            Epoch.AddMinutes(5),
            maxCatchUpOccurrences: 1,
            maxCatchUpAge: TimeSpan.FromDays(32)));
    }
}
