using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FiscOCRApp.Models;

public sealed class OllamaReceipt
{
    [JsonPropertyName("belge_turu")]
    public string? BelgeTuru { get; set; }

    [JsonPropertyName("firma_adi")]
    public string FirmaAdi { get; set; } = "";

    [JsonPropertyName("vergi_no")]
    public string? VergiNo { get; set; }

    [JsonPropertyName("tarih")]
    public string Tarih { get; set; } = "";

    [JsonPropertyName("fis_no")]
    public string? FisNo { get; set; }

    [JsonPropertyName("kalemler")]
    public List<OllamaLine> Kalemler { get; set; } = [];

    [JsonPropertyName("ara_toplam")]
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public decimal AraToplam { get; set; }

    [JsonPropertyName("kdv_toplam")]
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public decimal KdvToplam { get; set; }

    [JsonPropertyName("genel_toplam")]
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public decimal GenelToplam { get; set; }
}

public sealed class OllamaLine
{
    [JsonPropertyName("urun_adi")]
    public string UrunAdi { get; set; } = "";

    [JsonPropertyName("adet")]
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public decimal Adet { get; set; } = 1;

    [JsonPropertyName("birim")]
    public string? Birim { get; set; }

    [JsonPropertyName("birim_fiyat")]
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public decimal BirimFiyat { get; set; }

    [JsonPropertyName("toplam")]
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public decimal Toplam { get; set; }
}

public sealed class FisReadResult
{
    [JsonPropertyName("belge_turu")]
    public string? BelgeTuru { get; set; }

    [JsonPropertyName("firma_adi")]
    public string FirmaAdi { get; set; } = "";

    [JsonPropertyName("vergi_no")]
    public string? VergiNo { get; set; }

    [JsonPropertyName("tarih")]
    public string Tarih { get; set; } = "";

    [JsonPropertyName("fis_no")]
    public string? FisNo { get; set; }

    [JsonPropertyName("kalemler")]
    public List<OllamaLine> Kalemler { get; set; } = [];

    [JsonPropertyName("ara_toplam")]
    public decimal AraToplam { get; set; }

    [JsonPropertyName("kdv_toplam")]
    public decimal KdvToplam { get; set; }

    [JsonPropertyName("genel_toplam")]
    public decimal GenelToplam { get; set; }

    public string Company { get; set; } = "";
    public string? Date { get; set; }
    public string? DocumentNo { get; set; }
    public string? TaxNumber { get; set; }
    public decimal SubTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string DocumentType { get; set; } = "Fiş";
    public string Currency { get; set; } = "TRY";
    public double Confidence { get; set; }
    public string? Warning { get; set; }
    public string? RawText { get; set; }
    public string? SourceFileName { get; set; }
    public List<string> ValidationMessages { get; set; } = [];
    public List<FiscalLineDto> Lines { get; set; } = [];

    public static FisReadResult FromReceipt(FiscalDocumentDto ocr, OllamaReceipt receipt, string? warning)
    {
        var lines = receipt.Kalemler.Select((line, index) => new FiscalLineDto
        {
            LineNo = index + 1,
            Description = line.UrunAdi ?? "",
            Quantity = line.Adet <= 0 ? 1 : line.Adet,
            Unit = NormalizeUnit(line.Birim),
            UnitPrice = line.BirimFiyat < 0 ? 0 : line.BirimFiyat,
            Amount = line.Toplam < 0 ? 0 : line.Toplam
        }).ToList();

        var documentType = NormalizeType(receipt.BelgeTuru) ?? NormalizeType(ocr.DocumentType) ?? "Fiş";
        var taxNumber = string.IsNullOrWhiteSpace(receipt.VergiNo) ? ocr.TaxNumber : receipt.VergiNo.Trim();

        return new FisReadResult
        {
            BelgeTuru = documentType,
            FirmaAdi = receipt.FirmaAdi,
            VergiNo = taxNumber,
            Tarih = receipt.Tarih,
            FisNo = receipt.FisNo,
            Kalemler = receipt.Kalemler,
            AraToplam = receipt.AraToplam,
            KdvToplam = receipt.KdvToplam,
            GenelToplam = receipt.GenelToplam,
            Company = receipt.FirmaAdi,
            Date = string.IsNullOrWhiteSpace(receipt.Tarih) ? null : receipt.Tarih,
            DocumentNo = string.IsNullOrWhiteSpace(receipt.FisNo) ? null : receipt.FisNo,
            TaxNumber = taxNumber,
            SubTotal = receipt.AraToplam,
            TaxTotal = receipt.KdvToplam,
            Total = receipt.GenelToplam,
            DocumentType = documentType,
            Confidence = ocr.Confidence,
            Warning = warning,
            RawText = ocr.RawText,
            SourceFileName = ocr.SourceFileName,
            Lines = lines
        };
    }

