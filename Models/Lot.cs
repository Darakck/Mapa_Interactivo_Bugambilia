namespace MapaInteractivoBugambilia.Models;

public class Lot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ProjectKey { get; set; } = "bugambilia";

    public string Block { get; set; } = "";
    public int LotNumber { get; set; }
    public string DisplayCode { get; set; } = ""; // A-12

    public decimal? AreaM2 { get; set; }
    public decimal? AreaV2 { get; set; }

    public LotType LotType { get; set; } = LotType.Lot;
    public LotStatus Status { get; set; } = LotStatus.Available;

    // normalized 0..1
    public decimal? X { get; set; }
    public decimal? Y { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum LotType
{
    Lot,
    AreaVerde,
    Amenity
}

public enum LotStatus
{
    Available,
    Reserved,
    Sold,
    ComingSoon
}