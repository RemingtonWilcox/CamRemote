using Xunit;

namespace CamRemote.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(280L, "F2.8")]
    [InlineData(400L, "F4")]
    [InlineData(1100L, "F11")]
    [InlineData(0L, "--")]
    [InlineData(0xFFFEL, "--")]
    [InlineData(0xFFFDL, "Close")]
    public void FNumber(long raw, string shown) => Assert.Equal(shown, Format.FNumber(raw));

    [Theory]
    [InlineData(640L, "ISO 640")]
    [InlineData(0xFFFFFFL, "ISO AUTO")]
    [InlineData((1L << 24) | 409600, "ISO 409600")] // the expanded flag isn't part of the number
    public void Iso(long raw, string shown) => Assert.Equal(shown, Format.Iso(raw));

    [Theory]
    [InlineData((1L << 16) | 50, "1/50")]
    [InlineData((1L << 16) | 8000, "1/8000")]
    [InlineData((30L << 16) | 1, "30\"")]
    [InlineData((3L << 16) | 10, "0.3\"")]
    [InlineData((25L << 16) | 10, "2.5\"")]
    [InlineData(0L, "Bulb")]
    [InlineData(0xFFFFFFFFL, "--")]
    [InlineData(1L << 16, "--")] // zero denominator
    public void Shutter(long raw, string shown) => Assert.Equal(shown, Format.Shutter(raw));

    [Fact]
    public void Shutter_speeds_sort_by_exposure_time()
    {
        long[] raw = [(1L << 16) | 50, (30L << 16) | 1, (1L << 16) | 8000, (3L << 16) | 10];
        Assert.Equal(new[] { (1L << 16) | 8000, (1L << 16) | 50, (3L << 16) | 10, (30L << 16) | 1 },
            raw.OrderBy(Format.ShutterSeconds));
        Assert.True(Format.ShutterSeconds(0) > Format.ShutterSeconds(30L << 16 | 1)); // Bulb is longest
    }

    [Theory]
    [InlineData(0L, "Off")]
    [InlineData(10L << 32, "Off")]
    [InlineData((40L << 32) | 0x12345678, "x4")]
    [InlineData(55L << 32, "x5.5")]
    public void Magnifier(long raw, string shown) => Assert.Equal(shown, Format.Magnifier(raw));

    [Theory]
    [InlineData(5600L, "5600K")]
    [InlineData(0L, "< min")]
    [InlineData(0xFFFFL, "> max")]
    public void ColorTemp(long raw, string shown) => Assert.Equal(shown, Format.ColorTemp(raw));

    [Theory]
    [InlineData(57L, -1L, "57%")]
    [InlineData(-1L, 1L, "under 10%")]
    [InlineData(-1L, 5L, "75–100%")]
    [InlineData(-1L, 7L, "33–66%")]
    [InlineData(-1L, 0x10000L | 4, "50–75% + USB power")]
    [InlineData(-1L, 0x10000L, "USB power")]
    [InlineData(-1L, -1L, "--")]
    public void Battery(long percent, long level, string shown) => Assert.Equal(shown, Format.Battery(percent, level));

    [Theory]
    [InlineData(10L, -1L, true, true)]
    [InlineData(15L, -1L, false, true)]
    [InlineData(50L, -1L, false, false)]
    [InlineData(-1L, 1L, true, true)]
    [InlineData(-1L, 2L, false, true)]
    [InlineData(-1L, 6L, false, true)]
    [InlineData(-1L, 0x10001L, false, false)] // on USB power nothing is about to run out
    [InlineData(-1L, 5L, false, false)]
    public void Battery_warnings(long percent, long level, bool critical, bool low)
    {
        Assert.Equal(critical, Format.BatteryCritical(percent, level));
        Assert.Equal(low, Format.BatteryLow(percent, level));
    }

    [Theory]
    [InlineData(59L, "0m 59s")]
    [InlineData(61L, "1m 01s")]
    [InlineData(3600L + 120, "1h 02m")]
    [InlineData(-1L, "--")]
    [InlineData(0xFFFFFFFFL, "--")]
    public void Duration(long seconds, string shown) => Assert.Equal(shown, Format.Duration(seconds));

    [Fact]
    public void Named_values_fall_back_to_hex()
    {
        Assert.Equal("M Manual", Format.Named("ExposureProgram")(1));
        Assert.Equal("0x2A", Format.Named("ExposureProgram")(42));
        Assert.Equal("0x1", Format.Named("NoSuchTable")(1));
    }

    [Fact]
    public void Error_names_include_the_code()
    {
        Assert.Equal("CrError_Connect (0x8200)", Native.ErrorName(-0x8200));
        Assert.Equal("0x1234", Native.ErrorName(0x1234));
    }
}

public class CameraModelTests
{
    [Fact]
    public void Every_model_is_found_by_its_own_name_not_a_shorter_one()
    {
        foreach (var m in CameraModels.All)
        {
            Assert.Same(m, CameraModels.Find(m.Sdk));
            Assert.Same(m, CameraModels.Find(m.Sdk + " (ver 3.00)"));
        }
    }

    [Theory]
    [InlineData("ILME-FX30", "FX30")]
    [InlineData("ILME-FX3", "FX3")]
    [InlineData("ZV-E10M2", "ZV-E10 II")]
    [InlineData("ilce-7sm3", "A7S III")]
    [InlineData("ILCE-7M3", "ILCE-7M3")] // not supported: shown as reported
    [InlineData("", "Sony camera")]
    public void Labels(string reported, string label) => Assert.Equal(label, CameraModels.Label(reported));

    [Fact]
    public void Only_the_a7s_iii_has_base_iso_buttons()
    {
        var withBase = CameraModels.All.Where(m => m.HasBaseIso).ToList();
        var a7s3 = Assert.Single(withBase);
        Assert.Equal((640L, 12800L), (a7s3.LowIso, a7s3.HighIso));
    }
}

public class PictureZoomTests
{
    [Fact]
    public void Off_scales_around_the_middle()
    {
        var z = new PictureZoom();
        Assert.Equal(1, z.Scale);
        z.MoveTo(0.1, 0.9);
        Assert.Equal((0.5, 0.5), z.Origin);
    }

    [Fact]
    public void Centred_on_a_spot_stays_inside_the_picture()
    {
        var z = new PictureZoom();
        z.Set(20); // x2: the view is half the picture
        z.MoveTo(0.5, 0.5);
        Assert.Equal((0.5, 0.5), z.Origin);
        z.MoveTo(0, 1); // a corner: the view stops at the edge
        Assert.Equal((0.0, 1.0), z.Origin);
        z.MoveTo(0.375, 0.625);
        var (x, y) = z.Origin;
        Assert.Equal(0.25, x, 9);
        Assert.Equal(0.75, y, 9);
    }

    [Fact]
    public void Changing_the_ratio_recentres()
    {
        var z = new PictureZoom();
        z.Set(40);
        z.MoveTo(0.9, 0.9);
        z.Set(20);
        Assert.Equal((0.5, 0.5), z.Origin);
    }
}

public class UpdateCheckTests
{
    [Theory]
    [InlineData("v9.0.0", "9.0.0")]
    [InlineData("1.0.10", "1.0.10")]
    [InlineData("v1.0.2", null)]
    [InlineData("1.0.1", null)]
    [InlineData("v2.0-beta", null)] // not a plain version: ignored
    [InlineData("", null)]
    public void Only_a_higher_version_counts_as_newer(string tag, string? newer)
    {
        Assert.Equal("1.0.2", UpdateCheck.Current);
        Assert.Equal(newer, UpdateCheck.NewerThanCurrent(tag));
    }
}
