using System.Threading.Channels;

namespace Ato.Copilot.Core.Services.Roles;

/// <summary>
/// Default <see cref="IOrganizationRoleFanoutQueue"/> backed by a bounded
/// <see cref="Channel{T}"/> with capacity 1024 and <c>Wait</c> full-mode.
/// </summary>
/// <remarks>
/// Registered as <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton"/>
/// so there is exactly one channel per process, drained by a single
/// <c>OrganizationRoleFanoutWorker</c>.
/// </remarks>
public sealed class OrganizationRoleFanoutQueue : IOrganizationRoleFanoutQueue
{
    private readonly Channel<PropagationIntent> _channel;

    public OrganizationRoleFanoutQueue()
    {
        _channel = Channel.CreateBounded<PropagationIntent>(
            new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    /// <inheritdoc />
    public ValueTask EnqueueAsync(PropagationIntent intent, CancellationToken ct) =>
        _channel.Writer.WriteAsync(intent, ct);

    /// <inheritdoc />
    public ChannelReader<PropagationIntent> Reader => _channel.Reader;

    /// <inheritdoc />
    public void Complete() => _channel.Writer.TryComplete();

    /// <inheritdoc />
    public IAsyncEnumerable<PropagationIntent> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
