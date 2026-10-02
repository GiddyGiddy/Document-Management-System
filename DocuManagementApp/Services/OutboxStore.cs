using System.Data;
using DocuManagementApp.Data;
using DocuManagementApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DocuManagementApp.Services;

public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task MarkPublishedAsync(Guid eventId, Guid lockToken, CancellationToken cancellationToken);
    Task MarkFailedAsync(Guid eventId, Guid lockToken, string error, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken);
}

public sealed class EfOutboxStore : IOutboxStore
{
    private readonly AppDbContext _dbContext;

    public EfOutboxStore(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(leaseDuration);
        var lockToken = Guid.NewGuid();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var messages = new List<OutboxMessage>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction.GetDbTransaction();
            selectCommand.CommandText = """
                SELECT "Id", "DocumentId", "EventType", "SchemaVersion", "Payload", "CreatedAtUtc", "AttemptCount",
                       "LastAttemptAtUtc", "NextAttemptAtUtc", "LockedUntilUtc", "LockToken", "LastError", "PublishedAtUtc"
                FROM "outbox_messages"
                WHERE "PublishedAtUtc" IS NULL
                  AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= @now)
                  AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= @now)
                ORDER BY "NextAttemptAtUtc" NULLS FIRST, "CreatedAtUtc"
                FOR UPDATE SKIP LOCKED
                LIMIT @batchSize
                """;
            AddParameter(selectCommand, "now", now);
            AddParameter(selectCommand, "batchSize", batchSize);

            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                messages.Add(new OutboxMessage
                {
                    Id = reader.GetGuid(0),
                    DocumentId = reader.GetGuid(1),
                    EventType = reader.GetString(2),
                    SchemaVersion = reader.GetInt32(3),
                    Payload = reader.GetString(4),
                    CreatedAtUtc = reader.GetFieldValue<DateTimeOffset>(5),
                    AttemptCount = reader.GetInt32(6),
                    LastAttemptAtUtc = GetNullableDateTimeOffset(reader, 7),
                    NextAttemptAtUtc = GetNullableDateTimeOffset(reader, 8),
                    LockedUntilUtc = GetNullableDateTimeOffset(reader, 9),
                    LockToken = reader.IsDBNull(10) ? null : reader.GetGuid(10),
                    LastError = reader.IsDBNull(11) ? null : reader.GetString(11),
                    PublishedAtUtc = GetNullableDateTimeOffset(reader, 12)
                });
            }
        }

        foreach (var message in messages)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction.GetDbTransaction();
            updateCommand.CommandText = """
                UPDATE "outbox_messages"
                SET "AttemptCount" = "AttemptCount" + 1,
                    "LastAttemptAtUtc" = @now,
                    "LockedUntilUtc" = @leaseUntil,
                    "LockToken" = @lockToken
                WHERE "Id" = @id
                """;
            AddParameter(updateCommand, "now", now);
            AddParameter(updateCommand, "leaseUntil", leaseUntil);
            AddParameter(updateCommand, "lockToken", lockToken);
            AddParameter(updateCommand, "id", message.Id);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);

            message.AttemptCount++;
            message.LastAttemptAtUtc = now;
            message.LockedUntilUtc = leaseUntil;
            message.LockToken = lockToken;
        }

        await transaction.CommitAsync(cancellationToken);
        return messages;
    }

    public async Task MarkPublishedAsync(Guid eventId, Guid lockToken, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "outbox_messages"
            SET "PublishedAtUtc" = {now},
                "LockedUntilUtc" = NULL,
                "LockToken" = NULL,
                "LastError" = NULL,
                "NextAttemptAtUtc" = NULL
            WHERE "Id" = {eventId}
              AND "LockToken" = {lockToken}
              AND "PublishedAtUtc" IS NULL
            """, cancellationToken);

        if (updated != 1)
        {
            throw new InvalidOperationException($"Outbox event '{eventId}' was not marked published because its claim was lost.");
        }
    }

    public async Task MarkFailedAsync(
        Guid eventId,
        Guid lockToken,
        string error,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken)
    {
        var truncatedError = error.Length <= 4000 ? error : error[..4000];
        var updated = await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "outbox_messages"
            SET "LastError" = {truncatedError},
                "NextAttemptAtUtc" = {nextAttemptAtUtc},
                "LockedUntilUtc" = NULL,
                "LockToken" = NULL
            WHERE "Id" = {eventId}
              AND "LockToken" = {lockToken}
              AND "PublishedAtUtc" IS NULL
            """, cancellationToken);

        if (updated != 1)
        {
            throw new InvalidOperationException($"Outbox event '{eventId}' was not marked failed because its claim was lost.");
        }
    }

    private static DateTimeOffset? GetNullableDateTimeOffset(System.Data.Common.DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
