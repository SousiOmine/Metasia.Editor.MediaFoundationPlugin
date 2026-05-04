namespace MediaFoundationPlugin;

internal static class MediaFoundationDuration
{
    public static bool TryConvertDuration100ns(object? value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        long durationTicks = value switch
        {
            long longValue when longValue > 0 => longValue,
            ulong ulongValue when ulongValue > 0 && ulongValue <= long.MaxValue => (long)ulongValue,
            int intValue when intValue > 0 => intValue,
            uint uintValue when uintValue > 0 => uintValue,
            short shortValue when shortValue > 0 => shortValue,
            ushort ushortValue when ushortValue > 0 => ushortValue,
            byte byteValue when byteValue > 0 => byteValue,
            _ => 0
        };

        if (durationTicks <= 0)
        {
            return false;
        }

        duration = TimeSpan.FromTicks(durationTicks);
        return true;
    }
}
