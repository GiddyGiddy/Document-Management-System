using DocuManagementApp.Models;
using DocuManagementApp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DocuManagementApp.Tests;

public sealed class RabbitMqPublisherIntegrationFactAttribute : FactAttribute
{
    public RabbitMqPublisherIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_USERNAME"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_PASSWORD")))
        {
            Skip = "Set OUTBOX_TEST_RABBITMQ_USERNAME and OUTBOX_TEST_RABBITMQ_PASSWORD to run the live RabbitMQ integration test.";
        }
    }
}

public sealed class RabbitMqPublisherIntegrationTests
{
    [RabbitMqPublisherIntegrationFact]
    public async Task PublisherConnectsPublishesAndReceivesBrokerConfirmation()
    {
        var options = new OutboxPublisherOptions
        {
            HostName = Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_HOST") ?? "localhost",
            Port = int.TryParse(Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_PORT"), out var port) ? port : 5672,
            UserName = Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_USERNAME")!,
            Password = Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_PASSWORD")!,
            VirtualHost = Environment.GetEnvironmentVariable("OUTBOX_TEST_RABBITMQ_VHOST") ?? "/",
            Exchange = $"outbox.integration.{Guid.NewGuid():N}",
            Queue = $"outbox.integration.{Guid.NewGuid():N}",
            RoutingKey = "document.ingested",
            ConfirmTimeoutSeconds = 10
        };
        var publisher = new RabbitMqOutboxEventPublisher(
            Options.Create(options),
            NullLogger<RabbitMqOutboxEventPublisher>.Instance);
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            DocumentId = Guid.NewGuid(),
            EventType = "document.ingested",
            SchemaVersion = 1,
            Payload = "{\"eventType\":\"document.ingested\",\"schemaVersion\":1}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        try
        {
            await publisher.PublishAsync(message, CancellationToken.None);
        }
        finally
        {
            var factory = new ConnectionFactory
            {
                HostName = options.HostName,
                Port = options.Port,
                UserName = options.UserName,
                Password = options.Password,
                VirtualHost = options.VirtualHost
            };
            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();
            channel.QueueDelete(options.Queue);
            channel.ExchangeDelete(options.Exchange);
        }
    }
}
