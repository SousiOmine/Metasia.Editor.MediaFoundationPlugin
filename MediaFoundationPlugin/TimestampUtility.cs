namespace MediaFoundationPlugin;

internal static class TimestampUtility
{
    private const long HundredNanosecondsPerSecond = 10_000_000;

    public static long ConvertToTimestamp100ns(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return 0;
        }

        double timestamp = time.TotalSeconds * HundredNanosecondsPerSecond;
        if (!double.IsFinite(timestamp) || timestamp <= 0)
        {
            return 0;
        }

        if (timestamp >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return (long)Math.Round(timestamp);
    }

    public static long ResolveFrameDurationTicks100ns(double fps)
    {
        if (!double.IsFinite(fps) || fps <= 0)
        {
            return TimeSpan.FromMilliseconds(16).Ticks;
        }

        double frameDuration = TimeSpan.TicksPerSecond / fps;
        if (!double.IsFinite(frameDuration) || frameDuration < 1)
        {
            return 1;
        }

        if (frameDuration >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return (long)Math.Round(frameDuration);
    }
}