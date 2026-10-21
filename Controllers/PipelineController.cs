using FiscOCRApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace FiscOCRApp.Controllers;

[ApiController]
[Route("api/pipeline")]
public sealed class PipelineController : ControllerBase
{
    private readonly IDocumentJobQueue _queue;
    private readonly IDocumentPipelineService _pipeline;

    public PipelineController(IDocumentJobQueue queue, IDocumentPipelineService pipeline)
    {
        _queue = queue;
        _pipeline = pipeline;
    }

    /// <summary>Arka plan kuyruğuna iş ekler; UI donmaz.</summary>
    [HttpPost("jobs")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> Enqueue([FromForm] IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Belge görseli veya PDF seçin." });
        if (file.Length > 12 * 1024 * 1024)
            return BadRequest(new { message = "Dosya 12 MB sınırını aşıyor." });

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        var status = _queue.Enqueue(file.FileName, buffer.ToArray());
        return Accepted(new
        {
            jobId = status.JobId,
            stage = status.Stage.ToString(),
            stageLabel = status.StageLabel,
            progressPercent = status.ProgressPercent,
            pollUrl = $"/api/pipeline/jobs/{status.JobId}"
        });
    }

    [HttpGet("jobs/{jobId}")]
    public IActionResult GetStatus(string jobId)
    {
        if (!_queue.TryGet(jobId, out var status))
            return NotFound(new { message = "İş bulunamadı." });

        return Ok(status);
    }

    [HttpPost("jobs/{jobId}/cancel")]
    public IActionResult Cancel(string jobId)
    {
        if (!_queue.TryCancel(jobId))
            return NotFound(new { message = "İş bulunamadı veya iptal edilemedi." });
        return Ok(new { message = "İptal istendi.", jobId });
    }

    /// <summary>Senkron pipeline (eski istemciler / test için).</summary>
    [HttpPost("process")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> ProcessSync([FromForm] IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Belge görseli veya PDF seçin." });

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _pipeline.ProcessAsync(stream, file.FileName, progress: null, cancellationToken);
            return Ok(result.Document);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }
}
