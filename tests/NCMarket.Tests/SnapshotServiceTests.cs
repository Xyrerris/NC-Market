using NCMarket.Core;

namespace NCMarket.Tests;

public sealed class SnapshotServiceTests
{
    [Fact]
    public async Task A_capture_stores_every_type_and_completes_the_snapshot()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var market = new FakeMarket()
            .With(EquipmentType.Ring, TestData.Product(), TestData.Product())
            .With(
                EquipmentType.Weapon,
                TestData.Product(itemSubType: (int)EquipmentType.Weapon));

        var report = await new SnapshotService(db, market).CaptureAsync(
            new SnapshotRequest { Types = new[] { EquipmentType.Ring, EquipmentType.Weapon } });

        Assert.Equal(new[] { EquipmentType.Ring, EquipmentType.Weapon }, market.Requested);
        Assert.Equal(3, report.Listings);
        Assert.Equal(
            new[] { (EquipmentType.Ring, 2), (EquipmentType.Weapon, 1) },
            report.Types.Select(t => (t.Type, t.Listings)));

        var snapshot = db.GetSnapshot(report.SnapshotId)!;
        Assert.True(snapshot.IsComplete);
        Assert.False(snapshot.IsTruncated);
        Assert.Equal(3, snapshot.ProductCount);
        Assert.Equal(report.SnapshotId, db.GetLatestSnapshotId("heimdall"));
    }

    [Fact]
    public async Task An_interrupted_capture_keeps_what_it_downloaded_and_stays_partial()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var market = new FakeMarket()
            .With(EquipmentType.Ring, TestData.Product(), TestData.Product())
            .FailingOn(EquipmentType.Weapon);
        var progress = new RecordingProgress();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SnapshotService(db, market).CaptureAsync(
                new SnapshotRequest { Types = new[] { EquipmentType.Ring, EquipmentType.Weapon } },
                progress));

        // Le inserzioni del tipo già scaricato restano, ma lo snapshot non viene mai
        // finalizzato: nessun comando a valle lo sceglie come ultimo.
        var snapshot = Assert.Single(db.GetSnapshots());
        Assert.False(snapshot.IsComplete);
        Assert.Equal(2, db.GetSnapshotProducts(snapshot.Id).Count);
        Assert.Null(db.GetLatestSnapshotId("heimdall"));

        Assert.Equal(snapshot.Id, progress.Created);
        Assert.Equal(snapshot.Id, progress.Interrupted);
        Assert.Equal(new[] { (EquipmentType.Ring, 2) }, progress.Completed);
    }

    [Fact]
    public async Task A_capture_limit_reaches_the_market_and_is_recorded_on_the_snapshot()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var market = new FakeMarket().With(EquipmentType.Ring, TestData.Product());

        var report = await new SnapshotService(db, market).CaptureAsync(
            new SnapshotRequest { Types = new[] { EquipmentType.Ring }, MaxPerType = 50 });

        Assert.Equal(50, market.LastMaxItems);

        // Senza la colonna, la rilevazione delle vendite leggerebbe come sparite le
        // inserzioni che questa cattura non è arrivata a scaricare.
        var snapshot = db.GetSnapshot(report.SnapshotId)!;
        Assert.True(snapshot.IsTruncated);
        Assert.Equal(50, snapshot.MaxPerType);
    }

    [Fact]
    public async Task A_capture_of_no_type_is_refused_before_anything_is_written()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        var market = new FakeMarket();

        await Assert.ThrowsAsync<ArgumentException>(
            () => new SnapshotService(db, market).CaptureAsync(
                new SnapshotRequest { Types = Array.Empty<EquipmentType>() }));

        // Uno snapshot vuoto sarebbe stato finalizzato come completo e sarebbe diventato
        // l'ultimo del pianeta, nascondendo il listino vero.
        Assert.Empty(db.GetSnapshots());
        Assert.Empty(market.Requested);
    }
}
