using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace ExtractionService;

public sealed record ExtractionJob(
    Guid TaskId,
    Guid RequestEventId,
    Guid CorrelationId,
    Guid DocumentId,
    string DocumentKind,
    string? InputReference,
    int AttemptCount,
    Guid LockToken);

public interface IExtractionJobStore
{
    Task<ExtractionJob?> ClaimNextJobAsync(TimeSpan leaseDuration, bool includeLayoutReady, CancellationToken cancellationToken);
    Task MarkLayoutReadyAsync(ExtractionJob job, DocumentLayoutResult layout, CancellationToken cancellationToken);
    Task MarkLayoutFailedAsync(ExtractionJob job, string failureSummary, int maxAttempts, TimeSpan retryDelay, CancellationToken cancellationToken);
    Task MarkExtractionOutcomeAsync(ExtractionJob job, DocumentLayoutResult layout, DocumentExtractionOutcome outcome, CancellationToken cancellationToken);
}

public sealed class ExtractionDatabase(NpgsqlDataSource dataSource) : IExtractionJobStore
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS extraction_inbox_events (
                event_id uuid PRIMARY KEY,
                event_type text NOT NULL,
                payload jsonb NOT NULL,
                received_at_utc timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS extraction_jobs (
                task_id uuid PRIMARY KEY,
                request_event_id uuid NOT NULL REFERENCES extraction_inbox_events(event_id),
                correlation_id uuid NOT NULL,
                document_id uuid NOT NULL,
                document_kind text NOT NULL,
                input_reference text NULL,
                request_payload jsonb NOT NULL,
                status text NOT NULL,
                attempt_count integer NOT NULL DEFAULT 0,
                failure_summary text NULL,
                layout_markdown text NULL,
                layout_json jsonb NULL,
                extraction_json jsonb NULL,
                extraction_candidate text NULL,
                extraction_attempts integer NOT NULL DEFAULT 0,
                next_attempt_at_utc timestamptz NOT NULL DEFAULT now(),
                lease_until_utc timestamptz NULL,
                lock_token uuid NULL,
                created_at_utc timestamptz NOT NULL,
                updated_at_utc timestamptz NOT NULL
            );
            ALTER TABLE extraction_jobs ADD COLUMN IF NOT EXISTS layout_markdown text NULL;
            ALTER TABLE extraction_jobs ADD COLUMN IF NOT EXISTS layout_json jsonb NULL;
            ALTER TABLE extraction_jobs ADD COLUMN IF NOT EXISTS extraction_json jsonb NULL;
            ALTER TABLE extraction_jobs ADD COLUMN IF NOT EXISTS extraction_candidate text NULL;
            ALTER TABLE extraction_jobs ADD COLUMN IF NOT EXISTS extraction_attempts integer NOT NULL DEFAULT 0;
            ALTER TABLE extraction_jobs ADD COLUMN IF NOT EXISTS next_attempt_at_utc timestamptz NOT NULL DEFAULT now();
            ALTER TABLE extraction_jobs ADD COLUMN IF NOT EXISTS lease_until_utc timestamptz NULL;
            ALTER TABLE extraction_jobs ADD COLUMN IF NOT EXISTS lock_token uuid NULL;
            CREATE INDEX IF NOT EXISTS ix_extraction_jobs_status_created
                ON extraction_jobs (status, next_attempt_at_utc, created_at_utc);
            CREATE TABLE IF NOT EXISTS extraction_outbox_events (
                event_id uuid PRIMARY KEY,
                correlation_id uuid NOT NULL,
                document_id uuid NOT NULL,
                event_type text NOT NULL,
                routing_key text NOT NULL,
                payload jsonb NOT NULL,
                created_at_utc timestamptz NOT NULL,
                attempt_count integer NOT NULL DEFAULT 0,
                next_attempt_at_utc timestamptz NOT NULL DEFAULT now(),
                lease_until_utc timestamptz NULL,
                lock_token uuid NULL,
                published_at_utc timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_extraction_outbox_due
                ON extraction_outbox_events (next_attempt_at_utc, created_at_utc)
                WHERE published_at_utc IS NULL;
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> StoreRequestAsync(ExtractionRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var inboxCommand = new NpgsqlCommand("""
            INSERT INTO extraction_inbox_events (event_id, event_type, payload)
            VALUES (@event_id, 'document.task.requested', @payload)
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id;
            """, connection, transaction))
        {
            inboxCommand.Parameters.AddWithValue("event_id", request.EventId);
            inboxCommand.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, request.RawPayload);
            if (await inboxCommand.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
        }

        var now = DateTimeOffset.UtcNow;
        await using (var jobCommand = new NpgsqlCommand("""
            INSERT INTO extraction_jobs
                (task_id, request_event_id, correlation_id, document_id, document_kind,
                 input_reference, request_payload, status, created_at_utc, updated_at_utc)
            VALUES
                (@task_id, @event_id, @correlation_id, @document_id, @document_kind,
                 @input_reference, @payload, 'Queued', @now, @now)
            ON CONFLICT (task_id) DO NOTHING;
            """, connection, transaction))
        {
            jobCommand.Parameters.AddWithValue("task_id", request.TaskId);
            jobCommand.Parameters.AddWithValue("event_id", request.EventId);
            jobCommand.Parameters.AddWithValue("correlation_id", request.CorrelationId);
            jobCommand.Parameters.AddWithValue("document_id", request.DocumentId);
            jobCommand.Parameters.AddWithValue("document_kind", request.DocumentKind);
            jobCommand.Parameters.AddWithValue("input_reference", (object?)request.InputReference ?? DBNull.Value);
            jobCommand.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, request.RawPayload);
            jobCommand.Parameters.AddWithValue("now", now);
            await jobCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<ExtractionJob?> ClaimNextJobAsync(
        TimeSpan leaseDuration,
        bool includeLayoutReady,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lockToken = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH candidate AS (
                SELECT task_id
                FROM extraction_jobs
                WHERE (status = 'Queued' AND next_attempt_at_utc <= @now)
                   OR (status = 'Processing' AND lease_until_utc <= @now)
                         OR (status = 'ReadyForExtraction' AND @include_layout_ready)
                ORDER BY created_at_utc
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE extraction_jobs AS job
            SET status = 'Processing',
                attempt_count = job.attempt_count + 1,
                lease_until_utc = @lease_until,
                lock_token = @lock_token,
                updated_at_utc = @now
            FROM candidate
            WHERE job.task_id = candidate.task_id
            RETURNING job.task_id, job.request_event_id, job.correlation_id, job.document_id,
                      job.document_kind, job.input_reference, job.attempt_count, job.lock_token;
            """, connection, transaction);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("include_layout_ready", includeLayoutReady);
        command.Parameters.AddWithValue("lease_until", now.Add(leaseDuration));
        command.Parameters.AddWithValue("lock_token", lockToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.CloseAsync();
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var job = new ExtractionJob(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
            reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6), reader.GetGuid(7));
        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    public async Task MarkLayoutReadyAsync(ExtractionJob job, DocumentLayoutResult layout, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE extraction_jobs
            SET status = 'ReadyForExtraction',
                layout_markdown = @markdown,
                layout_json = @layout_json,
                failure_summary = NULL,
                lease_until_utc = NULL,
                lock_token = NULL,
                updated_at_utc = @now
            WHERE task_id = @task_id AND lock_token = @lock_token AND status = 'Processing';
            """, connection);
        command.Parameters.AddWithValue("markdown", layout.Markdown);
        command.Parameters.AddWithValue("layout_json", NpgsqlTypes.NpgsqlDbType.Jsonb, layout.RawJson);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("task_id", job.TaskId);
        command.Parameters.AddWithValue("lock_token", job.LockToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"Extraction job '{job.TaskId}' lost its processing lease before layout was saved.");
        }
    }

    public async Task MarkExtractionOutcomeAsync(
        ExtractionJob job,
        DocumentLayoutResult layout,
        DocumentExtractionOutcome outcome,
        CancellationToken cancellationToken)
    {
        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var succeeded = outcome is ExtractionSuccess;
        var eventType = succeeded ? "document.task.completed" : "document.task.failed";
        var outputJson = succeeded ? ((ExtractionSuccess)outcome).Json : null;
        var candidateJson = outcome switch
        {
            ExtractionSuccess success => success.Json,
            ExtractionFailed failed => failed.LastCandidateJson,
            _ => null
        };
        var failureSummary = outcome is ExtractionFailed failedOutcome
            ? string.Join("; ", failedOutcome.Issues.Select(issue => $"{issue.Path}: {issue.Message}"))
            : null;
        var attempts = outcome.Attempts;
        var payload = outcome switch
        {
            ExtractionSuccess success => JsonSerializer.Serialize(new
            {
                eventId,
                eventType,
                schemaVersion = 1,
                correlationId = job.CorrelationId,
                documentId = job.DocumentId,
                taskId = job.TaskId,
                taskType = "extraction",
                documentKind = success.DocumentKind,
                outputReference = (string?)null,
                extractionResult = JsonDocument.Parse(success.Json).RootElement.Clone(),
                attempts
            }),
            ExtractionFailed failed => JsonSerializer.Serialize(new
            {
                eventId,
                eventType,
                schemaVersion = 1,
                correlationId = job.CorrelationId,
                documentId = job.DocumentId,
                taskId = job.TaskId,
                taskType = "extraction",
                documentKind = failed.DocumentKind,
                failureSummary,
                extractionIssues = failed.Issues,
                attempts
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand("""
            UPDATE extraction_jobs
            SET status = @status,
                layout_markdown = @markdown,
                layout_json = @layout_json,
                extraction_json = @extraction_json,
                extraction_candidate = @candidate,
                extraction_attempts = @attempts,
                failure_summary = @failure_summary,
                lease_until_utc = NULL,
                lock_token = NULL,
                updated_at_utc = @now
            WHERE task_id = @task_id AND lock_token = @lock_token AND status = 'Processing';
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("status", succeeded ? "ExtractionSucceeded" : "ExtractionFailed");
            update.Parameters.AddWithValue("markdown", layout.Markdown);
            update.Parameters.AddWithValue("layout_json", NpgsqlDbType.Jsonb, layout.RawJson);
            update.Parameters.AddWithValue("extraction_json", NpgsqlDbType.Jsonb, (object?)outputJson ?? DBNull.Value);
            update.Parameters.AddWithValue("candidate", (object?)candidateJson ?? DBNull.Value);
            update.Parameters.AddWithValue("attempts", attempts);
            update.Parameters.AddWithValue("failure_summary", (object?)failureSummary ?? DBNull.Value);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("task_id", job.TaskId);
            update.Parameters.AddWithValue("lock_token", job.LockToken);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException($"Extraction job '{job.TaskId}' lost its lease before the extraction outcome was saved.");
            }
        }

        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO extraction_outbox_events
                (event_id, correlation_id, document_id, event_type, routing_key, payload, created_at_utc)
            VALUES (@event_id, @correlation_id, @document_id, @event_type, @routing_key, @payload, @now);
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("event_id", eventId);
            outbox.Parameters.AddWithValue("correlation_id", job.CorrelationId);
            outbox.Parameters.AddWithValue("document_id", job.DocumentId);
            outbox.Parameters.AddWithValue("event_type", eventType);
            outbox.Parameters.AddWithValue("routing_key", eventType);
            outbox.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
            outbox.Parameters.AddWithValue("now", now);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkLayoutFailedAsync(
        ExtractionJob job,
        string failureSummary,
        int maxAttempts,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        var retry = job.AttemptCount < maxAttempts;
        var now = DateTimeOffset.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE extraction_jobs
            SET status = @status,
                failure_summary = @failure_summary,
                next_attempt_at_utc = @next_attempt,
                lease_until_utc = NULL,
                lock_token = NULL,
                updated_at_utc = @now
            WHERE task_id = @task_id AND lock_token = @lock_token AND status = 'Processing';
            """, connection, transaction);
        command.Parameters.AddWithValue("status", retry ? "Queued" : "Failed");
        command.Parameters.AddWithValue("failure_summary", failureSummary.Length <= 4000 ? failureSummary : failureSummary[..4000]);
        command.Parameters.AddWithValue("next_attempt", now.Add(retryDelay));
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("task_id", job.TaskId);
        command.Parameters.AddWithValue("lock_token", job.LockToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"Extraction job '{job.TaskId}' lost its processing lease while recording a failure.");
        }

        if (!retry)
        {
            var eventId = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(new
            {
                eventId,
                eventType = "document.task.failed",
                schemaVersion = 1,
                correlationId = job.CorrelationId,
                documentId = job.DocumentId,
                taskId = job.TaskId,
                taskType = "extraction",
                failureSummary,
                attempts = job.AttemptCount
            });
            await using var outbox = new NpgsqlCommand("""
                INSERT INTO extraction_outbox_events
                    (event_id, correlation_id, document_id, event_type, routing_key, payload, created_at_utc)
                VALUES (@event_id, @correlation_id, @document_id, 'document.task.failed', 'document.task.failed', @payload, @now);
                """, connection, transaction);
            outbox.Parameters.AddWithValue("event_id", eventId);
            outbox.Parameters.AddWithValue("correlation_id", job.CorrelationId);
            outbox.Parameters.AddWithValue("document_id", job.DocumentId);
            outbox.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
            outbox.Parameters.AddWithValue("now", now);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ExtractionOutboxEvent>> ClaimOutboxAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lockToken = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH due AS (
                SELECT event_id
                FROM extraction_outbox_events
                WHERE published_at_utc IS NULL
                  AND next_attempt_at_utc <= @now
                  AND (lease_until_utc IS NULL OR lease_until_utc <= @now)
                ORDER BY created_at_utc
                LIMIT @batch_size
                FOR UPDATE SKIP LOCKED
            )
            UPDATE extraction_outbox_events AS outbox
            SET lease_until_utc = @lease_until,
                lock_token = @lock_token
            FROM due
            WHERE outbox.event_id = due.event_id
            RETURNING outbox.event_id, outbox.correlation_id, outbox.document_id,
                      outbox.event_type, outbox.routing_key, outbox.payload::text,
                      outbox.attempt_count, outbox.lock_token;
            """, connection);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batch_size", batchSize);
        command.Parameters.AddWithValue("lease_until", now.Add(leaseDuration));
        command.Parameters.AddWithValue("lock_token", lockToken);
        var events = new List<ExtractionOutboxEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new ExtractionOutboxEvent(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetGuid(7)));
        }

        return events;
    }

    public async Task MarkOutboxPublishedAsync(ExtractionOutboxEvent message, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE extraction_outbox_events
            SET published_at_utc = @now, lease_until_utc = NULL, lock_token = NULL
            WHERE event_id = @event_id AND lock_token = @lock_token AND published_at_utc IS NULL;
            """, connection);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("event_id", message.EventId);
        command.Parameters.AddWithValue("lock_token", message.LockToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkOutboxFailedAsync(
        ExtractionOutboxEvent message,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE extraction_outbox_events
            SET attempt_count = attempt_count + 1,
                next_attempt_at_utc = @next_attempt,
                lease_until_utc = NULL,
                lock_token = NULL
            WHERE event_id = @event_id AND lock_token = @lock_token AND published_at_utc IS NULL;
            """, connection);
        command.Parameters.AddWithValue("next_attempt", DateTimeOffset.UtcNow.Add(retryDelay));
        command.Parameters.AddWithValue("event_id", message.EventId);
        command.Parameters.AddWithValue("lock_token", message.LockToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record ExtractionOutboxEvent(
    Guid EventId,
    Guid CorrelationId,
    Guid DocumentId,
    string EventType,
    string RoutingKey,
    string Payload,
    int AttemptCount,
    Guid LockToken);