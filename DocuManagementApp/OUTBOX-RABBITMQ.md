# RabbitMQ outbox publisher

The hosted `OutboxPublisherService` polls unpublished rows from `outbox_messages`, claims rows using PostgreSQL `FOR UPDATE SKIP LOCKED` plus an expiring lease, and publishes them to a durable RabbitMQ topic exchange and durable queue. It waits for publisher confirms before setting `PublishedAtUtc`.

Non-secret broker settings are under `RabbitMq` in `appsettings.json`. Credentials are intentionally not stored there. The application validates that both values are provided and fails at startup if either is missing. For local PowerShell development, create a dedicated RabbitMQ account and use the same credentials for the broker and application process:

```powershell
$env:RABBITMQ_DEFAULT_USER = 'docuingestion'
# Set this from a local secret manager; do not commit the value.
$env:RABBITMQ_DEFAULT_PASS = '<set-a-strong-local-secret>'
$env:RabbitMq__UserName = $env:RABBITMQ_DEFAULT_USER
$env:RabbitMq__Password = $env:RABBITMQ_DEFAULT_PASS
podman compose -f compose.yaml up -d
dotnet run --project DocuManagementApp/DocuManagementApp.csproj
```

The same Compose file works with Docker Compose. It binds AMQP and the management UI to localhost, stores broker data in a named volume, and reports healthy only after `rabbitmq-diagnostics ping` succeeds. Host ports default to 5672 and 15672; set `RABBITMQ_AMQP_HOST_PORT` or `RABBITMQ_MANAGEMENT_HOST_PORT` to avoid local port conflicts. The RabbitMQ management UI is available at `http://localhost:15672` by default.

Do not commit the values of `RABBITMQ_DEFAULT_PASS` or `RabbitMq__Password`. For persistent development credentials, use .NET user secrets for the application and a local, untracked environment file or secret manager for Compose.

| Setting | Default | Purpose |
| --- | --- | --- |
| `RabbitMq:HostName` | `localhost` | Broker hostname |
| `RabbitMq:Port` | `5672` | AMQP port |
| `RabbitMq:UserName` / `Password` | Required | Set with user secrets or `RabbitMq__UserName` / `RabbitMq__Password` environment variables. The app has no credential defaults. |
| `RabbitMq:VirtualHost` | `/` | Broker virtual host |
| `RabbitMq:Exchange` | `documents.events` | Durable topic exchange |
| `RabbitMq:Queue` | `documents.ingested` | Durable queue bound to the event routing key |
| `RabbitMq:RoutingKey` | `document.ingested` | Published event route |
| `RabbitMq:BatchSize` | `20` | Maximum rows claimed per poll |
| `RabbitMq:PollIntervalSeconds` | `2` | Idle/error polling interval |
| `RabbitMq:LeaseSeconds` | `60` | Claim timeout for crashed publishers |
| `RabbitMq:ConfirmTimeoutSeconds` | `10` | Maximum publisher-confirm wait |
| `RabbitMq:RetryBaseSeconds` / `RetryMaxSeconds` | `2` / `300` | Capped exponential retry delay |

Failed events remain in the outbox with incremented `AttemptCount`, `LastAttemptAtUtc`, `NextAttemptAtUtc`, and `LastError`. Retries continue with capped exponential backoff; rows are not deleted or permanently discarded. Operators can inspect and replay them by resetting retry/lease metadata or using a future admin endpoint.

Delivery is **at least once**, not exactly once: the process can stop after RabbitMQ confirms a message but before the database records `PublishedAtUtc`. Every publication sets AMQP `MessageId` to the stable outbox event ID. Consumers must deduplicate that ID transactionally with their own side effects.

Apply database migrations before starting the publisher so the retry/lease columns and partial unpublished-event index exist. The publisher is registered as a hosted service and starts polling with the API process. Exchange, queue, and binding are declared durable by the publisher with the configured names before publishing.
