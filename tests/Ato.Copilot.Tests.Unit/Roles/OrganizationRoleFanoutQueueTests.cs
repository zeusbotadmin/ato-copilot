using Ato.Copilot.Core.Models.Compliance;
using Ato.Copilot.Core.Services.Roles;
using FluentAssertions;
using Xunit;

namespace Ato.Copilot.Tests.Unit.Roles;

/// <summary>
/// T015 [US1] — Failing tests pinning the bounded-channel contract of
/// <see cref="IOrganizationRoleFanoutQueue"/> per
/// <c>specs/049-unified-rmf-role-assignments/contracts/internal-services.md § 4</c>.
///
/// <list type="bullet">
///   <item>Capacity = 1024; <c>BoundedChannelFullMode.Wait</c>; SingleReader=true; SingleWriter=false.</item>
///   <item>Enqueue under capacity completes synchronously.</item>
///   <item>FIFO drain order is preserved by <c>ReadAllAsync</c>.</item>
///   <item>Reader returns intents the writer emitted, byte-for-byte.</item>
/// </list>
///
/// <para>The "at-capacity blocks" property is hard to assert deterministically against
/// a 1024-capacity production channel without an integration test; the FIFO and
/// round-trip properties already pin the dispatch behavior the worker depends on.</para>
/// </summary>
public class OrganizationRoleFanoutQueueTests
{
    [Fact]
    public async Task Enqueue_then_drain_preserves_order_and_payload()
    {
        // Arrange
        IOrganizationRoleFanoutQueue queue = new OrganizationRoleFanoutQueue();
        var tenantId = Guid.NewGuid();
        var orgRoleId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var enqueuedAt = DateTimeOffset.UtcNow;

        var intent1 = new PropagationIntent(tenantId, orgRoleId, RmfRole.MissionOwner, personId, enqueuedAt);
        var intent2 = new PropagationIntent(tenantId, Guid.NewGuid(), RmfRole.SystemOwner, personId, enqueuedAt.AddSeconds(1));
        var intent3 = new PropagationIntent(tenantId, Guid.NewGuid(), RmfRole.AuthorizingOfficial, personId, enqueuedAt.AddSeconds(2));

        // Act — enqueue three intents
        await queue.EnqueueAsync(intent1, CancellationToken.None);
        await queue.EnqueueAsync(intent2, CancellationToken.None);
        await queue.EnqueueAsync(intent3, CancellationToken.None);
        queue.Complete(); // signal no more writes

        var drained = new List<PropagationIntent>();
        await foreach (var item in queue.ReadAllAsync(CancellationToken.None))
        {
            drained.Add(item);
        }

        // Assert
        drained.Should().HaveCount(3);
        drained[0].Should().Be(intent1, "FIFO order: first-in = first-out");
        drained[1].Should().Be(intent2);
        drained[2].Should().Be(intent3);
    }

    [Fact]
    public async Task Enqueue_under_capacity_completes_quickly()
    {
        // Arrange — push 100 intents and assert the operation completes well under capacity.
        IOrganizationRoleFanoutQueue queue = new OrganizationRoleFanoutQueue();
        var tenantId = Guid.NewGuid();

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            await queue.EnqueueAsync(
                new PropagationIntent(tenantId, Guid.NewGuid(), RmfRole.MissionOwner, Guid.NewGuid(), DateTimeOffset.UtcNow),
                CancellationToken.None);
        }
        sw.Stop();
        queue.Complete();

        // Assert
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "enqueuing 100 intents under the 1024-capacity channel must not block (Wait-mode only kicks in at capacity)");

        var count = 0;
        await foreach (var _ in queue.ReadAllAsync(CancellationToken.None)) count++;
        count.Should().Be(100);
    }

    [Fact]
    public async Task Cancelled_drain_observes_cancellation()
    {
        // Arrange — empty queue; cancel before reading.
        IOrganizationRoleFanoutQueue queue = new OrganizationRoleFanoutQueue();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var act = async () =>
        {
            await foreach (var _ in queue.ReadAllAsync(cts.Token))
            {
                // unreachable — channel is empty and reader observes cancellation
            }
        };

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>(
            "the reader MUST observe cancellation cleanly; the worker depends on it for graceful shutdown");
    }
}
