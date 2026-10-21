using System.Collections.Concurrent;
using System.Threading.Channels;
using FiscOCRApp.Models;

namespace FiscOCRApp.Services;

public interface IDocumentJobQueue
{
    PipelineJobStatus Enqueue(string fileName, byte[] content);
    bool TryGet(string jobId, out PipelineJobStatus status);
    bool TryCancel(string jobId);
}

public sealed class DocumentJobQueue : BackgroundService, IDocumentJobQueue
{
    private readonly Channel<QueuedWork> _channel = Channel.CreateUnbounded<QueuedWork>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, PipelineJobStatus> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DocumentJobQueue> _logger;

    public DocumentJobQueue(IServiceScopeFactory scopeFactory, ILogger<DocumentJobQueue> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public PipelineJobStatus Enqueue(string fileName, byte[] content)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var status = new PipelineJobStatus
        {
            JobId = jobId,
            Stage = PipelineStage.Queued,
            StageLabel = PipelineStageLabels.Get(PipelineStage.Queued),
            ProgressPercent = PipelineStageLabels.Percent(PipelineStage.Queued),
            Message = fileName
        };
        status.StageLog.Add($"{DateTime.Now:HH:mm:ss} · Kuyruğa alındı: {fileName}");
        _jobs[jobId] = status;

        var cts = new CancellationTokenSource();
        _cancellations[jobId] = cts;
        _channel.Writer.TryWrite(new QueuedWork(jobId, fileName, content, cts));
        return status;
    }

    public bool TryGet(string jobId, out PipelineJobStatus status) =>
        _jobs.TryGetValue(jobId, out status!);

    public bool TryCancel(string jobId)
    {
        if (!_cancellations.TryGetValue(jobId, out var cts))
            return false;
        cts.Cancel();
        Update(jobId, PipelineStage.Failed, "İşlem iptal edildi.");
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessOneAsync(work, stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Pipeline job başarısız. JobId={JobId}", work.JobId);
                Update(work.JobId, PipelineStage.Failed, exception.Message);
            }
            finally
            {
                if (_cancellations.TryRemove(work.JobId, out var cts))
                    cts.Dispose();
            }
        }
    }

    private async Task ProcessOneAsync(QueuedWork work, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, work.Cts.Token);
        var token = linked.Token;

        Update(work.JobId, PipelineStage.Uploading, "Dosya alındı");
        var progress = new Progress<PipelineStage>(stage => Update(work.JobId, stage, null));

        await using var scope = _scopeFactory.CreateAsyncScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IDocumentPipelineService>();

        await using var stream = new MemoryStream(work.Content, writable: false);
        var result = await pipeline.ProcessAsync(stream, work.FileName, progress, token);

        if (!_jobs.TryGetValue(work.JobId, out var status))
            return;

        status.Result = result.Document;
        status.Message =
            $"OCR {result.OcrMs}ms · Regex {result.RegexMs}ms · AI {result.LlmMs}ms · {result.PipelinePath}";
        status.Stage = PipelineStage.Completed;
        status.StageLabel = PipelineStageLabels.Get(PipelineStage.Completed);
        status.ProgressPercent = 100;
        status.UpdatedAt = DateTimeOffset.UtcNow;
        status.StageLog.Add($"{DateTime.Now:HH:mm:ss} · Tamamlandı ({result.PipelinePath})");
    }

    private void Update(string jobId, PipelineStage stage, string? message)
    {
        if (!_jobs.TryGetValue(jobId, out var status))
            return;
        status.Stage = stage;
        status.StageLabel = PipelineStageLabels.Get(stage);
        status.ProgressPercent = PipelineStageLabels.Percent(stage);
        if (!string.IsNullOrWhiteSpace(message))
            status.Message = message;
        status.UpdatedAt = DateTimeOffset.UtcNow;
        status.StageLog.Add($"{DateTime.Now:HH:mm:ss} · {status.StageLabel}"
            + (string.IsNullOrWhiteSpace(message) ? "" : $" — {message}"));
        if (stage == PipelineStage.Failed)
            status.Error = message ?? "İşlem başarısız.";
    }

    private sealed record QueuedWork(string JobId, string FileName, byte[] Content, CancellationTokenSource Cts);
}
