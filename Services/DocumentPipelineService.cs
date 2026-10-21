using System.Diagnostics;
using FiscOCRApp.Models;

namespace FiscOCRApp.Services;

public interface IDocumentPipelineService
{
    Task<PipelineProcessResult> ProcessAsync(
        Stream content,
        string fileName,
        IProgress<PipelineStage>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Hibrit boru hattı: OCR → Regex (hızlı sabit alanlar) → LLM (yapılandırma) → Guard/Match.
/// </summary>
public sealed class DocumentPipelineService : IDocumentPipelineService
{
    private readonly IOcrService _ocr;
    private readonly IOllamaService _ollama;
    private readonly IRegexFieldExtractor _regex;
    private readonly ILogger<DocumentPipelineService> _logger;

    public DocumentPipelineService(
        IOcrService ocr,
        IOllamaService ollama,
        IRegexFieldExtractor regex,
        ILogger<DocumentPipelineService> logger)
    {
        _ocr = ocr;
        _ollama = ollama;
        _regex = regex;
        _logger = logger;
    }

    public async Task<PipelineProcessResult> ProcessAsync(
        Stream content,
        string fileName,
        IProgress<PipelineStage>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(PipelineStage.OcrScanning);
        var ocrSw = Stopwatch.StartNew();
        var document = await _ocr.ReadAsync(content, fileName, cancellationToken);
        ocrSw.Stop();

        if (string.IsNullOrWhiteSpace(document.RawText))
        {
            document.Warning = "Görselde okunabilir metin bulunamadı.";
            DocumentValidation.Validate(document);
            progress?.Report(PipelineStage.Completed);
            return new PipelineProcessResult
            {
                Document = FisReadResult.FromDocument(document),
                PipelinePath = "ocr-empty",
                OcrMs = ocrSw.ElapsedMilliseconds
            };
        }

        progress?.Report(PipelineStage.RegexParsing);
        var regexSw = Stopwatch.StartNew();
        var regexSnapshot = _regex.Extract(document.RawText);
        _regex.ApplyToDocument(document, regexSnapshot);
        regexSw.Stop();

        progress?.Report(PipelineStage.AiStructuring);
        var llmSw = Stopwatch.StartNew();
        FisReadResult result;
        string pipelinePath;
        try
        {
            var receipt = await _ollama.StructureAsync(document.RawText, cancellationToken);
            // Regex sabit alanları LLM boş bıraktıysa doldur
            MergeRegexIntoReceipt(regexSnapshot, receipt);
            var warning = ReceiptGuard.AlignWithOcr(document, receipt);
            result = FisReadResult.FromReceipt(document, receipt, warning);
            pipelinePath = "ocr+regex+llm";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(exception, "LLM yapılandırması başarısız; OCR+Regex fallback.");
            DocumentValidation.ReconcileTotals(document);
            document.Warning = exception.Message + " Tablo OCR/Regex katmanından dolduruldu.";
            result = FisReadResult.FromDocument(document);
            pipelinePath = "ocr+regex-fallback";
        }
        finally
        {
            llmSw.Stop();
        }

        progress?.Report(PipelineStage.Matching);
        var dto = ToDto(result);
        DocumentValidation.ReconcileTotals(dto);
        if (document.Total > 0) dto.Total = document.Total;
        if (document.TaxTotal > 0) dto.TaxTotal = document.TaxTotal;
        if (document.SubTotal > 0 && dto.SubTotal <= 0) dto.SubTotal = document.SubTotal;
        if (regexSnapshot.Total is > 0 && dto.Total <= 0) dto.Total = regexSnapshot.Total.Value;
        if (regexSnapshot.TaxTotal is > 0 && dto.TaxTotal <= 0) dto.TaxTotal = regexSnapshot.TaxTotal.Value;

        var messages = DocumentValidation.Validate(dto);
        result = FisReadResult.FromDocument(dto);
        result.ValidationMessages = messages;
        if (messages.Count > 0)
        {
            var note = "Doğrulama: " + string.Join(" ", messages.Take(2));
            result.Warning = string.IsNullOrWhiteSpace(result.Warning) ? note : result.Warning + " " + note;
        }

        progress?.Report(PipelineStage.Completed);
        _logger.LogInformation(
            "Pipeline tamam. Path={Path} OCR={OcrMs}ms Regex={RegexMs}ms LLM={LlmMs}ms Firma={Company} Kalem={Count}",
            pipelinePath,
            ocrSw.ElapsedMilliseconds,
            regexSw.ElapsedMilliseconds,
            llmSw.ElapsedMilliseconds,
            result.Company,
            result.Lines.Count);

        return new PipelineProcessResult
        {
            Document = result,
            Regex = regexSnapshot,
            PipelinePath = pipelinePath,
            OcrMs = ocrSw.ElapsedMilliseconds,
            RegexMs = regexSw.ElapsedMilliseconds,
            LlmMs = llmSw.ElapsedMilliseconds
        };
    }

    private static void MergeRegexIntoReceipt(RegexExtractionSnapshot regex, OllamaReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt.Tarih) && !string.IsNullOrWhiteSpace(regex.Date))
            receipt.Tarih = regex.Date!;
        if (string.IsNullOrWhiteSpace(receipt.VergiNo) && !string.IsNullOrWhiteSpace(regex.TaxNumber))
            receipt.VergiNo = regex.TaxNumber;
        if (string.IsNullOrWhiteSpace(receipt.FisNo) && !string.IsNullOrWhiteSpace(regex.DocumentNo))
            receipt.FisNo = regex.DocumentNo;
        if (string.IsNullOrWhiteSpace(receipt.BelgeTuru) && !string.IsNullOrWhiteSpace(regex.DocumentType))
            receipt.BelgeTuru = regex.DocumentType;
        if (string.IsNullOrWhiteSpace(receipt.FirmaAdi) && !string.IsNullOrWhiteSpace(regex.CompanyHint))
            receipt.FirmaAdi = regex.CompanyHint!;
        if (receipt.GenelToplam <= 0 && regex.Total is > 0)
            receipt.GenelToplam = regex.Total.Value;
        if (receipt.KdvToplam <= 0 && regex.TaxTotal is > 0)
            receipt.KdvToplam = regex.TaxTotal.Value;
        if (receipt.AraToplam <= 0 && regex.SubTotal is > 0)
            receipt.AraToplam = regex.SubTotal.Value;
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
