namespace NKeeper.Api.Tests.Infrastructure;

/// <summary>The real time, moved forward on demand: for idle timeouts, without waiting for them.</summary>
public sealed class ManualClock : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
}
