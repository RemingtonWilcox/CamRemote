namespace CamRemote;

/// <summary>Turns raw SDK property values into the labels the camera shows.</summary>
public static class Format
{
    public static string FNumber(long v)
    {
        if (v is 0 or 0xFFFE) return "--";
        if (v == 0xFFFD) return "Close";
        if (v == 0xFFFF) return "";
        return v % 100 == 0 ? $"F{v / 100}" : $"F{v / 100.0:0.#}";
    }

    public static string Iso(long v)
    {
        long value = v & 0xFFFFFF;
        return value == 0xFFFFFF ? "ISO AUTO" : $"ISO {value}";
    }

    public static string Shutter(long v)
    {
        if (v == 0) return "Bulb";
        if (v == 0xFFFFFFFF) return "--";
        long num = (v >> 16) & 0xFFFF, den = v & 0xFFFF;
        if (den == 0) return "--";
        if (num == 1) return $"1/{den}";
        if (num % den == 0) return $"{num / den}\"";
        return $"{num / (double)den:0.#}\"";
    }

    /// <summary>Exposure time in seconds, for sorting shutter speeds.</summary>
    public static double ShutterSeconds(long v)
    {
        long num = (v >> 16) & 0xFFFF, den = v & 0xFFFF;
        return v == 0 ? 1e6 : den == 0 ? 0 : num / (double)den;
    }

    /// <summary>Focus magnifier: the ratio x10 sits in the upper 4 bytes, the position in the lower 4.</summary>
    public static string Magnifier(long v)
    {
        long ratio = (v >> 32) & 0xFFFFFFFF;
        return ratio is 0 or 10 ? "Off" : $"x{ratio / 10.0:0.#}";
    }

    public static string ColorTemp(long v) => v switch
    {
        0 => "< min",
        0xFFFF => "> max",
        _ => $"{v}K",
    };

    /// <summary>
    /// Percent when the camera reports it. Over Wi-Fi the A7S III only sends the battery bars, so
    /// those are shown as the percent range each bar stands for.
    /// </summary>
    public static string Battery(long percent, long level)
    {
        if (percent is >= 0 and <= 100) return $"{percent}%";
        string usb = (level & 0x10000) != 0 ? " + USB power" : "";
        return (level & 0xFFFF) switch
        {
            1 => "under 10%" + usb,
            2 => "10–25%" + usb, 3 => "25–50%" + usb, 4 => "50–75%" + usb, 5 => "75–100%" + usb,
            6 => "10–33%" + usb, 7 => "33–66%" + usb, 8 => "66–100%" + usb,
            0 when usb.Length > 0 => "USB power",
            _ => "--",
        };
    }

    /// <summary>True on the last bar (or almost empty), when it's time to swap.</summary>
    public static bool BatteryCritical(long percent, long level)
    {
        if (percent is >= 0 and <= 100) return percent <= 10;
        return (level & 0x10000) == 0 && (level & 0xFFFF) == 1;
    }

    /// <summary>True at 20% or less, or at the bottom bar of the battery icon.</summary>
    public static bool BatteryLow(long percent, long level)
    {
        if (percent is >= 0 and <= 100) return percent <= 20;
        if ((level & 0x10000) != 0) return false; // on USB power
        return (level & 0xFFFF) is 1 or 2 or 6;
    }

    public static string Duration(long seconds)
    {
        if (seconds is < 0 or >= 0xFFFFFFFF) return "--";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
    }

    private static readonly Dictionary<string, string> PhotoWords = new()
    {
        ["Jpeg"] = "JPEG", ["Raw"] = "RAW", ["RawJpeg"] = "RAW+JPEG", ["RawHeif"] = "RAW+HEIF", ["Heif"] = "HEIF",
        ["Uncompression"] = "Uncompressed", ["Compression"] = "Compressed", ["LossLess"] = "Lossless", ["Compressed HQ"] = "Compressed HQ",
        ["ExFine"] = "Extra fine", ["MechanicalShutter"] = "Mechanical", ["ElectronicShutter"] = "Electronic",
        ["CenterWeighted"] = "Center", ["EntireScreenAverage"] = "Entire screen", ["HighLightWeighted"] = "Highlight",
        ["Spot Standard"] = "Spot", ["Spot Large"] = "Large spot", ["OFF"] = "Off", ["ON"] = "On",
        ["RAWAndJPEG"] = "RAW+JPEG", ["JPEGOnly"] = "JPEG", ["RAWOnly"] = "RAW", ["RAWAndHEIF"] = "RAW+HEIF", ["HEIFOnly"] = "HEIF",
        ["3 2"] = "3:2", ["16 9"] = "16:9", ["4 3"] = "4:3", ["1 1"] = "1:1",
    };

    /// <summary>Sony's names for a photo setting, in the words a photographer would use.</summary>
    public static Func<long, string> Photo(string table) => v =>
    {
        string name = Named(table)(v);
        return PhotoWords.GetValueOrDefault(name, name);
    };

    public static string SaveTo(long v) => v switch { 1 => "Computer", 2 => "Card", 3 => "Both", _ => $"0x{v:X}" };

    public static string ComputerCopy(long v) => v switch { 0 => "Full size", 1 => "Small JPEG", _ => $"0x{v:X}" };

    /// <summary>Drive modes, with Sony's bracket names made readable.</summary>
    public static string Drive(long v)
    {
        string name = Named("DriveMode")(v).Replace("Hi Plus", "Hi+").Replace("Timer ", "Self-timer ");
        if (name.StartsWith("Continuous ") && !name.Contains("Bracket")) name = "Burst " + name["Continuous ".Length..];
        var bracket = System.Text.RegularExpressions.Regex.Match(name, @"^(Continuous|Single) Bracket (\d)(\d)Ev (\d)pics(.*)$");
        return bracket.Success
            ? $"{bracket.Groups[1].Value} bracket {bracket.Groups[2].Value}.{bracket.Groups[3].Value} EV, {bracket.Groups[4].Value} shots{bracket.Groups[5].Value}"
            : name;
    }

    /// <summary>What subject recognition looks for.</summary>
    public static string Detect(long v) => Named("RecognitionTarget")(v) switch
    {
        "AnimalBird" => "Animal/bird",
        "CarTrain" => "Car/train",
        var name => name,
    };

    /// <summary>A time in tenths of a second, as the camera reports the time-lapse interval.</summary>
    public static string Interval(long tenths) => tenths < 600 ? $"{tenths / 10.0:0.#} s" : $"{tenths / 600.0:0.#} min";

    /// <summary>How long a time-lapse takes to shoot, and how long it plays for.</summary>
    public static string TimeLapse(long tenths, long shots) =>
        $"Shoots for {Duration(tenths * shots / 10)}. Plays for {shots / 24.0:0.#} s at 24 fps.";

    public static Func<long, string> Named(string table) => v =>
        SdkNames.Values.TryGetValue(table, out var names) && names.TryGetValue(v, out var n) ? n : $"0x{v:X}";
}
