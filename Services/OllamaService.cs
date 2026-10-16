using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FiscOCRApp.Models;
using Microsoft.Extensions.Options;

namespace FiscOCRApp.Services;

public interface IOllamaService
{
    Task<OllamaReceipt> StructureAsync(string rawText, CancellationToken cancellationToken);
}

public sealed class OllamaService : IOllamaService
{
    private const string GeneratePath = "api/generate";
    private readonly HttpClient _http;
    private readonly ILogger<OllamaService> _logger;
    private readonly string _model;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public OllamaService(HttpClient http, IOptions<OllamaOptions> options, ILogger<OllamaService> logger)
    {
        _http = http;
        _logger = logger;
        _model = string.IsNullOrWhiteSpace(options.Value.Model) ? "llama3.2" : options.Value.Model.Trim();
    }

    public async Task<OllamaReceipt> StructureAsync(string rawText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            throw new InvalidOperationException("Yapılandırılacak OCR metni boş.");

        // Kısa metin + düşük num_predict = CPU'da belirgin hız kazanımı
        var clipped = rawText.Length > 8000 ? rawText[..8000] : rawText;
        using var response = await SendAsync(clipped, useJsonFormat: true, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.BadRequest)
            return await ReadReceiptAsync(response, cancellationToken);

        using var retry = await SendAsync(clipped, useJsonFormat: false, cancellationToken);
        return await ReadReceiptAsync(retry, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(string rawText, bool useJsonFormat, CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(rawText);
        try
        {
            // 2048: çok kalemli fişlerde JSON'un yarıda kesilmesini azaltır
            var options = new { temperature = 0, num_predict = 2048, num_ctx = 4096 };
            var payload = useJsonFormat
                ? (object)new
                {
                    model = _model,
                    prompt,
                    stream = false,
                    format = "json",
                    keep_alive = "10m",
                    options
                }
                : new
                {
                    model = _model,
                    prompt,
                    stream = false,
                    keep_alive = "10m",
                    options
                };

            return await _http.PostAsJsonAsync(GeneratePath, payload, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                "Ollama servisine bağlanılamadı (http://localhost:11434). Ollama açık mı ve model indirildi mi?",
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Ollama yanıt vermeden zaman aşımına uğradı.", exception);
        }
    }

    private async Task<OllamaReceipt> ReadReceiptAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = body.Length > 280 ? body[..280] : body;
            throw new InvalidOperationException($"Ollama isteği başarısız ({(int)response.StatusCode}). {detail}");
        }

        var generated = JsonSerializer.Deserialize<OllamaGenerateResponse>(body, JsonOptions);
        if (string.IsNullOrWhiteSpace(generated?.Response))
            throw new InvalidOperationException("Ollama boş yanıt döndürdü.");

        var receipt = ParseReceipt(generated.Response);
        _logger.LogInformation(
            "Ollama yapılandırdı. Model={Model} Tür={Type} Firma={Company} Kalem={Count} KDV={Tax} Tutar={Total}",
            _model,
            receipt.BelgeTuru,
            receipt.FirmaAdi,
            receipt.Kalemler.Count,
            receipt.KdvToplam,
            receipt.GenelToplam);
        return receipt;
    }

    public static OllamaReceipt ParseReceipt(string modelText)
    {
        OllamaReceipt? receipt;
        try
        {
            receipt = DeserializeReceipt(ExtractJson(modelText));
        }
        catch (Exception first) when (first is JsonException or InvalidOperationException)
        {
            try
            {
                receipt = DeserializeReceipt(RepairTruncatedJson(ExtractJsonLoose(modelText)));
            }
            catch (Exception second) when (second is JsonException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "Model JSON yanıtı yarım veya geçersiz (kalem listesi kesilmiş olabilir).",
                    second);
            }
        }

        if (receipt is null)
            throw new InvalidOperationException("Model JSON'u çözümlenemedi.");

        receipt.BelgeTuru = FisReadResult.NormalizeType(receipt.BelgeTuru);
        receipt.FirmaAdi = Collapse(receipt.FirmaAdi);
        receipt.VergiNo = NormalizeTax(receipt.VergiNo);
        receipt.FisNo = string.IsNullOrWhiteSpace(receipt.FisNo) ? null : Collapse(receipt.FisNo);
        receipt.Tarih = NormalizeDate(receipt.Tarih);
        receipt.Kalemler ??= [];
        receipt.Kalemler = receipt.Kalemler
            .Where(line => !string.IsNullOrWhiteSpace(line.UrunAdi) || line.Toplam != 0 || line.BirimFiyat != 0)
            .Take(80)
            .ToList();

        foreach (var line in receipt.Kalemler)
        {
            line.UrunAdi = Collapse(line.UrunAdi);
            line.Birim = FisReadResult.NormalizeUnit(line.Birim);
            if (line.Adet <= 0)
                line.Adet = 1;
            if (line.BirimFiyat < 0)
                line.BirimFiyat = 0;
            if (line.Toplam < 0)
                line.Toplam = 0;

            if (line.Toplam == 0 && line.BirimFiyat > 0)
                line.Toplam = decimal.Round(line.Adet * line.BirimFiyat, 2, MidpointRounding.AwayFromZero);
            else if (line.BirimFiyat == 0 && line.Toplam > 0)
                line.BirimFiyat = decimal.Round(line.Toplam / line.Adet, 2, MidpointRounding.AwayFromZero);
            else if (line.BirimFiyat > 0)
            {
                var expected = decimal.Round(line.Adet * line.BirimFiyat, 2, MidpointRounding.AwayFromZero);
                if (line.Toplam == 0 || Math.Abs(expected - line.Toplam) <= 0.05m)
                    line.Toplam = expected;
                else if (Math.Abs(expected - line.Toplam) > 0.05m && line.Toplam > 0)
                {
                    // Prefer product when OCR-like rounding drift is small; keep model total if far apart
                    var ratio = expected == 0 ? 1 : Math.Abs(expected - line.Toplam) / expected;
                    if (ratio <= 0.02m)
                        line.Toplam = expected;
                }
            }
        }

        var lineSum = decimal.Round(receipt.Kalemler.Sum(line => line.Toplam), 2, MidpointRounding.AwayFromZero);
        if (receipt.AraToplam <= 0)
            receipt.AraToplam = lineSum;
        if (receipt.KdvToplam < 0)
            receipt.KdvToplam = 0;

        if (receipt.GenelToplam <= 0)
        {
            receipt.GenelToplam = receipt.AraToplam + receipt.KdvToplam;
            if (receipt.GenelToplam <= 0)
                receipt.GenelToplam = lineSum;
        }
        else if (receipt.KdvToplam == 0 && receipt.AraToplam > 0 && receipt.GenelToplam > receipt.AraToplam)
        {
            receipt.KdvToplam = decimal.Round(receipt.GenelToplam - receipt.AraToplam, 2, MidpointRounding.AwayFromZero);
        }
        else if (receipt.AraToplam == 0 && lineSum > 0)
        {
            receipt.AraToplam = lineSum;
            if (receipt.KdvToplam == 0 && receipt.GenelToplam > lineSum)
                receipt.KdvToplam = decimal.Round(receipt.GenelToplam - lineSum, 2, MidpointRounding.AwayFromZero);
        }

        receipt.AraToplam = decimal.Round(receipt.AraToplam, 2, MidpointRounding.AwayFromZero);
        receipt.KdvToplam = decimal.Round(receipt.KdvToplam, 2, MidpointRounding.AwayFromZero);
        receipt.GenelToplam = decimal.Round(receipt.GenelToplam, 2, MidpointRounding.AwayFromZero);
        return receipt;
    }

