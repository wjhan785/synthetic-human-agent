namespace SyntheticAgent.Timing;

/// <summary>
/// Human timing model — the heart of the entropy score. Inter-action delays come from a two-state
/// Markov mixture: a fast "active" component (short log-normal gaps while working) and a heavy-tailed
/// "thinking" component (the 2–5s+ reading/deciding pauses). Because the state switches stochastically,
/// long pauses appear naturally and non-periodically rather than on a fixed schedule — which is exactly
/// what defeats fixed-interval detection.
///
/// Keystroke timing is separate (<see cref="TypingLatencyMs"/>): digraph-aware per-character gaps with
/// longer pauses after punctuation and for shifted/number keys.
///
/// The RNG seed is stored and logged so any run is reproducible for judges.
/// </summary>
public sealed class DelayModel
{
    public int Seed { get; }
    private readonly Random _rng;

    private enum State { Active, Thinking }
    private State _state = State.Active;

    // Markov transition probabilities (per action). Thinking is entered occasionally and left quickly,
    // so most gaps are short but long pauses recur irregularly.
    private const double PActiveToThinking = 0.10;
    private const double PThinkingToActive = 0.55;

    // Log-normal parameters (natural log of seconds). median = exp(mu).
    private const double ActiveMu = -1.05, ActiveSigma = 0.55;   // median ~0.35s
    private const double ThinkMu = 1.03, ThinkSigma = 0.60;      // median ~2.8s, tail to ~10s+

    private const double TypeMu = -2.12, TypeSigma = 0.40;       // median ~0.12s per keystroke

    public DelayModel(int? seed = null)
    {
        Seed = seed ?? unchecked(Environment.TickCount ^ (int)DateTime.UtcNow.Ticks);
        _rng = new Random(Seed);
    }

    /// <summary>Delay (ms) before the next discrete action, Markov-switched active/thinking.</summary>
    public double NextActionDelayMs()
    {
        double r = _rng.NextDouble();
        if (_state == State.Active && r < PActiveToThinking) _state = State.Thinking;
        else if (_state == State.Thinking && r < PThinkingToActive) _state = State.Active;

        double seconds = _state == State.Active
            ? LogNormal(ActiveMu, ActiveSigma)
            : LogNormal(ThinkMu, ThinkSigma);

        return Math.Max(30.0, seconds * 1000.0);
    }

    /// <summary>Whether the model is currently in a "thinking" pause (for logging/telemetry colour).</summary>
    public bool InThinkingState => _state == State.Thinking;

    /// <summary>Per-keystroke latency (ms) from <paramref name="prev"/> to <paramref name="next"/>.</summary>
    public double TypingLatencyMs(char prev, char next)
    {
        double ms = LogNormal(TypeMu, TypeSigma) * 1000.0;
        double mult = 1.0, add = 0.0;

        // Shifted characters (capitals, symbols) take longer — a second key is held.
        if (char.IsUpper(next) || "!@#$%^&*()_+{}|:\"<>?~".IndexOf(next) >= 0) mult *= 1.25;
        if (char.IsDigit(next)) mult *= 1.15;

        // Pauses after punctuation: sentence ends longer than mid-clause.
        if (prev is '.' or '!' or '?') add += 250 + _rng.NextDouble() * 300;
        else if (prev is ',' or ';' or ':') add += 120 + _rng.NextDouble() * 150;

        if (prev == ' ') mult *= 0.95;                               // mid-flow after a space
        if (prev == next && char.IsLetter(next)) mult *= 0.85;       // double letters are quick

        return Math.Max(25.0, ms * mult + add);
    }

    // Box–Muller standard normal.
    private double NextGaussian()
    {
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private double LogNormal(double mu, double sigma) => Math.Exp(mu + sigma * NextGaussian());
}
