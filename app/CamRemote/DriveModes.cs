using System.Text.RegularExpressions;

namespace CamRemote;

/// <summary>
/// Sony's drive modes, sorted into what a person picks: single, burst, self-timer or bracket, then the
/// speed, delay or step within that. The camera offers well over a hundred, most of them brackets.
/// </summary>
public static class DriveModes
{
    public enum Kind { Single, Burst, Timer, Bracket, Other }

    public const long Single = 0x1;

    // Slowest to fastest
    public static readonly long[] BurstSpeeds = [0x10004, 0x10007, 0x10001, 0x10002];
    public static readonly int[] TimerDelays = [2, 5, 10];
    public static readonly int[] TimerShots = [1, 3, 5];
    public static readonly string[] BracketShots = ["2+", "2−", "3", "5", "7", "9"];

    public static Kind KindOf(long v) => v switch
    {
        Single => Kind.Single,
        _ when BurstSpeeds.Contains(v) => Kind.Burst,
        _ when Timer(v) != null => Kind.Timer,
        _ when Bracket(v) != null => Kind.Bracket,
        _ => Kind.Other,
    };

    public static string Name(Kind kind) => kind switch
    {
        Kind.Burst => "Burst",
        Kind.Timer => "Timer",
        Kind.Bracket => "Bracket",
        Kind.Other => "Other",
        _ => "Single",
    };

    /// <summary>"Lo", "Mid", "Hi" or "Hi+".</summary>
    public static string BurstName(long v) => Format.Drive(v).Replace("Burst ", "");

    /// <summary>A self-timer's delay in seconds and how many photos it takes.</summary>
    public static (int Delay, int Shots)? Timer(long v) => v switch
    {
        0x30001 => (2, 1),
        0x30002 => (5, 1),
        0x30003 => (10, 1),
        0x80001 => (10, 3),
        0x80002 => (10, 5),
        0x80003 => (2, 3),
        0x80004 => (2, 5),
        0x80005 => (5, 3),
        0x80006 => (5, 5),
        _ => null,
    };

    private static readonly Regex BracketName = new(@"^(Continuous|Single) Bracket (\d)(\d)Ev (\d)pics( Plus| Minus)?$");

    /// <summary>An exposure bracket: shot one after another or one per press, its step in tenths of a stop, and its shots.</summary>
    public static (bool Continuous, int Step, string Shots)? Bracket(long v)
    {
        var m = BracketName.Match(Format.Named("DriveMode")(v));
        if (!m.Success) return null;
        string shots = m.Groups[4].Value + (m.Groups[5].Value == " Plus" ? "+" : m.Groups[5].Value == " Minus" ? "−" : "");
        return (m.Groups[1].Value == "Continuous", int.Parse(m.Groups[2].Value) * 10 + int.Parse(m.Groups[3].Value), shots);
    }

    public static string StepName(int tenths) => $"{tenths / 10.0:0.0}";

    /// <summary>What to switch to when a kind is picked: the one used last, or an everyday choice.</summary>
    public static long? Default(Kind kind, IReadOnlyCollection<long> choices, long? last)
    {
        if (last is long l && choices.Contains(l) && KindOf(l) == kind) return l;
        long[] everyday = kind switch
        {
            Kind.Single => [Single],
            Kind.Burst => [0x10001, 0x10007, 0x10002, 0x10004],
            Kind.Timer => [0x30002, 0x30001, 0x30003],
            Kind.Bracket => [0x4030A, 0x40307, 0x5000A],
            _ => [],
        };
        return everyday.Cast<long?>().FirstOrDefault(v => choices.Contains(v!.Value)) ?? choices.Cast<long?>().FirstOrDefault(v => KindOf(v!.Value) == kind);
    }

    /// <summary>The timer that matches, or the nearest one the camera has.</summary>
    public static long? PickTimer(IReadOnlyCollection<long> choices, int delay, int shots) =>
        choices.Where(v => Timer(v) != null).OrderBy(v => Timer(v)!.Value.Delay == delay ? 0 : 1)
               .ThenBy(v => Math.Abs(Timer(v)!.Value.Shots - shots)).Cast<long?>().FirstOrDefault();

    /// <summary>The bracket that matches, keeping as much as it can when the exact one isn't there.</summary>
    public static long? PickBracket(IReadOnlyCollection<long> choices, bool continuous, int step, string shots) =>
        choices.Where(v => Bracket(v) is { } b && b.Continuous == continuous).OrderBy(v => Bracket(v)!.Value.Step == step ? 0 : 1)
               .ThenBy(v => Bracket(v)!.Value.Shots == shots ? 0 : Bracket(v)!.Value.Shots == "3" ? 1 : 2)
               .ThenBy(v => Math.Abs(Bracket(v)!.Value.Step - step)).Cast<long?>().FirstOrDefault();
}