    public static FisReadResult FromDocument(FiscalDocumentDto document)
    {
        var lines = document.Lines ?? [];
        return new FisReadResult
        {
            BelgeTuru = document.DocumentType,
            FirmaAdi = document.Company,
            VergiNo = document.TaxNumber,
            Tarih = document.Date ?? "",
            FisNo = document.DocumentNo,
            GenelToplam = document.Total,
            AraToplam = document.SubTotal,
            KdvToplam = document.TaxTotal,
            Kalemler = lines.Select(line => new OllamaLine
            {
                UrunAdi = line.Description,
                Adet = line.Quantity,
                Birim = line.Unit,
                BirimFiyat = line.UnitPrice,
                Toplam = line.Amount
            }).ToList(),
            Company = document.Company,
            Date = document.Date,
            DocumentNo = document.DocumentNo,
            TaxNumber = document.TaxNumber,
            SubTotal = document.SubTotal,
            TaxTotal = document.TaxTotal,
            Total = document.Total,
            DocumentType = document.DocumentType,
            Confidence = document.Confidence,
            Warning = document.Warning,
            RawText = document.RawText,
            SourceFileName = document.SourceFileName,
            ValidationMessages = document.ValidationMessages ?? [],
            Lines = lines
        };
    }

    public static string NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return "AD";
        var value = unit.Trim().ToUpperInvariant();
        return value switch
        {
            "ADET" or "ADT" => "AD",
            "PAKET" or "PKT" => "PK",
            "KOLI" or "KOLİ" => "KL",
            "L" => "LT",
            "G" => "GR",
            _ => value.Length <= 12 ? value : value[..12]
        };
    }

    public static string? NormalizeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var folded = value.Trim().ToLowerInvariant()
            .Replace("ı", "i").Replace("İ", "i").Replace("ş", "s").Replace("ğ", "g")
            .Replace("ü", "u").Replace("ö", "o").Replace("ç", "c");
        if (folded.Contains("irsaliye") || folded.Contains("sevk"))
            return "İrsaliye";
        if (folded.Contains("fatura") || folded.Contains("e-fatura") || folded.Contains("earsiv") || folded.Contains("e-arsiv"))
            return "Fatura";
        if (folded.Contains("fis") || folded.Contains("fiş") || folded.Contains("pos") || folded.Contains("yazar kasa"))
            return "Fiş";
        if (folded.Contains("belge"))
            return "Belge";
        return value.Trim() switch
        {
            "Fiş" or "İrsaliye" or "Fatura" or "Belge" => value.Trim(),
            _ => null
        };
    }
}

public sealed class FlexibleDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDecimal(out var number))
            return number;
        if (reader.TokenType == JsonTokenType.Null)
            return 0;
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Sayı alanı okunamadı.");

        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        text = text.Trim().Replace("₺", "", StringComparison.Ordinal).Replace("TL", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (text.Contains(',') && text.Contains('.'))
        {
            text = text.LastIndexOf(',') > text.LastIndexOf('.')
                ? text.Replace(".", "", StringComparison.Ordinal).Replace(',', '.')
                : text.Replace(",", "", StringComparison.Ordinal);
        }
        else if (text.Contains(','))
        {
            text = text.Replace(',', '.');
        }

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
