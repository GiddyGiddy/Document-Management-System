using System.Text.Json;
using Npgsql;

namespace OrchestratorService;

public sealed class OrchestratorDatabase(NpgsqlDataSource dataSource, IDocumentClassifier classifier)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS orchestrator_inbox_events (
                event_id uuid PRIMARY KEY,
                event_type text NOT NULL,
                received_at_utc timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS document_workflows (
                document_id uuid PRIMARY KEY,
                correlation_id uuid NOT NULL,
                original_file_name text NOT NULL,
                content_type text NOT NULL,
                document_kind text NOT NULL,
                status text NOT NULL,
                failure_summary text NULL,
                created_at_utc timestamptz NOT NULL,
                updated_at_utc timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_document_workflows_correlation_id
                ON document_workflows (correlation_id);
            CREATE TABLE IF NOT EXISTS document_tasks (
                task_id uuid PRIMARY KEY,
                document_id uuid NOT NULL REFERENCES document_workflows(document_id),
                task_type text NOT NULL,
                status text NOT NULL,
                output_reference text NULL,
                created_at_utc timestamptz NOT NULL,
                completed_at_utc timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_document_tasks_document_status
                ON document_tasks (document_id, status);
            CREATE TABLE IF NOT EXISTS orchestration_outbox (
                event_id uuid PRIMARY KEY,
                correlation_id uuid NOT NULL,
                document_id uuid NOT NULL,
                event_type text NOT NULL,
                routing_key text NOT NULL,
                payload jsonb NOT NULL,
                created_at_utc timestamptz NOT NULL,
                attempt_count integer NOT NULL DEFAULT 0,
                next_attempt_at_utc timestamptz NOT NULL DEFAULT now(),
                locked_until_utc timestamptz NULL,
                lock_token uuid NULL,
                published_at_utc timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_orchestration_outbox_due
                ON orchestration_outbox (next_attempt_at_utc, created_at_utc)
                WHERE published_at_utc IS NULL;
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ProcessAsync(OrchestrationEvent message, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var inboxCommand = new NpgsqlCommand("""
            INSERT INTO orchestrator_inbox_events (event_id, event_type)
            VALUES (@event_id, @event_type)
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id;
            """, connection, transaction))
        {
            inboxCommand.Parameters.AddWithValue("event_id", message.EventId);
            inboxCommand.Parameters.AddWithValue("event_type", message.EventType);
            if (await inboxCommand.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
        }

        switch (message.EventType)
        {
            case "document.ingested":
                if (message.Documents.Count == 0)
                {
                    throw new InvalidDataException("The document.ingested event contains no documents.");
                }

                foreach (var document in message.Documents)
                {
                    await StartWorkflowAsync(connection, transaction, message.CorrelationId, document, cancellationToken);
                }
                break;
            case "document.task.completed":
                await CompleteTaskAsync(connection, transaction, message, cancellationToken);
                break;
            case "document.task.failed":
                await FailTaskAsync(connection, transaction, message, cancellationToken);
                break;
            default:
                await transaction.CommitAsync(cancellationToken);
                return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<OutboxRecord>> ClaimOutboxAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var lockToken = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH due AS (
                SELECT event_id
                FROM orchestration_outbox
                WHERE published_at_utc IS NULL
                  AND next_attempt_at_utc <= @now
                  AND (locked_until_utc IS NULL OR locked_until_utc <= @now)
                ORDER BY created_at_utc
                LIMIT @batch_size
                FOR UPDATE SKIP LOCKED
            )
            UPDATE orchestration_outbox AS outbox
            SET locked_until_utc = @locked_until,
                lock_token = @lock_token
            FROM due
            WHERE outbox.event_id = due.event_id
            RETURNING outbox.event_id, outbox.correlation_id, outbox.document_id,
                      outbox.event_type, outbox.routing_key, outbox.payload::text,
                      outbox.attempt_count, outbox.lock_token;
            """, connection);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batch_size", batchSize);
        command.Parameters.AddWithValue("locked_until", now.Add(leaseDuration));
        command.Parameters.AddWithValue("lock_token", lockToken);

        var records = new List<OutboxRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new OutboxRecord(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetGuid(7)));
        }

        return records;
    }

    public async Task MarkOutboxPublishedAsync(OutboxRecord message, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE orchestration_outbox
            SET published_at_utc = @published_at, locked_until_utc = NULL, lock_token = NULL
            WHERE event_id = @event_id AND lock_token = @lock_token AND published_at_utc IS NULL;
            """, connection);
        command.Parameters.AddWithValue("published_at", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("event_id", message.EventId);
        command.Parameters.AddWithValue("lock_token", message.LockToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkOutboxFailedAsync(
        OutboxRecord message,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE orchestration_outbox
            SET attempt_count = attempt_count + 1,
                next_attempt_at_utc = @next_attempt,
                locked_until_utc = NULL,
                lock_token = NULL
            WHERE event_id = @event_id AND lock_token = @lock_token AND published_at_utc IS NULL;
            """, connection);
        command.Parameters.AddWithValue("next_attempt", nextAttemptAtUtc);
        command.Parameters.AddWithValue("event_id", message.EventId);
        command.Parameters.AddWithValue("lock_token", message.LockToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task StartWorkflowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid correlationId,
        IngestedDocument document,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var kind = classifier.Classify(document.OriginalFileName, document.ContentType);
        await using var workflowCommand = new NpgsqlCommand("""
            INSERT INTO document_workflows
                (document_id, correlation_id, original_file_name, content_type, document_kind, status, created_at_utc, updated_at_utc)
            VALUES (@document_id, @correlation_id, @file_name, @content_type, @document_kind, 'Processing', @now, @now)
            ON CONFLICT (document_id) DO NOTHING
            RETURNING document_id;
            """, connection, transaction);
        workflowCommand.Parameters.AddWithValue("document_id", document.DocumentId);
        workflowCommand.Parameters.AddWithValue("correlation_id", correlationId);
        workflowCommand.Parameters.AddWithValue("file_name", document.OriginalFileName);
        workflowCommand.Parameters.AddWithValue("content_type", document.ContentType);
        workflowCommand.Parameters.AddWithValue("document_kind", kind.ToString());
        workflowCommand.Parameters.AddWithValue("now", now);
        if (await workflowCommand.ExecuteScalarAsync(cancellationToken) is null)
        {
            return;
        }

        await AddTaskAsync(connection, transaction, correlationId, document.DocumentId, kind, "extraction", null, now, cancellationToken);
    }

    private static async Task CompleteTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrchestrationEvent message,
        CancellationToken cancellationToken)
    {
        if (message.TaskId is null)
        {
            throw new InvalidDataException("The task.completed event is missing taskId.");
        }

        await using var update = new NpgsqlCommand("""
            UPDATE document_tasks
            SET status = 'Completed', output_reference = @output_reference, completed_at_utc = @now
            WHERE task_id = @task_id AND status = 'Requested'
            RETURNING document_id, task_type;
            """, connection, transaction);
        update.Parameters.AddWithValue("output_reference", (object?)message.OutputReference ?? DBNull.Value);
        update.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        update.Parameters.AddWithValue("task_id", message.TaskId.Value);
        await using var reader = await update.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.CloseAsync();
            throw new InvalidDataException($"The task.completed event references an unknown or inactive task '{message.TaskId}'.");
        }

        var documentId = reader.GetGuid(0);
        var taskType = reader.GetString(1);
        await reader.CloseAsync();

        if (taskType == "extraction")
        {
            var workflow = await GetWorkflowAsync(connection, transaction, documentId, cancellationToken);
            if (workflow is not null)
            {
                var now = DateTimeOffset.UtcNow;
                await AddTaskAsync(connection, transaction, workflow.Value.CorrelationId, documentId,
                    workflow.Value.Kind, "search-index", message.OutputReference, now, cancellationToken);
                if (workflow.Value.Kind is DocumentKind.Contract or DocumentKind.Invoice or DocumentKind.FinancialStatement)
                {
                    await AddTaskAsync(connection, transaction, workflow.Value.CorrelationId, documentId,
                        workflow.Value.Kind, "semantic-review", message.OutputReference, now, cancellationToken);
                }
            }
        }

        await UpdateWorkflowCompletionAsync(connection, transaction, documentId, cancellationToken);
    }

    private static async Task FailTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OrchestrationEvent message,
        CancellationToken cancellationToken)
    {
        if (message.TaskId is null || message.DocumentId is null)
        {
            throw new InvalidDataException("The task.failed event is missing taskId or documentId.");
        }

        var now = DateTimeOffset.UtcNow;
        await using var taskCommand = new NpgsqlCommand("""
            UPDATE document_tasks SET status = 'Failed', completed_at_utc = @now
            WHERE task_id = @task_id AND document_id = @document_id;
            """, connection, transaction);
        taskCommand.Parameters.AddWithValue("now", now);
        taskCommand.Parameters.AddWithValue("task_id", message.TaskId.Value);
        taskCommand.Parameters.AddWithValue("document_id", message.DocumentId.Value);
        var updated = await taskCommand.ExecuteNonQueryAsync(cancellationToken);
        if (updated == 0)
        {
            throw new InvalidDataException("The task.failed event references an unknown task.");
        }

        await using var workflowCommand = new NpgsqlCommand("""
            UPDATE document_workflows
            SET status = 'Failed', failure_summary = @failure_summary, updated_at_utc = @now
            WHERE document_id = @document_id;
            """, connection, transaction);
        workflowCommand.Parameters.AddWithValue("failure_summary", (object?)message.FailureSummary ?? "A downstream task failed.");
        workflowCommand.Parameters.AddWithValue("now", now);
        workflowCommand.Parameters.AddWithValue("document_id", message.DocumentId.Value);
        await workflowCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid correlationId,
        Guid documentId,
        DocumentKind kind,
        string taskType,
        string? inputReference,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var taskId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await using (var taskCommand = new NpgsqlCommand("""
            INSERT INTO document_tasks (task_id, document_id, task_type, status, created_at_utc)
            VALUES (@task_id, @document_id, @task_type, 'Requested', @now);
            """, connection, transaction))
        {
            taskCommand.Parameters.AddWithValue("task_id", taskId);
            taskCommand.Parameters.AddWithValue("document_id", documentId);
            taskCommand.Parameters.AddWithValue("task_type", taskType);
            taskCommand.Parameters.AddWithValue("now", now);
            await taskCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var payload = JsonSerializer.Serialize(new
        {
            eventId,
            eventType = "document.task.requested",
            schemaVersion = 1,
            correlationId,
            documentId,
            taskId,
            taskType,
            documentKind = kind.ToString(),
            inputReference,
            requestedAtUtc = now
        }, JsonOptions);
        await using var outboxCommand = new NpgsqlCommand("""
            INSERT INTO orchestration_outbox
                (event_id, correlation_id, document_id, event_type, routing_key, payload, created_at_utc)
            VALUES (@event_id, @correlation_id, @document_id, 'document.task.requested', @routing_key, @payload, @now);
            """, connection, transaction);
        outboxCommand.Parameters.AddWithValue("event_id", eventId);
        outboxCommand.Parameters.AddWithValue("correlation_id", correlationId);
        outboxCommand.Parameters.AddWithValue("document_id", documentId);
        outboxCommand.Parameters.AddWithValue("routing_key", $"document.task.{taskType}.requested");
        outboxCommand.Parameters.AddWithValue("payload", NpgsqlTypes.NpgsqlDbType.Jsonb, payload);
        outboxCommand.Parameters.AddWithValue("now", now);
        await outboxCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(Guid CorrelationId, DocumentKind Kind)?> GetWorkflowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT correlation_id, document_kind FROM document_workflows WHERE document_id = @document_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("document_id", documentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetGuid(0), Enum.Parse<DocumentKind>(reader.GetString(1)))
            : null;
    }

    private static async Task UpdateWorkflowCompletionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE document_workflows
            SET status = CASE WHEN EXISTS (
                    SELECT 1 FROM document_tasks
                    WHERE document_id = @document_id AND status <> 'Completed'
                ) THEN 'Processing' ELSE 'Completed' END,
                updated_at_utc = @now
            WHERE document_id = @document_id AND status <> 'Failed';
            """, connection, transaction);
        command.Parameters.AddWithValue("document_id", documentId);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record OutboxRecord(
    Guid EventId,
    Guid CorrelationId,
    Guid DocumentId,
    string EventType,
    string RoutingKey,
    string Payload,
    int AttemptCount,
    Guid LockToken);