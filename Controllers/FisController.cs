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
            var warning = MergeBlanks(document, receipt);
            var result = FisReadResult.FromReceipt(document, receipt, warning);
            var dto = ToDto(result);
            DocumentValidation.ReconcileTotals(dto);
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
        catch (InvalidOperationException exception)
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

    private static string? MergeBlanks(FiscalDocumentDto ocr, OllamaReceipt receipt)
    {
        var filled = false;
        if (string.IsNullOrWhiteSpace(receipt.BelgeTuru) && !string.IsNullOrWhiteSpace(ocr.DocumentType))
        {
            receipt.BelgeTuru = ocr.DocumentType;
            filled = true;
        }

        if (string.IsNullOrWhiteSpace(receipt.FirmaAdi) && !string.IsNullOrWhiteSpace(ocr.Company))
        {
            receipt.FirmaAdi = ocr.Company;
            filled = true;
        }

        if (string.IsNullOrWhiteSpace(receipt.VergiNo) && !string.IsNullOrWhiteSpace(ocr.TaxNumber))
        {
            receipt.VergiNo = ocr.TaxNumber;
            filled = true;
        }

        if (string.IsNullOrWhiteSpace(receipt.Tarih) && !string.IsNullOrWhiteSpace(ocr.Date))
        {
            receipt.Tarih = ocr.Date;
            filled = true;
        }

        if (string.IsNullOrWhiteSpace(receipt.FisNo) && !string.IsNullOrWhiteSpace(ocr.DocumentNo))
        {
            receipt.FisNo = ocr.DocumentNo;
            filled = true;
        }

        if (receipt.Kalemler.Count == 0 && ocr.Lines.Count > 0)
        {
            receipt.Kalemler = ocr.Lines.Select(line => new OllamaLine
            {
                UrunAdi = line.Description,
                Adet = line.Quantity,
                Birim = line.Unit,
                BirimFiyat = line.UnitPrice,
                Toplam = line.Amount
            }).ToList();
            filled = true;
        }

        if (receipt.KdvToplam <= 0 && ocr.TaxTotal > 0)
        {
            receipt.KdvToplam = ocr.TaxTotal;
            filled = true;
        }

        if (receipt.AraToplam <= 0 && ocr.SubTotal > 0)
        {
            receipt.AraToplam = ocr.SubTotal;
            filled = true;
        }

        if (receipt.GenelToplam <= 0 && ocr.Total > 0)
        {
            receipt.GenelToplam = ocr.Total;
            filled = true;
        }

        return filled
            ? "Bazı alanlar model yanıtında yoktu ve OCR metninden tamamlandı."
            : null;
    }
}
