using FiscOCRApp.Models;

namespace FiscOCRApp.Services;

public static class DocumentValidation
{
    public static List<string> Validate(FiscalDocumentDto document)
    {
        var messages = new List<string>();
        var lines = document.Lines ?? [];

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line.Description) && line.Amount == 0 && line.UnitPrice == 0)
                continue;

            if (line.Quantity > 0 && line.UnitPrice > 0)
            {
                var expected = decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(expected - line.Amount) > 0.05m)
                    messages.Add($"Satır {i + 1}: adet × birim fiyat ({expected:0.##}) satır tutarıyla ({line.Amount:0.##}) uyuşmuyor.");
            }
        }

        var lineSum = lines.Sum(line => line.Amount);
        if (document.SubTotal <= 0 && lineSum > 0)
            document.SubTotal = decimal.Round(lineSum, 2, MidpointRounding.AwayFromZero);

        if (document.SubTotal > 0 && lineSum > 0 && Math.Abs(document.SubTotal - lineSum) > 0.5m)
            messages.Add($"Ara toplam ({document.SubTotal:0.##}) kalem toplamından ({lineSum:0.##}) sapıyor.");

        if (document.TaxTotal < 0)
            document.TaxTotal = 0;

        var expectedTotal = document.SubTotal + document.TaxTotal;
        if (document.Total > 0 && document.SubTotal > 0 && document.TaxTotal > 0
            && Math.Abs(document.Total - expectedTotal) > 0.5m)
            messages.Add($"Genel toplam ({document.Total:0.##}) ara toplam + KDV ({expectedTotal:0.##}) ile uyuşmuyor.");

        if (document.Total > 0 && lineSum > 0 && document.TaxTotal == 0
            && Math.Abs(document.Total - lineSum) > 1m)
            messages.Add($"Genel toplam ({document.Total:0.##}) kalem toplamından ({lineSum:0.##}) sapıyor. KDV veya satırları kontrol edin.");

        if (string.IsNullOrWhiteSpace(document.Company))
            messages.Add("Firma adı boş.");

        if (string.IsNullOrWhiteSpace(document.Date))
            messages.Add("Tarih boş.");

        document.ValidationMessages = messages;
        return messages;
    }

    public static void ReconcileTotals(FiscalDocumentDto document)
    {
        var lines = document.Lines ?? [];
        foreach (var line in lines)
        {
            if (line.Quantity <= 0)
                line.Quantity = 1;
            if (line.Amount == 0 && line.UnitPrice > 0)
                line.Amount = decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
            else if (line.UnitPrice == 0 && line.Amount > 0)
                line.UnitPrice = decimal.Round(line.Amount / line.Quantity, 2, MidpointRounding.AwayFromZero);
            else if (line.Quantity > 0 && line.UnitPrice > 0)
            {
                var expected = decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(expected - line.Amount) <= 0.05m)
                    line.Amount = expected;
            }
        }

        var lineSum = decimal.Round(lines.Sum(line => line.Amount), 2, MidpointRounding.AwayFromZero);
        if (document.SubTotal <= 0)
            document.SubTotal = lineSum;

        if (document.TaxTotal < 0)
            document.TaxTotal = 0;

        if (document.Total <= 0)
        {
            if (document.SubTotal > 0)
                document.Total = document.SubTotal + document.TaxTotal;
            else if (lineSum > 0)
                document.Total = lineSum + document.TaxTotal;
        }
        else if (document.TaxTotal == 0 && document.SubTotal > 0 && document.Total > document.SubTotal)
        {
            document.TaxTotal = decimal.Round(document.Total - document.SubTotal, 2, MidpointRounding.AwayFromZero);
        }
        else if (document.SubTotal == 0 && document.TaxTotal > 0 && document.Total > document.TaxTotal)
        {
            document.SubTotal = decimal.Round(document.Total - document.TaxTotal, 2, MidpointRounding.AwayFromZero);
        }
    }
}
