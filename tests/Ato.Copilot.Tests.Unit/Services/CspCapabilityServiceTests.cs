using Ato.Copilot.Agents.Compliance.Services;
using Ato.Copilot.Core.Data.Context;
using Ato.Copilot.Core.Interfaces.Compliance;
using Ato.Copilot.Core.Models.Compliance;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Ato.Copilot.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="CspCapabilityService"/> — NeedsReview gate (#160) and remap (#161).
/// Tests were written FIRST (TDD) and the service was implemented to make them green.
/// </summary>
public class CspCapabilityServiceTests : IAsyncDisposable
{
    private readonly DbContextOptions<AtoCopilotContext> _options;
    private readonly AtoCopilotContext _context;
    private readonly Mock<ICapabilityHistoryService> _historyMock;
    private readonly CspCapabilityService _service;

    public CspCapabilityServiceTests()
    {
        _options = new DbContextOptionsBuilder<AtoCopilotContext>()
            .UseInMemoryDatabase($"CspCapability_{Guid.NewGuid():N}")
            .Options;

        _context = new AtoCopilotContext(_options);

        var mockFactory = new Mock<IDbContextFactory<AtoCopilotContext>>();
        mockFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AtoCopilotContext(_options));

        _historyMock = new Mock<ICapabilityHistoryService>();
        _historyMock
            .Setup(h => h.RecordEventAsync(
                It.IsAny<string>(), It.IsAny<CapabilityHistoryEventType>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new CspCapabilityService(
            mockFactory.Object,
            _historyMock.Object,
            Mock.Of<ILogger<CspCapabilityService>>());
    }

    public async ValueTask DisposeAsync() => await _context.DisposeAsync();

    // ─── #160: NeedsReview gate ───────────────────────────────────────────────

    [Fact]
    public async Task CreateCapability_SetsNeedsReviewTrue()
    {
        // Act
        var cap = await _service.CreateCapabilityAsync(
            name: "My Capability",
            description: "Test",
            parentCapabilityId: null,
            createdBy: "user-1");

        // Assert
        cap.NeedsReview.Should().BeTrue("manual creates must trigger the NeedsReview gate");
        cap.Status.Should().Be(CapabilityStatus.NeedsReview);
    }

    [Fact]
    public async Task CreateCapability_RecordsNeedsReviewFlaggedEvent()
    {
        // Act
        var cap = await _service.CreateCapabilityAsync("Cap X", null, null, "user-2");

        // Assert — history event should be recorded
        _historyMock.Verify(h => h.RecordEventAsync(
            cap.Id,
            CapabilityHistoryEventType.NeedsReviewFlagged,
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClearReview_SetsStatusActive()
    {
        // Arrange — create a capability to clear
        var cap = await _service.CreateCapabilityAsync("Need Review Cap", null, null, "user-3");

        // Act
        var updated = await _service.ClearReviewAsync(cap.Id, "reviewer-1");

        // Assert
        updated.NeedsReview.Should().BeFalse();
        updated.Status.Should().Be(CapabilityStatus.Active);
    }

    [Fact]
    public async Task ClearReview_RecordsReviewClearedEvent()
    {
        // Arrange
        var cap = await _service.CreateCapabilityAsync("Need Review Cap 2", null, null, "user-4");

        // Act
        await _service.ClearReviewAsync(cap.Id, "reviewer-2");

        // Assert
        _historyMock.Verify(h => h.RecordEventAsync(
            cap.Id,
            CapabilityHistoryEventType.ReviewCleared,
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
