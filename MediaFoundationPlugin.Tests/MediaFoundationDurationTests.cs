namespace MediaFoundationPlugin.Tests;

public class MediaFoundationDurationTests
{
    [Test]
    public void TryConvertDuration100ns_AcceptsLongDuration()
    {
        bool converted = MediaFoundationDuration.TryConvertDuration100ns(10_000_000L, out TimeSpan duration);

        Assert.That(converted, Is.True);
        Assert.That(duration, Is.EqualTo(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void TryConvertDuration100ns_AcceptsUnsignedLongDuration()
    {
        bool converted = MediaFoundationDuration.TryConvertDuration100ns(10_000_000UL, out TimeSpan duration);

        Assert.That(converted, Is.True);
        Assert.That(duration, Is.EqualTo(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void TryConvertDuration100ns_RejectsZeroDuration()
    {
        bool converted = MediaFoundationDuration.TryConvertDuration100ns(0UL, out TimeSpan duration);

        Assert.That(converted, Is.False);
        Assert.That(duration, Is.EqualTo(TimeSpan.Zero));
    }
}
