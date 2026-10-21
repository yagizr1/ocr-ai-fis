using System.Text.Json.Serialization;

namespace FiscOCRApp.Models;

public enum PipelineStage
{
    Queued = 0,
    Uploading = 1,
    OcrScanning = 2,
    RegexParsing = 3,
    AiStructuring = 4,
    Matching = 5,
    Completed = 6,
    Failed = 7
}

public sealed class PipelineJobStatus
{
    public string JobId { get; set; } = "";
    public PipelineStage Stage { get; set; } = PipelineStage.Queued;
    public string StageLabel { get; set; } = "Kuyrukta";
    public int ProgressPercent { get; set; }
    public string? Message { get; set; }
    public bool IsTerminal => Stage is PipelineStage.Completed or PipelineStage.Failed;
    public FisReadResult? Result { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> StageLog { get; set; } = [];
}

public sealed class PipelineJobRequest
{
    public string FileName { get; set; } = "";
    public byte[] Content { get; set; } = [];
}

public sealed class PipelineProcessResult
{
    public FisReadResult Document { get; set; } = new();
    public RegexExtractionSnapshot Regex { get; set; } = new();
    public string PipelinePath { get; set; } = "ocr+regex+llm";
    public long OcrMs { get; set; }
    public long RegexMs { get; set; }
    public long LlmMs { get; set; }
}

public sealed class RegexExtractionSnapshot
{
    [JsonPropertyName("tarih")]
    public string? Date { get; set; }

    [JsonPropertyName("vergi_no")]
    public string? TaxNumber { get; set; }

    [JsonPropertyName("fis_no")]
    public string? DocumentNo { get; set; }

    [JsonPropertyName("belge_turu")]
    public string? DocumentType { get; set; }

    [JsonPropertyName("genel_toplam")]
    public decimal? Total { get; set; }

    [JsonPropertyName("kdv_toplam")]
    public decimal? TaxTotal { get; set; }

    [JsonPropertyName("ara_toplam")]
    public decimal? SubTotal { get; set; }

    [JsonPropertyName("firma_adi")]
    public string? CompanyHint { get; set; }
}

public static class PipelineStageLabels
{
    public static string Get(PipelineStage stage) => stage switch
    {
        PipelineStage.Queued => "Kuyrukta",
        PipelineStage.Uploading => "Yükleniyor",
        PipelineStage.OcrScanning => "OCR taraniyor",
        PipelineStage.RegexParsing => "Regex ayrıştırma",
        PipelineStage.AiStructuring => "AI yapılandırma",
        PipelineStage.Matching => "Doğrulama / eşleştirme",
        PipelineStage.Completed => "Tamamlandı",
        PipelineStage.Failed => "Hata",
        _ => stage.ToString()
    };

    public static int Percent(PipelineStage stage) => stage switch
    {
        PipelineStage.Queued => 5,
        PipelineStage.Uploading => 10,
        PipelineStage.OcrScanning => 35,
        PipelineStage.RegexParsing => 50,
        PipelineStage.AiStructuring => 75,
        PipelineStage.Matching => 90,
        PipelineStage.Completed => 100,
        PipelineStage.Failed => 100,
        _ => 0
    };
}
