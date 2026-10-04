using Microsoft.Extensions.Options;

namespace ExtractionService;

public sealed class ExtractionProcessingOptions
{
    public bool EnableModelExtraction { get; set; }
    public int PollIntervalSeconds { get; set; } = 2;
    public int LeaseSeconds { get; set; } = 300;
    public int MaxLayoutAttempts { get; set; } = 3;
    public int RetryBaseSeconds { get; set; } = 5;
    public int RetryMaxSeconds { get; set; } = 60;
}

public sealed class ExtractionLayoutPipeline(
    IDocumentContentClient contentClient,
    IDocumentLayoutClient layoutClient,
    DocumentCriticLoop criticLoop,
    IExtractionJobStore jobStore,
    IOptions<ExtractionProcessingOptions> options,
    ILogger<ExtractionLayoutPipeline> logger)
{
    public async Task ProcessAsync(ExtractionJob job, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        try
        {
            var content = await contentClient.GetAsync(job.DocumentId, cancellationToken);
            var layout = await layoutClient.AnalyzeAsync(content, cancellationToken);
            if (!settings.EnableModelExtraction)
            {
                await jobStore.MarkLayoutReadyAsync(job, layout, cancellationToken);
                logger.LogInformation("Layout is ready for extraction task {TaskId} on document {DocumentId}; model extraction is disabled.", job.TaskId, job.DocumentId);
                return;
            }

            var outcome = await criticLoop.RunAsync(job.DocumentKind, layout.Markdown, cancellationToken);
            await jobStore.MarkExtractionOutcomeAsync(job, layout, outcome, cancellationToken);
            logger.LogInformation(
                "Extraction task {TaskId} finished with {OutcomeType} after {Attempts} attempt(s).",
                job.TaskId,
                outcome.GetType().Name,
                outcome.Attempts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var delay = CalculateRetryDelay(job.AttemptCount, settings.RetryBaseSeconds, settings.RetryMaxSeconds);
            await jobStore.MarkLayoutFailedAsync(
                job,
                exception.Message,
                settings.MaxLayoutAttempts,
                delay,
                cancellationToken);
            logger.LogWarning(exception,
                "Layout processing failed for task {TaskId}, attempt {AttemptCount}/{MaxAttempts}.",
                job.TaskId,
                job.AttemptCount,
                settings.MaxLayoutAttempts);
        }
    }

    internal static TimeSpan CalculateRetryDelay(int attemptCount, int baseSeconds, int maxSeconds)
    {
        var seconds = (double)baseSeconds * Math.Pow(2, Math.Clamp(attemptCount - 1, 0, 30));
        return TimeSpan.FromSeconds(Math.Min(seconds, maxSeconds));
    }
}

public sealed class ExtractionJobProcessor(
    IExtractionJobStore jobStore,
    ExtractionLayoutPipeline pipeline,
    IOptions<ExtractionProcessingOptions> options,
    ILogger<ExtractionJobProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await jobStore.ClaimNextJobAsync(
                    TimeSpan.FromSeconds(options.Value.LeaseSeconds),
                    options.Value.EnableModelExtraction,
                    stoppingToken);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
                    continue;
                }

                await pipeline.ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Extraction job polling failed.");
                await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), stoppingToken);
            }
        }
    }
}