    private static string BuildPrompt(string rawText) =>
        """
        Aşağıdaki fiş/irsaliye/fatura OCR metnini yapılandır. Yalnızca geçerli JSON döndür.
        Şema:
        {"belge_turu":"Fiş|İrsaliye|Fatura|Belge","firma_adi":"string","vergi_no":"string","tarih":"YYYY-MM-DD","fis_no":"string","kalemler":[{"urun_adi":"string","adet":number,"birim":"AD","birim_fiyat":number,"toplam":number}],"ara_toplam":number,"kdv_toplam":number,"genel_toplam":number}
        Kurallar: satıcı unvanı; KDV/özet satırlarını kalem yapma; sayılarda nokta kullan; uydurma veri ekleme.

        OCR metni:
        """ + rawText;

    private static OllamaReceipt? DeserializeReceipt(string json) =>
        JsonSerializer.Deserialize<OllamaReceipt>(json, JsonOptions);

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("Model JSON döndürmedi.");
        return text[start..(end + 1)];
    }

    private static string ExtractJsonLoose(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
            throw new InvalidOperationException("Model JSON döndürmedi.");
        var end = text.LastIndexOf('}');
        return end > start ? text[start..(end + 1)] : text[start..];
    }

    /// <summary>
    /// Model num_predict dolunca yarım kalan JSON'u mümkünse kapatır.
    /// </summary>
    private static string RepairTruncatedJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Model JSON boş.");

        var text = json.Trim();
        // Son tamamlanmamış kalem nesnesini at: son tam '}' sonrasını kırp
        var lastBrace = text.LastIndexOf('}');
        if (lastBrace > 0)
            text = text[..(lastBrace + 1)];

        text = text.TrimEnd();
        while (text.EndsWith(','))
            text = text[..^1].TrimEnd();

        var openBrace = 0;
        var openBracket = 0;
        var inString = false;
        var escape = false;
        foreach (var ch in text)
        {
            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (ch == '\\') { escape = true; continue; }
                if (ch == '"') inString = false;
                continue;
            }

            switch (ch)
            {
                case '"': inString = true; break;
                case '{': openBrace++; break;
                case '}': openBrace--; break;
                case '[': openBracket++; break;
                case ']': openBracket--; break;
            }
        }

        if (inString)
            text += "\"";

        // Eksik kapanışları ekle (önce diziler, sonra nesneler)
        while (openBracket > 0) { text += "]"; openBracket--; }
        while (openBrace > 0) { text += "}"; openBrace--; }

        return text;
    }

    private static string NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var text = value.Trim();
        string[] formats = ["yyyy-MM-dd", "dd.MM.yyyy", "dd/MM/yyyy", "dd-MM-yyyy", "d.M.yyyy", "yyyy/MM/dd"];
        if (DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return "";
    }

    private static string? NormalizeTax(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length is 10 or 11 ? digits : null;
    }

    private static string Collapse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var text = string.Join(' ', parts).Trim();
        return text.Length <= 200 ? text : text[..200];
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }
    }
}
