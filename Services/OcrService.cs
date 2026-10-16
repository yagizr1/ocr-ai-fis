using System.Text;
using FiscOCRApp.Models;
using PaddleOCRSharp;
using UglyToad.PdfPig;

namespace FiscOCRApp.Services;

public interface IOcrService
{
    Task<FiscalDocumentDto> ReadAsync(Stream image, string fileName, CancellationToken cancellationToken);
}

public sealed class OcrService : IOcrService, IDisposable
{
    private const int MaxBytes = 12 * 1024 * 1024;
    private readonly ILogger<OcrService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PaddleOCREngine? _engine;

    public OcrService(ILogger<OcrService> logger) => _logger = logger;

    public async Task<FiscalDocumentDto> ReadAsync(Stream image, string fileName, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0)
            throw new InvalidOperationException("Yüklenen dosya boş.");
        if (buffer.Length > MaxBytes)
            throw new InvalidOperationException("Dosya 12 MB sınırını aşıyor.");

        var bytes = buffer.ToArray();
        if (IsPdf(bytes))
            return await ReadPdfAsync(bytes, fileName, cancellationToken);

        if (!IsSupportedImage(bytes))
            throw new InvalidOperationException("Dosya desteklenen bir görsel veya PDF değil. JPG, PNG, BMP, WEBP, TIFF, GIF veya PDF yükleyin.");

        return await ReadImageAsync(bytes, fileName, cancellationToken);
    }

    private async Task<FiscalDocumentDto> ReadPdfAsync(byte[] bytes, string fileName, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await Task.Run(() => ExtractPdfText(bytes), cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "PDF metin çıkarma başarısız. Dosya={File}", fileName);
            throw new InvalidOperationException("PDF okunamadı: " + exception.Message, exception);
        }

        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 8)
            throw new InvalidOperationException(
                "PDF içinde seçilebilir metin bulunamadı. Taranmış PDF için sayfaları JPG/PNG olarak dışa aktarıp yükleyin.");

        var document = FiscalDocumentParser.ParseText(text);
        document.SourceFileName = Path.GetFileName(fileName);
        _logger.LogInformation(
            "PDF metin okundu. Dosya={File} Firma={Company} Kalem={Count} Tutar={Total}",
            document.SourceFileName,
            document.Company,
            document.Lines.Count,
            document.Total);
        return document;
    }

    private static string ExtractPdfText(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes);
        var builder = new StringBuilder();
        var pageCount = Math.Min(document.NumberOfPages, 8);
        for (var page = 1; page <= pageCount; page++)
        {
            var content = document.GetPage(page).Text;
            if (string.IsNullOrWhiteSpace(content))
                continue;
            if (builder.Length > 0)
                builder.AppendLine();
            builder.AppendLine(content.Trim());
            if (builder.Length > 12000)
                break;
        }

        return builder.ToString();
    }

    private async Task<FiscalDocumentDto> ReadImageAsync(byte[] bytes, string fileName, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var engine = GetEngine();
            var result = await Task.Run(() => engine.DetectText(bytes), cancellationToken);
            var document = FiscalDocumentParser.Parse(result);
            document.SourceFileName = Path.GetFileName(fileName);
            _logger.LogInformation(
                "OCR tamamlandı. Dosya={File} Firma={Company} Kalem={Count} Tutar={Total}",
                document.SourceFileName,
                document.Company,
                document.Lines.Count,
                document.Total);
            return document;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "OCR başarısız. Dosya={File}", fileName);
            throw new InvalidOperationException("Görsel okunamadı: " + exception.Message, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private PaddleOCREngine GetEngine()
    {
        if (_engine is not null)
            return _engine;

        try
        {
            var parameter = new OCRParameter
            {
                cpu_math_library_num_threads = Math.Clamp(Environment.ProcessorCount, 2, 8),
                enable_mkldnn = true,
                max_side_len = 1600
            };
            var engine = new PaddleOCREngine(null, parameter);
            engine.EnableDetUseRect(true);
            _engine = engine;
            return engine;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("OCR motoru başlatılamadı: " + exception.Message, exception);
        }
    }

    private static bool IsPdf(ReadOnlySpan<byte> data) =>
        data.Length >= 5 && data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46;

    private static bool IsSupportedImage(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
            return false;
        if (data[0] == 0xFF && data[1] == 0xD8)
            return true;
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return true;
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
            return true;
        if (data[0] == 0x42 && data[1] == 0x4D)
            return true;
        if ((data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D))
            return true;
        return data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50;
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _gate.Dispose();
    }
}
