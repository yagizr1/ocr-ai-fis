namespace FiscOCRApp.Models;

public sealed class FiscalDocumentDto
{
    public long? Id { get; set; }
    public string DocumentType { get; set; } = "Belge";
    public string? DocumentNo { get; set; }
    public string Company { get; set; } = "";
    public string? TaxNumber { get; set; }
    public string? Date { get; set; }
    public decimal SubTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "TRY";
    public double Confidence { get; set; }
    public string? Warning { get; set; }
    public string? RawText { get; set; }
    public string? SourceFileName { get; set; }
    public string? CreatedAt { get; set; }
    public List<string> ValidationMessages { get; set; } = [];
    public List<FiscalLineDto> Lines { get; set; } = [];
}

public sealed class FiscalLineDto
{
    public int LineNo { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public string Unit { get; set; } = "AD";
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
}
