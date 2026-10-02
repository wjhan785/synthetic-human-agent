namespace SyntheticAgent.Input;

/// <summary>
/// Human-like mouse movement on top of <see cref="NativeInput"/>. Paths are generated with the
/// WindMouse algorithm (gravity pulls toward the target, "wind" adds correlated jitter), which yields
/// curved, slightly shaky trajectories with natural momentum overshoot near the end. Movement duration
/// follows Fitts's law (farther / smaller targets take longer). Clicks have a realistic press-hold;
/// scrolling uses variable deltas with occasional scroll-back.
/// </summary>
public sealed class HumanMouse
{
    private const double Sqrt3 = 1.7320508, Sqrt5 = 2.2360680;
    private readonly Random _rng;

    public HumanMouse(int seed) => _rng = new Random(seed);

    /// <summary>Generate a WindMouse path from (x0,y0) to (x1,y1). Pure math — no input sent.
    /// Higher wind + larger steps give a curvier path in fewer, faster hops.</summary>
    public List<(int X, int Y)> PlanPath(int x0, int y0, int x1, int y1,
                                         double gravity = 7, double wind = 6,
                                         double maxStep = 22, double targetArea = 12)
    {
        var pts = new List<(int, int)> { (x0, y0) };
        double cx = x0, cy = y0, vX = 0, vY = 0, wX = 0, wY = 0;
        int guard = 0;

        double dist;
        while ((dist = Math.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy))) >= 1 && guard++ < 100000)
        {
            double w = Math.Min(wind, dist);
            if (dist >= targetArea)
            {
                wX = wX / Sqrt3 + (2 * _rng.NextDouble() - 1) * w / Sqrt5;
                wY = wY / Sqrt3 + (2 * _rng.NextDouble() - 1) * w / Sqrt5;
            }
            else
            {
                wX /= Sqrt3;
                wY /= Sqrt3;
                if (maxStep < 3) maxStep = _rng.NextDouble() * 3 + 3;
                else maxStep /= Sqrt5;
            }

            vX += wX + gravity * (x1 - cx) / dist;
            vY += wY + gravity * (y1 - cy) / dist;

            double vMag = Math.Sqrt(vX * vX + vY * vY);
            if (vMag > maxStep)
            {
                double clip = maxStep / 2 + _rng.NextDouble() * maxStep / 2;
                vX = vX / vMag * clip;
                vY = vY / vMag * clip;
            }

            cx += vX;
            cy += vY;
            pts.Add(((int)Math.Round(cx), (int)Math.Round(cy)));
        }

        if (pts[^1] != (x1, y1)) pts.Add((x1, y1));
        return pts;
    }

    /// <summary>Fitts's-law movement time (ms) for distance D and effective target width W.</summary>
    public static double FittsMs(double distance, double width = 14) =>
        100 + 150 * Math.Log2(1 + distance / Math.Max(1, width));

    /// <summary>
    /// Move the cursor to (x,y) the way a human does: a couple of quick small jitters near the start
    /// (hand settling on the mouse), then a fast, curved flick to the target. Travel is brisk — most
    /// of a real move is over in a fraction of a second — with only light per-step pacing.
    /// </summary>
    public void MoveTo(int x, int y)
    {
        var (sx, sy) = NativeInput.CursorPosition();
        PreMoveJitter(sx, sy);

        var (cx, cy) = NativeInput.CursorPosition();
        var path = PlanPath(cx, cy, x, y);
        double dist = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        // Quick and deliberate: a fraction of the Fitts estimate, clamped to a snappy range.
        double totalMs = Math.Clamp(FittsMs(dist) * 0.40, 90, 550);
        double perStep = totalMs / Math.Max(1, path.Count);

        foreach (var (px, py) in path)
        {
            Control.StopSignal.ThrowIfRequested();
            NativeInput.MouseMoveAbsolute(px, py);
            int ms = (int)Math.Round(perStep * (0.5 + _rng.NextDouble()));
            if (ms > 0) Thread.Sleep(ms);
        }
    }

    /// <summary>A few quick, small random nudges near the start point before the real move.</summary>
    private void PreMoveJitter(int sx, int sy)
    {
        int n = _rng.Next(2, 5);
        for (int i = 0; i < n; i++)
        {
            int jx = sx + _rng.Next(-12, 13);
            int jy = sy + _rng.Next(-12, 13);
            NativeInput.MouseMoveAbsolute(jx, jy);
            Thread.Sleep(_rng.Next(6, 18));
        }
    }

    /// <summary>Left click with a realistic press-hold.</summary>
    public void Click()
    {
        NativeInput.LeftButton(true);
        Sleep(50 + _rng.NextDouble() * 80);
        NativeInput.LeftButton(false);
    }

    /// <summary>Move to (x,y) then click, with a brief settle pause before pressing.</summary>
    public void MoveClick(int x, int y)
    {
        MoveTo(x, y);
        Sleep(60 + _rng.NextDouble() * 140);
        Click();
    }

    /// <summary>
    /// Scroll roughly <paramref name="notches"/> wheel notches (negative = down), in a few variable
    /// bursts with pauses, and an occasional small scroll-back (re-reading a line).
    /// </summary>
    public void Scroll(int notches)
    {
        int remaining = notches;
        int sign = Math.Sign(notches);
        while (Math.Abs(remaining) > 0)
        {
            Control.StopSignal.ThrowIfRequested();
            int burst = Math.Min(Math.Abs(remaining), _rng.Next(1, 4));
            NativeInput.Scroll(sign * burst * 120);
            remaining -= sign * burst;
            Sleep(120 + _rng.NextDouble() * 400);

            if (_rng.NextDouble() < 0.12) // occasional scroll-back
            {
                NativeInput.Scroll(-sign * 120);
                Sleep(200 + _rng.NextDouble() * 400);
            }
        }
    }

    private static void Sleep(double ms) => Thread.Sleep((int)Math.Max(1, ms));
}
