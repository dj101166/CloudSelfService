using System.Collections.Concurrent;

namespace CloudSelfService.Services;

/// <summary>
/// Per-actor spacing of Test access probes. A singleton, because AdminService is scoped and a page reload makes a new
/// circuit (and so a new AdminService): a cooldown held there would reset on every reload.
/// </summary>
public sealed class ProbeCooldown(TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _last = new();

    /// <summary>True and records the attempt when the actor may probe now; false when one ran within <paramref name="window"/>.</summary>
    public bool TryEnter(Guid actorId, TimeSpan window)
    {
        var now = time.GetUtcNow();
        var allowed = false;
        _last.AddOrUpdate(actorId,
            _ => { allowed = true; return now; },
            (_, last) =>
            {
                allowed = now - last >= window;
                return allowed ? now : last;
            });
        return allowed;
    }
}
