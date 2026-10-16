using FiscOCRApp.Models;
using FiscOCRApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace FiscOCRApp.Controllers;

[ApiController]
[Route("api/fis")]
public sealed class FisController : ControllerBase
{
    private readonly IOcrService _ocr;
    private readonly IOllamaService _ollama;
    private readonly ILogger<FisController> _logger;

    public FisController(IOcrService ocr, IOllamaService ollama, ILogger<FisController> logger)
    {
        _ocr = ocr;
        _ollama = ollama;
        _logger = logger;
    }

    [HttpPost("oku")]
    [HttpPost("/api/ocr")]
    [IgnoreAntiforgeryToken]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> Oku([FromForm] IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Fiş, irsaliye veya fatura görseli seçin." });

        FiscalDocumentDto document;
        try
        {
            await using var stream = file.OpenReadStream();
            document = await _ocr.ReadAsync(stream, file.FileName, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { message = exception.Message });
        }

        if (string.IsNullOrWhiteSpace(document.RawText))
        {
            document.Warning = "Görselde okunabilir metin bulunamadı.";
            DocumentValidation.Validate(document);
            return Ok(FisReadResult.FromDocument(document));
        }

        try
        {
            var receipt = await _ollama.StructureAsync(document.RawText, cancellationToken);
            var warning = ReceiptGuard.AlignWithOcr(document, receipt);
            var result = FisReadResult.FromReceipt(document, receipt, warning);
            var dto = ToDto(result);
            DocumentValidation.ReconcileTotals(dto);
            // OCR toplamı varsa nihai otorite
            if (document.Total > 0)
                dto.Total = document.Total;
            if (document.TaxTotal > 0)
                dto.TaxTotal = document.TaxTotal;
            if (document.SubTotal > 0 && dto.SubTotal <= 0)
                dto.SubTotal = document.SubTotal;

            var messages = DocumentValidation.Validate(dto);
            result = FisReadResult.FromDocument(dto);
            result.Warning = warning;
            result.ValidationMessages = messages;
            if (messages.Count > 0)
            {
                var note = "Doğrulama: " + string.Join(" ", messages.Take(2));
                result.Warning = string.IsNullOrWhiteSpace(warning) ? note : warning + " " + note;
            }

            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(exception, "Ollama yapılandırması başarısız. OCR ayrıştırması kullanılacak.");
            DocumentValidation.ReconcileTotals(document);
            DocumentValidation.Validate(document);
            document.Warning = exception.Message + " Tablo OCR metninden dolduruldu.";
            return Ok(FisReadResult.FromDocument(document));
        }
    }

    private static FiscalDocumentDto ToDto(FisReadResult result) => new()
    {
        DocumentType = result.DocumentType,
        DocumentNo = result.DocumentNo,
        Company = result.Company,
        TaxNumber = result.TaxNumber,
        Date = result.Date,
        SubTotal = result.SubTotal,
        TaxTotal = result.TaxTotal,
        Total = result.Total,
        Confidence = result.Confidence,
        Warning = result.Warning,
        RawText = result.RawText,
        SourceFileName = result.SourceFileName,
        Lines = result.Lines
    };
}
