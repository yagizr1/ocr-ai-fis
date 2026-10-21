using FiscOCRApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace FiscOCRApp.Controllers;

[ApiController]
[Route("api/fis")]
public sealed class FisController : ControllerBase
{
    private readonly IDocumentPipelineService _pipeline;

    public FisController(IDocumentPipelineService pipeline) => _pipeline = pipeline;

    /// <summary>Geriye uyumlu senkron okuma — dahili hibrit pipeline kullanır.</summary>
    [HttpPost("oku")]
    [HttpPost("/api/ocr")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> Oku([FromForm] IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Fiş, irsaliye veya fatura görseli seçin." });

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
