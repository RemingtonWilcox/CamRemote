namespace CamRemote;

/// <summary>Enlarges the preview in the app, for cameras that won't magnify over the SDK. It's the same pixels, just bigger.</summary>
public sealed class PictureZoom
{
    private double _centreX = 0.5, _centreY = 0.5;

    /// <summary>The ratio x10 (40 is x4.0), or 0 when off.</summary>
    public long Ratio { get; private set; }

    public double Scale => Ratio > 0 ? Ratio / 10.0 : 1;

    public void Set(long ratio)
    {
        Ratio = ratio;
        _centreX = _centreY = 0.5;
    }

    /// <summary>Centres the view on a spot in the picture, given as 0..1.</summary>
    public void MoveTo(double fx, double fy)
    {
        _centreX = fx;
        _centreY = fy;
    }

    /// <summary>The point, as 0..1, to scale the picture around so the view stays centred and inside the picture.</summary>
    public (double X, double Y) Origin => (Fixed(_centreX), Fixed(_centreY));

    private double Fixed(double centre)
    {
        if (Scale <= 1) return 0.5;
        double half = 0.5 / Scale;
        return (Math.Clamp(centre, half, 1 - half) - half) / (1 - 2 * half);
    }
}
