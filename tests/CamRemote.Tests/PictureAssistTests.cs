using Xunit;

namespace CamRemote.Tests;

public class PictureAssistTests
{
    private static byte[] Fill(int w, int h, Func<int, int, byte> grey)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = grey(x, y);
                int i = (y * w + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = 255;
            }
        return px;
    }

    private static AssistResult Run(byte[] px, int w, int h, AssistOptions o)
    {
        var r = new AssistResult();
        byte[] luma = [];
        PictureAssist.Analyze(px, w, h, o, r, ref luma);
        return r;
    }

    [Fact]
    public void Histogram_counts_every_pixel()
    {
        var r = Run(Fill(10, 10, (x, _) => x < 5 ? (byte)0 : (byte)255), 10, 10, new(true, false, 90, false, 1, 0));
        Assert.Equal(50, r.Luma[0]);
        Assert.Equal(50, r.Luma[255]);
        Assert.Equal(100, r.Red.Sum());
        Assert.Equal(50.0, r.OverPercent);
        Assert.Equal(50.0, r.UnderPercent);
        Assert.Empty(r.Overlay); // nothing to draw with zebras and peaking off
    }

    [Fact]
    public void Zebras_only_land_on_the_bright_part()
    {
        const int w = 100, h = 100;
        var r = Run(Fill(w, h, (x, _) => x < 50 ? (byte)100 : (byte)250), w, h, new(true, true, 90, false, 1, 0));
        Assert.Equal(w * h * 4, r.Overlay.Length);
        int darkSide = 0, brightSide = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (r.Overlay[(y * w + x) * 4 + 3] != 0) { if (x < 50) darkSide++; else brightSide++; }
        Assert.Equal(0, darkSide);
        Assert.InRange(brightSide, 2000, 3000); // stripes cover about half
    }

    [Fact]
    public void Peaking_marks_a_crisp_edge_but_not_a_soft_one()
    {
        const int w = 120, h = 80;
        var sharp = Run(Fill(w, h, (x, _) => x < 60 ? (byte)20 : (byte)220), w, h, new(true, false, 90, true, 1, 0xFF0000));
        var soft = Run(Fill(w, h, (x, _) => (byte)Math.Clamp(20 + (x - 20) * 2.5, 20, 220)), w, h, new(true, false, 90, true, 1, 0xFF0000));
        int Marked(AssistResult r) => Enumerable.Range(0, w * h).Count(i => r.Overlay[i * 4 + 3] == 255);
        Assert.True(Marked(sharp) > h); // a line down the picture
        Assert.Equal(0, Marked(soft));
        int first = Enumerable.Range(0, w * h).First(i => sharp.Overlay[i * 4 + 3] == 255);
        Assert.Equal(0xFF, sharp.Overlay[first * 4 + 2]); // red, in BGRA
        Assert.Equal(0, sharp.Overlay[first * 4]);
    }

    [Fact]
    public void Peaking_ignores_specks()
    {
        const int w = 120, h = 80;
        var r = Run(Fill(w, h, (x, y) => x == 60 && y == 40 ? (byte)250 : (byte)20), w, h, new(true, false, 90, true, 2, 0xFF0000));
        Assert.DoesNotContain(Enumerable.Range(0, w * h), i => r.Overlay[i * 4 + 3] != 0);
    }

    [Fact]
    public void Peaking_near_the_edges_stays_in_bounds()
    {
        const int w = 40, h = 30;
        var r = Run(Fill(w, h, (x, y) => (x + y) % 2 == 0 ? (byte)0 : (byte)255), w, h, new(true, true, 50, true, 2, 0xFF0000));
        Assert.Equal(w * h * 4, r.Overlay.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Histogram_picture_is_256_wide(bool colour)
    {
        var r = Run(Fill(16, 16, (x, y) => (byte)(x * 16 + y)), 16, 16, new(true, false, 90, false, 1, 0));
        var px = PictureAssist.DrawHistogram(r, 40, colour, 90);
        Assert.Equal(256 * 40 * 4, px.Length);
        int markerX = 90 * 255 / 100;
        Assert.Equal(255, px[(10 * 256 + markerX) * 4 + 3]); // the zebra level marker runs top to bottom
    }

    [Fact]
    public void Offer_drops_frames_while_one_is_being_read()
    {
        using var ui = new TestUi();
        var assist = new PictureAssist(ui) { Options = new(true, false, 90, false, 1, 0) };
        int done = 0;
        assist.Done += _ => Interlocked.Increment(ref done);
        int copies = 0;
        ui.Invoke(() =>
        {
            for (int i = 0; i < 5; i++) assist.Offer(8, 8, px => { copies++; Array.Fill(px, (byte)128); });
        });
        Rig.WaitFor(() => Volatile.Read(ref done) == 1);
        Assert.Equal(1, copies);
    }
}
