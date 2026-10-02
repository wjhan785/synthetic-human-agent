using SyntheticAgent.Timing;

namespace SyntheticAgent.Input;

/// <summary>Raised when the target window loses focus mid-type, so the caller can abort safely.</summary>
public sealed class FocusLostException : Exception
{
    public FocusLostException() : base("Target window lost focus during typing.") { }
}

/// <summary>
/// Human-like typing on top of <see cref="NativeInput"/> + <see cref="DelayModel"/>:
/// digraph-aware per-keystroke latency, longer pauses after punctuation/newlines, occasional brief
/// "think" pauses, and a small adjacent-key typo model (wrong key → notice → backspace → correct).
/// A foreground guard runs before every keystroke so stray input can never land in another window.
/// </summary>
public sealed class HumanTyping
{
    private readonly DelayModel _timing;
    private readonly Random _rng;
    private readonly Func<bool> _focusOk;

    public double TypoRate { get; init; } = 0.02;
    public double ThinkPauseRate { get; init; } = 0.03;

    public HumanTyping(DelayModel timing, Func<bool> focusOk)
    {
        _timing = timing;
        _focusOk = focusOk;
        _rng = new Random(unchecked(timing.Seed ^ 0x5bd1e995));
    }

    public readonly record struct Stats(int Chars, int Typos, long ElapsedMs)
    {
        public double EffectiveWpm => ElapsedMs > 0 ? Chars / 5.0 / (ElapsedMs / 60000.0) : 0;
    }

    /// <summary>Type <paramref name="text"/> character by character with human timing and typos.</summary>
    public Stats TypeText(string text)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int typos = 0;
        char prev = ' ';

        foreach (char c in text)
        {
            Guard();

            if (c == '\n')
            {
                Sleep(180 + _rng.NextDouble() * 500); // pause at line end
                NativeInput.KeyPress(Vk.Return);
                prev = ' ';
                continue;
            }

            // Occasional adjacent-key typo on letters, then correct it.
            if (char.IsLetter(c) && _rng.NextDouble() < TypoRate)
            {
                char wrong = AdjacentKey(c);
                Sleep(_timing.TypingLatencyMs(prev, wrong));
                NativeInput.UnicodeChar(wrong);
                Sleep(150 + _rng.NextDouble() * 400); // notice the mistake
                Guard();
                NativeInput.KeyPress(Vk.Back);
                Sleep(60 + _rng.NextDouble() * 140);
                typos++;
            }

            Sleep(_timing.TypingLatencyMs(prev, c));
            Guard();
            NativeInput.UnicodeChar(c);
            prev = c;

            // Rare brief pause to "think", bounded so typing stays fluid.
            if (_rng.NextDouble() < ThinkPauseRate)
                Sleep(400 + _rng.NextDouble() * 1200);
        }

        sw.Stop();
        return new Stats(text.Length, typos, sw.ElapsedMilliseconds);
    }

    private void Guard()
    {
        Control.StopSignal.ThrowIfRequested(); // near-instant quit from mid-typing
        if (!_focusOk())
            throw new FocusLostException();
    }

    private static void Sleep(double ms) => Thread.Sleep((int)Math.Max(1, ms));

    // QWERTY physical neighbours for the typo model.
    private static readonly Dictionary<char, string> Neighbours = new()
    {
        ['q'] = "wa", ['w'] = "qes", ['e'] = "wrd", ['r'] = "etf", ['t'] = "ryg",
        ['y'] = "tuh", ['u'] = "yij", ['i'] = "uok", ['o'] = "ipl", ['p'] = "ol",
        ['a'] = "qsz", ['s'] = "adwx", ['d'] = "sfec", ['f'] = "dgrv", ['g'] = "fhtb",
        ['h'] = "gjyn", ['j'] = "hkum", ['k'] = "jli", ['l'] = "kop",
        ['z'] = "ax", ['x'] = "zcs", ['c'] = "xvd", ['v'] = "cbf", ['b'] = "vng",
        ['n'] = "bmh", ['m'] = "nj",
    };

    private char AdjacentKey(char c)
    {
        char lower = char.ToLowerInvariant(c);
        if (Neighbours.TryGetValue(lower, out string? adj) && adj.Length > 0)
        {
            char pick = adj[_rng.Next(adj.Length)];
            return char.IsUpper(c) ? char.ToUpperInvariant(pick) : pick;
        }
        return c; // no mapping: effectively a harmless double-type then backspace
    }
}
