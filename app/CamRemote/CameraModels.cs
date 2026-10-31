namespace CamRemote;

/// <summary>A camera Sony's SDK can control over Wi-Fi. <see cref="Sdk"/> is the start of the name it reports.</summary>
public sealed record CameraModel(string Sdk, string Name, long LowIso = 0, long HighIso = 0)
{
    public bool HasBaseIso => LowIso > 0;
    public override string ToString() => Name;
}

public static class CameraModels
{
    /// <summary>Longer names sit above names they start with, so <see cref="Find"/> picks the right one.</summary>
    public static readonly IReadOnlyList<CameraModel> All =
    [
        new("ILCE-1M2", "A1 II"),
        new("ILCE-1", "A1"),
        new("ILCE-9M3", "A9 III"),
        new("ILCE-7RM6", "A7R VI"),
        new("ILCE-7RM5", "A7R V"),
        new("ILCE-7CR", "A7CR"),
        new("ILCE-7SM3", "A7S III", 640, 12800),
        new("ILCE-7M5", "A7 V"),
        new("ILCE-7M4", "A7 IV"),
        new("ILCE-7CM2", "A7C II"),
        new("ILCE-6700", "a6700"),
        new("ILME-FX6", "FX6"),
        new("ILME-FX3A", "FX3A"),
        new("ILME-FX30", "FX30"),
        new("ILME-FX3", "FX3"),
        new("ILME-FX2", "FX2"),
        new("MPC-2610", "BURANO"),
        new("ZV-E10M2", "ZV-E10 II"),
        new("ZV-E1", "ZV-E1"),
        new("PXW-Z300", "PXW-Z300"),
        new("PXW-Z200", "PXW-Z200"),
        new("HXR-NX800", "HXR-NX800"),
        new("DSC-RX1RM3", "RX1R III"),
        new("ILX-LR1", "ILX-LR1"),
    ];

    public static CameraModel? Find(string reported) =>
        All.FirstOrDefault(m => reported.StartsWith(m.Sdk, StringComparison.OrdinalIgnoreCase));

    public static string Label(string reported) => Find(reported)?.Name ?? (reported.Length > 0 ? reported : "Sony camera");
}
