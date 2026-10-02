using DocuManagementApp.Models;
using DocuManagementApp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocuManagementApp.Tests;

public sealed class OutboxPublisherTests
{
    [Fact]
    public async Task PublisherMarksOutboxEventPublishedOnlyAfterPublishSucceeds()
    {
        var message = CreateMessage();
        var store = new FakeOutboxStore([message]);
        var publisher = new FakeOutboxPublisher();
        using var provider = CreateProvider(store);
        var service = CreatePublisherService(provider, publisher);

        var publishedCount = await service.PublishBatchAsync(CancellationToken.None);

        Assert.Equal(1, publishedCount);
        Assert.Equal([message.Id], publisher.PublishedEventIds);
        Assert.Equal([message.Id], store.PublishedEventIds);
        Assert.Empty(store.FailedEvents);
    }

    [Fact]
    public async Task PublisherRetainsFailedEventAndSchedulesExponentialRetry()
    {
        var message = CreateMessage(attemptCount: 2);
        var store = new FakeOutboxStore([message]);
        var publisher = new FakeOutboxPublisher { ExceptionToThrow = new IOException("Broker is unavailable.") };
        using var provider = CreateProvider(store);
        var service = CreatePublisherService(provider, publisher);
        var beforePublish = DateTimeOffset.UtcNow;

        var publishedCount = await service.PublishBatchAsync(CancellationToken.None);

        var failedEvent = Assert.Single(store.FailedEvents);
        Assert.Equal(0, publishedCount);
        Assert.Equal(message.Id, failedEvent.EventId);
        Assert.Equal(message.LockToken, failedEvent.LockToken);
        Assert.Contains("Broker is unavailable", failedEvent.Error);
        Assert.InRange(failedEvent.NextAttemptAtUtc, beforePublish.AddSeconds(3), DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Empty(store.PublishedEventIds);
        Assert.Null(message.PublishedAtUtc);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(10, 30)]
    public void RetryDelayUsesCappedExponentialBackoff(int attemptCount, int expectedSeconds)
    {
        var delay = OutboxPublisherService.CalculateRetryDelay(attemptCount, baseSeconds: 2, maxSeconds: 30);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    private static OutboxMessage CreateMessage(int attemptCount = 1)
    {
        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            DocumentId = Guid.NewGuid(),
            EventType = "document.ingested",
            SchemaVersion = 1,
            Payload = "{\"eventType\":\"document.ingested\"}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            AttemptCount = attemptCount,
            LockToken = Guid.NewGuid(),
            LockedUntilUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        };
    }

    private static ServiceProvider CreateProvider(FakeOutboxStore store)
    {
        var services = new ServiceCollection();
        services.AddScoped<IOutboxStore>(_ => store);
        return services.BuildServiceProvider();
    }

    private static OutboxPublisherService CreatePublisherService(IServiceProvider provider, FakeOutboxPublisher publisher)
    {
        var options = Options.Create(new OutboxPublisherOptions
        {
            BatchSize = 10,
            LeaseSeconds = 60,
            RetryBaseSeconds = 2,
            RetryMaxSeconds = 30
        });
        return new OutboxPublisherService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            publisher,
            options,
            NullLogger<OutboxPublisherService>.Instance);
    }

    private sealed class FakeOutboxPublisher : IOutboxEventPublisher
    {
        public List<Guid> PublishedEventIds { get; } = [];
        public Exception? ExceptionToThrow { get; init; }

        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            PublishedEventIds.Add(message.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOutboxStore(IReadOnlyList<OutboxMessage> messages) : IOutboxStore
    {
        public List<Guid> PublishedEventIds { get; } = [];
        public List<(Guid EventId, Guid LockToken, string Error, DateTimeOffset NextAttemptAtUtc)> FailedEvents { get; } = [];

        public Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            return Task.FromResult(messages);
        }

        public Task MarkPublishedAsync(Guid eventId, Guid lockToken, CancellationToken cancellationToken)
        {
            PublishedEventIds.Add(eventId);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid eventId, Guid lockToken, string error, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken)
        {
            FailedEvents.Add((eventId, lockToken, error, nextAttemptAtUtc));
            return Task.CompletedTask;
        }
    }
}
