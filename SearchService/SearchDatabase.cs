using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace SearchService;

public sealed record SearchOutboxEvent(
    Guid EventId,
    Guid CorrelationId,
    Guid DocumentId,
    string RoutingKey,
    string Payload,
    int AttemptCount,
    Guid LockToken);

public sealed class SearchDatabase(NpgsqlDataSource dataSource, SearchOptions options)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (options.EmbeddingDimensions is < 1 or > 16000)
        {
            throw new InvalidOperationException("Search:EmbeddingDimensions must be between 1 and 16000.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            CREATE EXTENSION IF NOT EXISTS vector;
            CREATE TABLE IF NOT EXISTS search_inbox_events (
                event_id uuid PRIMARY KEY,
                event_type text NOT NULL,
                received_at_utc timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS search_documents (
                tenant_id text NOT NULL,
                document_id uuid NOT NULL,
                correlation_id uuid NOT NULL,
                document_kind text NOT NULL,
                entity_name text NOT NULL DEFAULT '',
                document_date date NULL,
                extraction_data jsonb NOT NULL,
                artifact_reference text NOT NULL,
                indexed_at_utc timestamptz NOT NULL,
                PRIMARY KEY (tenant_id, document_id)
            );
            CREATE INDEX IF NOT EXISTS ix_search_documents_tenant_kind_date
                ON search_documents (tenant_id, document_kind, document_date);
            CREATE INDEX IF NOT EXISTS ix_search_documents_tenant_entity
                ON search_documents (tenant_id, lower(entity_name));
            CREATE TABLE IF NOT EXISTS search_chunks (
                tenant_id text NOT NULL,
                document_id uuid NOT NULL,
                chunk_index integer NOT NULL,
                chunk_text text NOT NULL,
                page_number integer NOT NULL,
                bounding_box jsonb NULL,
                embedding vector({options.EmbeddingDimensions}) NULL,
                search_text tsvector GENERATED ALWAYS AS (to_tsvector('simple', chunk_text)) STORED,
                PRIMARY KEY (tenant_id, document_id, chunk_index),
                FOREIGN KEY (tenant_id, document_id)
                    REFERENCES search_documents(tenant_id, document_id) ON DELETE CASCADE
            );
                DO $dimension_migration$
                DECLARE
                    current_embedding_type text;
                BEGIN
                    SELECT format_type(attribute.atttypid, attribute.atttypmod)
                    INTO current_embedding_type
                    FROM pg_attribute AS attribute
                    WHERE attribute.attrelid = 'search_chunks'::regclass
                      AND attribute.attname = 'embedding'
                      AND NOT attribute.attisdropped;

                    IF current_embedding_type IS DISTINCT FROM 'vector({options.EmbeddingDimensions})' THEN
                        IF EXISTS (SELECT 1 FROM search_chunks WHERE embedding IS NOT NULL) THEN
                            RAISE EXCEPTION 'Search embedding dimension change requires re-embedding existing non-null vectors before changing the schema.';
                        END IF;

                        DROP INDEX IF EXISTS ix_search_chunks_embedding_hnsw;
                        ALTER TABLE search_chunks
                            ALTER COLUMN embedding TYPE vector({options.EmbeddingDimensions})
                            USING embedding::vector({options.EmbeddingDimensions});
                    END IF;
                END
                $dimension_migration$;
            CREATE INDEX IF NOT EXISTS ix_search_chunks_tenant_document
                ON search_chunks (tenant_id, document_id);
            CREATE INDEX IF NOT EXISTS ix_search_chunks_full_text
                ON search_chunks USING gin (search_text);
            CREATE INDEX IF NOT EXISTS ix_search_chunks_embedding_hnsw
                ON search_chunks USING hnsw (embedding vector_cosine_ops)
                WHERE embedding IS NOT NULL;
            CREATE TABLE IF NOT EXISTS search_outbox_events (
                event_id uuid PRIMARY KEY,
                correlation_id uuid NOT NULL,
                document_id uuid NOT NULL,
                routing_key text NOT NULL,
                payload jsonb NOT NULL,
                created_at_utc timestamptz NOT NULL,
                attempt_count integer NOT NULL DEFAULT 0,
                next_attempt_at_utc timestamptz NOT NULL DEFAULT now(),
                lease_until_utc timestamptz NULL,
                lock_token uuid NULL,
                published_at_utc timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_search_outbox_due
                ON search_outbox_events (next_attempt_at_utc, created_at_utc)
                WHERE published_at_utc IS NULL;
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IndexAsync(
        SearchIndexCommand indexCommand,
        ExtractionArtifactResponse artifact,
        IReadOnlyList<SearchChunk> chunks,
        IReadOnlyList<float[]>? embeddings,
        CancellationToken cancellationToken)
    {
        if (chunks.Count == 0 ||
            (embeddings is not null && (chunks.Count != embeddings.Count ||
                embeddings.Any(embedding => embedding.Length != options.EmbeddingDimensions))))
        {
            throw new InvalidDataException("Search chunks and embedding vectors are missing or have mismatched dimensions.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var inbox = new NpgsqlCommand("""
            INSERT INTO search_inbox_events (event_id, event_type)
            VALUES (@event_id, 'document.task.requested')
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id;
            """, connection, transaction))
        {
            inbox.Parameters.AddWithValue("event_id", indexCommand.EventId);
            if (await inbox.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
        }

        var extracted = artifact.ExtractionResult;
        var entityName = GetEntityName(indexCommand.DocumentKind, extracted);
        var documentDate = GetDocumentDate(indexCommand.DocumentKind, extracted);
        await using (var document = new NpgsqlCommand("""
            INSERT INTO search_documents
                (tenant_id, document_id, correlation_id, document_kind, entity_name, document_date,
                 extraction_data, artifact_reference, indexed_at_utc)
            VALUES
                (@tenant_id, @document_id, @correlation_id, @document_kind, @entity_name, @document_date,
                 @extraction_data, @artifact_reference, @indexed_at)
            ON CONFLICT (tenant_id, document_id) DO UPDATE SET
                correlation_id = EXCLUDED.correlation_id,
                document_kind = EXCLUDED.document_kind,
                entity_name = EXCLUDED.entity_name,
                document_date = EXCLUDED.document_date,
                extraction_data = EXCLUDED.extraction_data,
                artifact_reference = EXCLUDED.artifact_reference,
                indexed_at_utc = EXCLUDED.indexed_at_utc;
            """, connection, transaction))
        {
            document.Parameters.AddWithValue("tenant_id", options.TenantId);
            document.Parameters.AddWithValue("document_id", indexCommand.DocumentId);
            document.Parameters.AddWithValue("correlation_id", indexCommand.CorrelationId);
            document.Parameters.AddWithValue("document_kind", indexCommand.DocumentKind);
            document.Parameters.AddWithValue("entity_name", entityName);
            document.Parameters.Add(new NpgsqlParameter("document_date", NpgsqlDbType.Date)
            {
                Value = (object?)documentDate ?? DBNull.Value
            });
            document.Parameters.AddWithValue("extraction_data", NpgsqlDbType.Jsonb, extracted.GetRawText());
            document.Parameters.AddWithValue("artifact_reference", indexCommand.ArtifactReference);
            document.Parameters.AddWithValue("indexed_at", DateTimeOffset.UtcNow);
            await document.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteChunks = new NpgsqlCommand("""
            DELETE FROM search_chunks WHERE tenant_id = @tenant_id AND document_id = @document_id;
            """, connection, transaction))
        {
            deleteChunks.Parameters.AddWithValue("tenant_id", options.TenantId);
            deleteChunks.Parameters.AddWithValue("document_id", indexCommand.DocumentId);
            await deleteChunks.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            var vectorText = embeddings is null ? null : FormatVector(embeddings[index]);
            await using var insertChunk = new NpgsqlCommand("""
                INSERT INTO search_chunks
                    (tenant_id, document_id, chunk_index, chunk_text, page_number, bounding_box, embedding)
                VALUES
                    (@tenant_id, @document_id, @chunk_index, @chunk_text, @page_number, @bounding_box, @embedding::vector);
                """, connection, transaction);
            insertChunk.Parameters.AddWithValue("tenant_id", options.TenantId);
            insertChunk.Parameters.AddWithValue("document_id", indexCommand.DocumentId);
            insertChunk.Parameters.AddWithValue("chunk_index", chunk.ChunkIndex);
            insertChunk.Parameters.AddWithValue("chunk_text", chunk.Text);
            insertChunk.Parameters.AddWithValue("page_number", chunk.PageNumber);
            insertChunk.Parameters.AddWithValue("bounding_box", NpgsqlDbType.Jsonb,
                (object?)chunk.BoundingBox?.GetRawText() ?? DBNull.Value);
            insertChunk.Parameters.AddWithValue("embedding", (object?)vectorText ?? DBNull.Value);
            await insertChunk.ExecuteNonQueryAsync(cancellationToken);
        }

        var completionEventId = Guid.NewGuid();
        var completion = JsonSerializer.Serialize(new
        {
            eventId = completionEventId,
            eventType = "document.task.completed",
            schemaVersion = 1,
            correlationId = indexCommand.CorrelationId,
            documentId = indexCommand.DocumentId,
            taskId = indexCommand.TaskId,
            taskType = "search-index",
            outputReference = indexCommand.ArtifactReference
        });
        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO search_outbox_events
                (event_id, correlation_id, document_id, routing_key, payload, created_at_utc)
            VALUES (@event_id, @correlation_id, @document_id, 'document.task.completed', @payload, @created_at);
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("event_id", completionEventId);
            outbox.Parameters.AddWithValue("correlation_id", indexCommand.CorrelationId);
            outbox.Parameters.AddWithValue("document_id", indexCommand.DocumentId);
            outbox.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, completion);
            outbox.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchDeterministicAsync(
        SearchFilter filter,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = CreateFilteredQuery(connection, null, filter, limit, hybrid: false);
        return await ReadResultsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchHybridAsync(
        SearchFilter filter,
        int limit,
        float[] queryEmbedding,
        CancellationToken cancellationToken)
    {
        if (queryEmbedding.Length != options.EmbeddingDimensions)
        {
            throw new InvalidDataException("Query embedding dimensions do not match Search configuration.");
        }

        limit = Math.Clamp(limit, 1, 100);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = CreateFilteredQuery(connection, FormatVector(queryEmbedding), filter, limit, hybrid: true);
        return await ReadResultsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<SearchOutboxEvent>> ClaimOutboxAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH due AS (
                SELECT event_id FROM search_outbox_events
                WHERE published_at_utc IS NULL AND next_attempt_at_utc <= @now
                  AND (lease_until_utc IS NULL OR lease_until_utc <= @now)
                ORDER BY created_at_utc LIMIT @batch_size FOR UPDATE SKIP LOCKED
            )
            UPDATE search_outbox_events AS outbox
            SET lease_until_utc = @lease_until, lock_token = @token
            FROM due WHERE outbox.event_id = due.event_id
            RETURNING outbox.event_id, outbox.correlation_id, outbox.document_id,
                      outbox.routing_key, outbox.payload::text, outbox.attempt_count, outbox.lock_token;
            """, connection);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batch_size", batchSize);
        command.Parameters.AddWithValue("lease_until", now.Add(lease));
        command.Parameters.AddWithValue("token", token);
        var events = new List<SearchOutboxEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new SearchOutboxEvent(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetGuid(6)));
        }

        return events;
    }

    public Task MarkOutboxPublishedAsync(SearchOutboxEvent message, CancellationToken cancellationToken)
    {
        return UpdateOutboxAsync(message, """
            SET published_at_utc = @now, lease_until_utc = NULL, lock_token = NULL
            """, command => command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow), cancellationToken);
    }

    public Task MarkOutboxFailedAsync(SearchOutboxEvent message, TimeSpan delay, CancellationToken cancellationToken)
    {
        return UpdateOutboxAsync(message, """
            SET attempt_count = attempt_count + 1, next_attempt_at_utc = @next_attempt,
                lease_until_utc = NULL, lock_token = NULL
            """, command => command.Parameters.AddWithValue("next_attempt", DateTimeOffset.UtcNow.Add(delay)), cancellationToken);
    }

    private NpgsqlCommand CreateFilteredQuery(NpgsqlConnection connection, string? embedding, SearchFilter filter, int limit, bool hybrid)
    {
        var score = hybrid ? "1 - (chunk.embedding <=> @embedding::vector)" : "NULL::double precision";
        var order = hybrid ? "chunk.embedding <=> @embedding::vector" : "document.document_date DESC NULLS LAST, document.indexed_at_utc DESC";
        var command = new NpgsqlCommand($"""
            SELECT document.document_id, document.document_kind, document.entity_name, document.document_date,
                   chunk.chunk_text, {score} AS score, chunk.page_number, chunk.bounding_box::text
            FROM search_documents AS document
            JOIN search_chunks AS chunk
              ON chunk.tenant_id = document.tenant_id AND chunk.document_id = document.document_id
            WHERE document.tenant_id = @tenant_id
              AND (@kind IS NULL OR document.document_kind = @kind)
              AND (@entity IS NULL OR lower(document.entity_name) = lower(@entity))
              AND (@date_from IS NULL OR document.document_date >= @date_from)
              AND (@date_to IS NULL OR document.document_date <= @date_to)
              AND (@keyword IS NULL OR chunk.search_text @@ plainto_tsquery('simple', @keyword))
            ORDER BY {order}
            LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("tenant_id", options.TenantId);
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text)
        {
            Value = (object?)filter.DocumentKind ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("entity", NpgsqlDbType.Text)
        {
            Value = (object?)filter.EntityName ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("date_from", NpgsqlDbType.Date)
        {
            Value = (object?)filter.DateFrom ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("date_to", NpgsqlDbType.Date)
        {
            Value = (object?)filter.DateTo ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("keyword", NpgsqlDbType.Text)
        {
            Value = (object?)filter.Keyword ?? DBNull.Value
        });
        command.Parameters.AddWithValue("limit", limit);
        if (hybrid)
        {
            command.Parameters.AddWithValue("embedding", embedding!);
        }

        return command;
    }

    private async Task<IReadOnlyList<SearchResult>> ReadResultsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            JsonElement? boundingBox = null;
            if (!reader.IsDBNull(7))
            {
                using var json = JsonDocument.Parse(reader.GetString(7));
                boundingBox = json.RootElement.Clone();
            }

            results.Add(new SearchResult(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : DateOnly.FromDateTime(reader.GetDateTime(3)),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                new SearchCitation(reader.GetGuid(0), BuildSourceUrl(reader.GetGuid(0), reader.GetInt32(6)), reader.GetInt32(6), boundingBox)));
        }

        return results;
    }

    private string BuildSourceUrl(Guid documentId, int pageNumber)
    {
        var configuredBase = options.SourceDocumentBaseUrl.EndsWith('/')
            ? options.SourceDocumentBaseUrl
            : options.SourceDocumentBaseUrl + "/";
        var documentUri = new Uri(new Uri(configuredBase, UriKind.Absolute), $"api/fileupload/download/{documentId:D}");
        return new UriBuilder(documentUri) { Fragment = $"page={pageNumber}" }.ToString();
    }

    private async Task UpdateOutboxAsync(
        SearchOutboxEvent message,
        string setClause,
        Action<NpgsqlCommand> addParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            UPDATE search_outbox_events
            {setClause}
            WHERE event_id = @event_id AND lock_token = @lock_token AND published_at_utc IS NULL;
            """, connection);
        command.Parameters.AddWithValue("event_id", message.EventId);
        command.Parameters.AddWithValue("lock_token", message.LockToken);
        addParameters(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string FormatVector(IEnumerable<float> vector)
    {
        return "[" + string.Join(',', vector.Select(value => value.ToString("R", CultureInfo.InvariantCulture))) + "]";
    }

    private static string GetEntityName(string kind, JsonElement extraction)
    {
        var property = kind switch
        {
            "Invoice" => "vendorName",
            "Contract" => "parties",
            "IdentityDocument" => "fullName",
            "FinancialStatement" => "entityName",
            _ => "title"
        };
        foreach (var item in extraction.EnumerateObject())
        {
            if (string.Equals(item.Name, property, StringComparison.OrdinalIgnoreCase))
            {
                return item.Value.ValueKind switch
                {
                    JsonValueKind.String => item.Value.GetString() ?? string.Empty,
                    JsonValueKind.Array => string.Join(", ", item.Value.EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value))),
                    _ => string.Empty
                };
            }
        }

        return string.Empty;
    }

    private static DateOnly? GetDocumentDate(string kind, JsonElement extraction)
    {
        var property = kind switch
        {
            "Invoice" => "invoiceDate",
            "Contract" => "effectiveDate",
            "IdentityDocument" => "issueDate",
            "FinancialStatement" => "periodStart",
            _ => string.Empty
        };
        foreach (var item in extraction.EnumerateObject())
        {
            if (string.Equals(item.Name, property, StringComparison.OrdinalIgnoreCase) &&
                item.Value.ValueKind == JsonValueKind.String && DateOnly.TryParse(item.Value.GetString(), out var date))
            {
                return date;
            }
        }

        return null;
    }
}