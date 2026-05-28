using System.Threading.Channels;

namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// FR-028 bounded producer-consumer queue between the Org-role-assign write path
/// (producer) and the Org-role fan-out worker (single consumer).
/// </summary>
/// <remarks>
/// <para>Backed by <c>System.Threading.Channels</c> with
/// <c>BoundedChannelOptions(1024) { FullMode = Wait, SingleReader = true,
/// SingleWriter = false }</c>.</para>
/// </remarks>
public interface IOrganizationRoleFanoutQueue
{
    /// <summary>
    /// Enqueue a propagation intent. Returns when the intent is on the queue
    /// (does NOT wait for fan-out completion). Bounded; momentarily blocks the
    /// caller under sustained load.
    /// </summary>
    ValueTask EnqueueAsync(PropagationIntent intent, CancellationToken ct);

    /// <summary>Reader half for the worker. Single consumer expected.</summary>
    ChannelReader<PropagationIntent> Reader { get; }

    /// <summary>
    /// Marks the channel as completed for writes. Subsequent
    /// <see cref="EnqueueAsync"/> calls will throw. Existing intents already on
    /// the channel are still drained by the consumer.
    /// </summary>
    void Complete();

    /// <summary>
    /// Convenience helper: yields every intent currently on the channel until
    /// completion or <paramref name="ct"/> fires. The worker uses this in
    /// production; tests use it to drain after <see cref="Complete"/>.
    /// </summary>
    IAsyncEnumerable<PropagationIntent> ReadAllAsync(CancellationToken ct);
}
