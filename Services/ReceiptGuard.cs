using FiscOCRApp.Models;

namespace FiscOCRApp.Services;

/// <summary>
/// AI çıktısını OCR ile doğrular; uydurma kalem/toplamları reddeder.
/// </summary>
public static class ReceiptGuard
{
    public static string? AlignWithOcr(FiscalDocumentDto ocr, OllamaReceipt receipt)
    {
        var notes = new List<string>();

        FillHeaderFromOcr(ocr, receipt, notes);

        var ocrLines = ocr.Lines ?? [];
        var aiLines = receipt.Kalemler ?? [];
        var aiSum = decimal.Round(aiLines.Sum(line => line.Toplam), 2, MidpointRounding.AwayFromZero);
        var ocrSum = decimal.Round(ocrLines.Sum(line => line.Amount), 2, MidpointRounding.AwayFromZero);
        var ocrTotal = ocr.Total > 0 ? ocr.Total : ocrSum;
        var aiTotal = receipt.GenelToplam > 0 ? receipt.GenelToplam : aiSum;

        var rejectLines = ShouldRejectAiLines(ocrLines.Count, aiLines.Count, ocrTotal, aiTotal, ocrSum, aiSum);
        if (rejectLines)
        {
            receipt.Kalemler = ocrLines.Select(line => new OllamaLine
            {
                UrunAdi = line.Description,
                Adet = line.Quantity,
                Birim = line.Unit,
                BirimFiyat = line.UnitPrice,
                Toplam = line.Amount
            }).ToList();
            notes.Add("Yapay zeka kalemleri OCR ile uyuşmadığı için OCR satırları kullanıldı.");
        }
        else if (aiLines.Count == 0 && ocrLines.Count > 0)
        {
            receipt.Kalemler = ocrLines.Select(line => new OllamaLine
            {
                UrunAdi = line.Description,
                Adet = line.Quantity,
                Birim = line.Unit,
                BirimFiyat = line.UnitPrice,
                Toplam = line.Amount
            }).ToList();
            notes.Add("Kalemler OCR metninden alındı.");
        }

        // Toplamlar: OCR otorite
        if (ocrTotal > 0)
        {
            if (receipt.GenelToplam <= 0 || RelativeDiff(receipt.GenelToplam, ocrTotal) > 0.08m || Math.Abs(receipt.GenelToplam - ocrTotal) > 1.5m)
            {
                receipt.GenelToplam = ocrTotal;
                notes.Add("Genel toplam OCR'daki TOPLAM ile eşitlendi.");
            }
        }

        if (ocr.TaxTotal > 0 && (receipt.KdvToplam <= 0 || Math.Abs(receipt.KdvToplam - ocr.TaxTotal) > 0.5m))
        {
            receipt.KdvToplam = ocr.TaxTotal;
            notes.Add("KDV OCR'dan alındı.");
        }

        if (ocr.SubTotal > 0 && receipt.AraToplam <= 0)
            receipt.AraToplam = ocr.SubTotal;

        var lineSum = decimal.Round(receipt.Kalemler.Sum(line => line.Toplam), 2, MidpointRounding.AwayFromZero);
        if (receipt.AraToplam <= 0)
            receipt.AraToplam = lineSum;

        if (receipt.GenelToplam > 0 && receipt.KdvToplam <= 0 && receipt.AraToplam > 0 && receipt.GenelToplam >= receipt.AraToplam)
            receipt.KdvToplam = decimal.Round(receipt.GenelToplam - receipt.AraToplam, 2, MidpointRounding.AwayFromZero);

        if (receipt.GenelToplam > 0 && receipt.AraToplam <= 0 && receipt.KdvToplam >= 0)
            receipt.AraToplam = decimal.Round(receipt.GenelToplam - receipt.KdvToplam, 2, MidpointRounding.AwayFromZero);

        // Satır içi tutar düzeltmesi
        foreach (var line in receipt.Kalemler)
        {
            if (line.Adet <= 0)
                line.Adet = 1;
            if (line.BirimFiyat > 0)
            {
                var expected = decimal.Round(line.Adet * line.BirimFiyat, 2, MidpointRounding.AwayFromZero);
                if (line.Toplam <= 0 || Math.Abs(expected - line.Toplam) <= 0.15m)
                    line.Toplam = expected;
                else if (line.Toplam > 0 && line.Adet == 1)
                    line.BirimFiyat = line.Toplam; // market fişi: satır tutarı fiyat
                else if (line.Toplam > 0)
                    line.BirimFiyat = decimal.Round(line.Toplam / line.Adet, 2, MidpointRounding.AwayFromZero);
            }
            else if (line.Toplam > 0)
            {
                line.BirimFiyat = decimal.Round(line.Toplam / line.Adet, 2, MidpointRounding.AwayFromZero);
            }
        }

        return notes.Count == 0 ? null : string.Join(" ", notes.Distinct());
    }

    private static bool ShouldRejectAiLines(
        int ocrCount,
        int aiCount,
        decimal ocrTotal,
        decimal aiTotal,
        decimal ocrSum,
        decimal aiSum)
    {
        if (aiCount == 0)
            return false;

        // OCR satırı varken AI aşırı şişirmişse
        if (ocrCount > 0 && aiCount > Math.Max(ocrCount + 5, (int)Math.Ceiling(ocrCount * 1.6)))
            return true;

        // Toplam OCR'dan ciddi sapıyorsa
        if (ocrTotal > 0 && aiTotal > 0)
        {
            if (RelativeDiff(aiTotal, ocrTotal) > 0.12m || Math.Abs(aiTotal - ocrTotal) > 2m)
                return true;
        }

        // Kalem toplamı OCR kalem toplamından ciddi sapıyorsa
        if (ocrSum > 0 && aiSum > 0)
        {
            if (RelativeDiff(aiSum, ocrSum) > 0.2m || Math.Abs(aiSum - ocrSum) > 3m)
                return true;
        }

        // OCR toplamı varken AI kalem toplamı OCR toplamına hiç yaklaşmıyorsa
        if (ocrTotal > 0 && aiSum > 0 && RelativeDiff(aiSum, ocrTotal) > 0.25m && Math.Abs(aiSum - ocrTotal) > 3m)
            return true;

        return false;
    }

    private static void FillHeaderFromOcr(FiscalDocumentDto ocr, OllamaReceipt receipt, List<string> notes)
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

        if (filled)
            notes.Add("Bazı başlık alanları OCR metninden tamamlandı.");
    }

    private static decimal RelativeDiff(decimal a, decimal b)
    {
        var baseValue = Math.Max(Math.Abs(a), Math.Abs(b));
        if (baseValue <= 0)
            return 0;
        return Math.Abs(a - b) / baseValue;
    }
}
