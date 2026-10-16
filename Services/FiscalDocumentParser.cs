using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FiscOCRApp.Models;
using PaddleOCRSharp;

namespace FiscOCRApp.Services;

public static class FiscalDocumentParser
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private static readonly Regex CompanySuffix = new(
        @"\b(a\.\s*s\.|a\.s\.|anonim|ltd\.?\s*sti\.?|limited|ltd)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DocNoRegex = new(
        @"\b(?:fis|fi[sş]|irsaliye|fatura|belge|evrak|e-?fatura|e-?arsiv)\s*(?:no|numarasi|numarası)?\s*[:\-]?\s*([a-z0-9][a-z0-9\-/]{0,30})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TaxRegex = new(
        @"\b(?:vkn|tckn|vergi\s*no|vn)\s*[:\-]?\s*(\d{10,11})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Date4 = new(
        @"\b(\d{2})[./-](\d{2})[./-](\d{4})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Date2 = new(
        @"\b(\d{2})[./-](\d{2})[./-](\d{2})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IsoDate = new(
        @"\b(\d{4})[./-](\d{2})[./-](\d{2})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Units = new(StringComparer.Ordinal)
    {
        "AD", "ADET", "ADT", "KG", "GR", "G", "LT", "L", "MT", "M",
        "PK", "PKT", "PAKET", "KL", "KOLI", "KOLI.", "KT", "TN", "KUTU",
        "CUVAL", "M2", "M3"
    };

    public static FiscalDocumentDto Parse(OCRResult? result)
    {
        var tokens = ExtractTokens(result);
        var rows = ClusterRows(tokens);
        var lines = rows.Select(row => string.Join(" ", row.Select(token => token.Text))).ToList();
        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(result?.Text))
            lines = result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var confidence = tokens.Count == 0
            ? 0
            : Math.Round(tokens.Average(token => (double)token.Score), 2);
        var emptyWarning = tokens.Count == 0 && lines.Count == 0
            ? "Görselde okunabilir metin bulunamadı."
            : null;
        return BuildFromLines(lines, result?.Text, confidence, emptyWarning);
    }

    public static FiscalDocumentDto ParseText(string text)
    {
        var lines = (text ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToList();
        return BuildFromLines(lines, text, confidence: 0.85, emptyWarning: lines.Count == 0 ? "Metin bulunamadı." : null);
    }

    private static FiscalDocumentDto BuildFromLines(
        List<string> lines,
        string? fallbackRaw,
        double confidence,
        string? emptyWarning)
    {
        var foldedLines = lines.Select(Fold).ToList();
        var foldedAll = string.Join("\n", foldedLines);
        var company = FindCompany(lines, foldedLines);
        var date = FindDate(lines, foldedLines);
        var documentNo = FindDocumentNo(foldedAll);
        var taxNumber = FindTaxNumber(lines, foldedLines);
        var documentType = DetectType(foldedAll);
        var keywordTotal = FindKeywordTotal(lines, foldedLines);
        var taxTotal = FindKeywordTax(lines, foldedLines);
        var subTotal = FindKeywordSubTotal(lines, foldedLines);
        var items = FindLineItems(lines, foldedLines);

        var lineSum = items.Sum(item => item.Amount);
        var total = keywordTotal ?? 0m;
        if (total <= 0)
            total = lineSum + (taxTotal ?? 0m);
        if (subTotal is null or <= 0)
            subTotal = lineSum > 0 ? lineSum : (total > 0 && taxTotal is > 0 ? total - taxTotal.Value : total);
        if (taxTotal is null or < 0)
            taxTotal = total > subTotal ? total - subTotal.Value : 0m;
        if (items.Count == 0 && total > 0)
        {
            items.Add(new FiscalLineDto
            {
                LineNo = 1,
                Description = "Okunan belge tutarı",
                Quantity = 1,
                Unit = "AD",
                UnitPrice = subTotal ?? total,
                Amount = subTotal ?? total
            });
        }

        for (var i = 0; i < items.Count; i++)
            items[i].LineNo = i + 1;

        var raw = lines.Count > 0 ? string.Join("\n", lines) : fallbackRaw ?? "";
        if (raw.Length > 12000)
            raw = raw[..12000];

        string? warning = emptyWarning;
        if (warning is null && (string.IsNullOrWhiteSpace(company) || total <= 0))
            warning = "Bazı alanlar güvenle ayrılamadı. Firma, tarih, tutar ve kalemleri tabloda düzeltip kaydedebilirsiniz.";

        return new FiscalDocumentDto
        {
            DocumentType = documentType,
            DocumentNo = documentNo,
            Company = company,
            TaxNumber = taxNumber,
            Date = date,
            SubTotal = decimal.Round(subTotal ?? 0m, 2, MidpointRounding.AwayFromZero),
            TaxTotal = decimal.Round(taxTotal ?? 0m, 2, MidpointRounding.AwayFromZero),
            Total = decimal.Round(total, 2, MidpointRounding.AwayFromZero),
            Confidence = confidence,
            Warning = warning,
            RawText = raw,
            Lines = items
        };
    }

    private static List<OcrToken> ExtractTokens(OCRResult? result)
    {
        var tokens = new List<OcrToken>();
        if (result?.TextBlocks is null)
            return tokens;

        foreach (var block in result.TextBlocks)
        {
            if (string.IsNullOrWhiteSpace(block.Text) || block.Score < 0.35f)
                continue;

            float x = tokens.Count * 10f;
            float y = tokens.Count * 20f;
            float height = 16f;
            if (block.BoxPoints is { Count: > 0 })
            {
                var minX = (float)block.BoxPoints[0].X;
                var minY = (float)block.BoxPoints[0].Y;
                var maxY = (float)block.BoxPoints[0].Y;
                float sumY = 0;
                foreach (var point in block.BoxPoints)
                {
                    if (point.X < minX) minX = point.X;
                    if (point.Y < minY) minY = point.Y;
                    if (point.Y > maxY) maxY = point.Y;
                    sumY += point.Y;
                }

                x = minX;
                y = sumY / block.BoxPoints.Count;
                height = Math.Max(8f, maxY - minY);
            }

            tokens.Add(new OcrToken(block.Text.Trim(), block.Score, x, y, height));
        }

        return tokens;
    }

    private static List<List<OcrToken>> ClusterRows(List<OcrToken> tokens)
    {
        if (tokens.Count == 0)
            return [];

        var heights = tokens.Select(token => token.Height).Where(height => height > 0).OrderBy(height => height).ToList();
        var median = heights.Count == 0 ? 16f : heights[heights.Count / 2];
        var threshold = Math.Max(10f, median * 0.65f);
        var sorted = tokens.OrderBy(token => token.Y).ThenBy(token => token.X).ToList();
        var rows = new List<List<OcrToken>>();

        foreach (var token in sorted)
        {
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs(token.Y - row.Average(item => item.Y)) > threshold)
                rows.Add([token]);
            else
                row.Add(token);
        }

        foreach (var row in rows)
            row.Sort((left, right) => left.X.CompareTo(right.X));

        return rows;
    }

    private static string FindCompany(IReadOnlyList<string> lines, IReadOnlyList<string> foldedLines)
    {
        for (var i = 0; i < Math.Min(lines.Count, 10); i++)
        {
            if (LooksLikeCompany(foldedLines[i]) && !IsSkippableCompanyLine(foldedLines[i], lines[i]))
                return Clean(lines[i]);
        }

        string[] labels = ["unvan", "satici", "gonderen", "firma"];
        for (var i = 0; i < lines.Count; i++)
        {
            foreach (var label in labels)
            {
                var index = foldedLines[i].IndexOf(label, StringComparison.Ordinal);
                if (index < 0 || index > 8)
                    continue;

                var rest = Clean(lines[i][(index + label.Length)..].TrimStart(' ', ':', '-', '\t'));
                if (rest.Length >= 3 && !IsSkippableCompanyLine(Fold(rest), rest))
                    return rest;
                if (i + 1 < lines.Count && !IsSkippableCompanyLine(foldedLines[i + 1], lines[i + 1]))
                    return Clean(lines[i + 1]);
            }
        }

        for (var i = 0; i < Math.Min(lines.Count, 8); i++)
        {
            if (!IsSkippableCompanyLine(foldedLines[i], lines[i]))
                return Clean(lines[i]);
        }

        return "";
    }

    private static bool LooksLikeCompany(string folded) => CompanySuffix.IsMatch(folded)
        || ((folded.Contains("ticaret", StringComparison.Ordinal) || folded.Contains("sanayi", StringComparison.Ordinal))
            && !Regex.IsMatch(folded, @"\b(mah|mahalle|sok|sokak|cad|cadde|bulvar)\b")
            && folded.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2);

    private static bool IsSkippableCompanyLine(string folded, string original)
    {
        if (folded.Length < 3 || IsMetaRow(folded) || IsHeading(folded))
            return true;
        if (ParseDateInText(original) is not null && folded.Length < 28)
            return true;
        if (Regex.IsMatch(folded, @"\b(mah|mahalle|sok|sokak|cad|cadde|bulvar|apart|tel|telefon|www|http)\b"))
            return true;
        if (original.Contains('@', StringComparison.Ordinal))
            return true;
        if (Regex.IsMatch(original, @"^\d[\d\s./:-]{5,}$"))
            return true;
        return !Regex.IsMatch(original, @"[A-Za-zÇĞİÖŞÜçğıöşü]{3}");
    }

    private static bool IsHeading(string folded) =>
        Regex.IsMatch(folded, @"^(e-)?(fis|irsaliye|fatura|arsiv|bilgi fisi)\b");

    private static string? FindDate(IReadOnlyList<string> lines, IReadOnlyList<string> foldedLines)
    {
        string? fallback = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var found = ParseDateInText(lines[i]);
            if (found is null)
                continue;
            if (foldedLines[i].Contains("tarih", StringComparison.Ordinal))
                return found;
            fallback ??= found;
        }

        return fallback;
    }

    private static string? ParseDateInText(string text)
    {
        foreach (Match match in IsoDate.Matches(text))
        {
            if (TryDate(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                    out var iso))
                return iso;
        }

        foreach (Match match in Date4.Matches(text))
        {
            if (TryDate(int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    out var iso))
                return iso;
        }

        foreach (Match match in Date2.Matches(text))
        {
            var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            year += year >= 70 ? 1900 : 2000;
            if (TryDate(year,
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    out var iso))
                return iso;
        }

        return null;
    }

    private static bool TryDate(int year, int month, int day, out string iso)
    {
        iso = "";
        if (year is < 1990 or > 2100 || month is < 1 or > 12 || day is < 1 or > 31)
            return false;
        try
        {
            iso = new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string? FindDocumentNo(string foldedAll)
    {
        var match = DocNoRegex.Match(foldedAll);
        return match.Success ? match.Groups[1].Value.ToUpper(Turkish) : null;
    }

    private static string? FindTaxNumber(IReadOnlyList<string> lines, IReadOnlyList<string> foldedLines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var match = TaxRegex.Match(foldedLines[i]);
            if (match.Success)
                return match.Groups[1].Value;

            if (Regex.IsMatch(foldedLines[i], @"^(vkn|tckn|vergi no|vn)\b") && i + 1 < lines.Count)
            {
                var digits = Regex.Match(lines[i + 1], @"\b(\d{10,11})\b");
                if (digits.Success)
                    return digits.Groups[1].Value;
            }
        }

        return null;
    }

    private static string DetectType(string foldedAll)
    {
        if (Regex.IsMatch(foldedAll, @"\b(sevk\s*)?irsaliye\b"))
            return "İrsaliye";
        if (Regex.IsMatch(foldedAll, @"\b(e-?\s*fatura|e-?\s*arsiv\s*fatura|fatura)\b"))
            return "Fatura";
        if (Regex.IsMatch(foldedAll, @"\b(fis|e-arsiv|yazar\s*kasa|pos)\b"))
            return "Fiş";
        return "Belge";
    }

    private static decimal? FindKeywordTax(IReadOnlyList<string> lines, IReadOnlyList<string> foldedLines)
    {
        (int Weight, decimal Amount)? best = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var folded = foldedLines[i];
            var weight = 0;
            if (Regex.IsMatch(folded, @"\b(toplam\s*)?kdv\b") || folded.Contains("kdv tutar", StringComparison.Ordinal))
                weight = 80;
            else if (folded.Contains("kdv", StringComparison.Ordinal) && !folded.Contains("dahil", StringComparison.Ordinal))
                weight = 50;
            if (weight == 0)
                continue;
            if (folded.Contains("genel toplam", StringComparison.Ordinal) || folded.Contains("odenecek", StringComparison.Ordinal))
                continue;

            var amounts = ExtractMoney(lines[i]);
            if (amounts.Count == 0 && i + 1 < lines.Count)
                amounts = ExtractMoney(lines[i + 1]);
            if (amounts.Count == 0)
                continue;

            var amount = amounts[^1];
            if (best is null || weight > best.Value.Weight)
                best = (weight, amount);
        }

        return best?.Amount;
    }

    private static decimal? FindKeywordSubTotal(IReadOnlyList<string> lines, IReadOnlyList<string> foldedLines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var folded = foldedLines[i];
            if (!folded.Contains("ara toplam", StringComparison.Ordinal)
                && !folded.Contains("matrah", StringComparison.Ordinal)
                && !folded.Contains("kdv matrah", StringComparison.Ordinal))
                continue;

            var amounts = ExtractMoney(lines[i]);
            if (amounts.Count == 0 && i + 1 < lines.Count)
                amounts = ExtractMoney(lines[i + 1]);
            if (amounts.Count > 0)
                return amounts[^1];
        }

        return null;
    }

    private static decimal? FindKeywordTotal(IReadOnlyList<string> lines, IReadOnlyList<string> foldedLines)
    {
        (int Weight, decimal Amount, int Index)? best = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var folded = foldedLines[i];
            var weight = 0;
            if (folded.Contains("genel toplam", StringComparison.Ordinal)) weight = 100;
            else if (folded.Contains("odenecek", StringComparison.Ordinal)) weight = 90;
            else if (folded.Contains("tahsil", StringComparison.Ordinal)) weight = 80;
            else if (folded.Contains("toplam tutar", StringComparison.Ordinal)) weight = 70;
            else if (Regex.IsMatch(folded, @"\btoplam\b") && !folded.Contains("ara toplam", StringComparison.Ordinal)) weight = 55;
            else if (folded.Contains("ara toplam", StringComparison.Ordinal)) weight = 25;
            if (weight == 0)
                continue;
            if (folded.Contains("kdv", StringComparison.Ordinal) && weight < 80)
                continue;

            var amounts = ExtractMoney(lines[i]);
            if (amounts.Count == 0 && i + 1 < lines.Count && !IsMetaRow(foldedLines[i + 1]))
                amounts = ExtractMoney(lines[i + 1]);
            if (amounts.Count == 0)
                continue;

            var candidate = (weight, amounts[^1], i);
            if (best is null || candidate.weight > best.Value.Weight || (candidate.weight == best.Value.Weight && candidate.i > best.Value.Index))
                best = candidate;
        }

        return best?.Amount;
    }

    private static List<FiscalLineDto> FindLineItems(IReadOnlyList<string> lines, IReadOnlyList<string> foldedLines)
    {
        var items = new List<FiscalLineDto>();
        for (var i = 0; i < lines.Count && items.Count < 40; i++)
        {
            if (IsMetaRow(foldedLines[i]) || IsHeading(foldedLines[i]))
                continue;

            var item = ParseLine(lines[i]);
            if (item is not null)
                items.Add(item);
        }

        return items;
    }

    private static FiscalLineDto? ParseLine(string line)
    {
        var parts = Regex.Split(line.Trim(), @"\s+").Where(part => part.Length > 0).ToList();
        if (parts.Count == 0)
            return null;

        var used = new HashSet<int>();
        var moneys = new List<decimal>();
        var moneyIndexes = new List<int>();
        for (var i = 0; i < parts.Count; i++)
        {
            if (!TryParseMoney(parts[i], out var money))
                continue;
            moneys.Add(money);
            moneyIndexes.Add(i);
            used.Add(i);
        }

        if (moneys.Count == 0)
            return null;

        var amount = moneys[^1];
        var unitPrice = moneys.Count >= 2 ? moneys[^2] : 0m;
        var unit = "AD";
        for (var i = 0; i < parts.Count; i++)
        {
            if (used.Contains(i))
                continue;
            var normalized = parts[i].Trim('.', ',', ':').ToUpper(Turkish);
            normalized = Fold(normalized).ToUpperInvariant();
            if (!Units.Contains(normalized) && normalized is not ("KOLI" or "CUVAL"))
                continue;
            unit = normalized switch
            {
                "ADET" or "ADT" => "AD",
                "PAKET" or "PKT" => "PK",
                "KOLI" => "KL",
                "CUVAL" => "ÇUVAL",
                "G" or "GR" => "GR",
                "L" => "LT",
                _ => normalized
            };
            used.Add(i);
            break;
        }

        decimal quantity = 0;
        var firstMoney = moneyIndexes[0];
        for (var i = firstMoney - 1; i >= 0; i--)
        {
            if (used.Contains(i) || !TryParseQuantity(parts[i], out var parsed))
                continue;
            quantity = parsed;
            used.Add(i);
            break;
        }

        if (quantity <= 0)
            quantity = 1;
        if (unitPrice == 0 && amount > 0)
            unitPrice = decimal.Round(amount / quantity, 2, MidpointRounding.AwayFromZero);

        var description = Clean(string.Join(" ", parts.Where((_, index) => !used.Contains(index))));
        if (description.Length < 2 || !Regex.IsMatch(description, @"[A-Za-zÇĞİÖŞÜçğıöşü]"))
        {
            if (amount <= 0)
                return null;
            description = "Kalem";
        }

        return new FiscalLineDto
        {
            Description = description,
            Quantity = quantity,
            Unit = unit,
            UnitPrice = unitPrice,
            Amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero)
        };
    }

    private static List<decimal> ExtractMoney(string line)
    {
        var amounts = new List<decimal>();
        foreach (var part in Regex.Split(line, @"\s+"))
        {
            if (TryParseMoney(part, out var money))
                amounts.Add(money);
        }

        return amounts;
    }

    private static bool TryParseMoney(string raw, out decimal value)
    {
        value = 0;
        var token = raw.Trim().Trim('₺', '*', '|', ':', ';', '(', ')');
        token = token.Replace("TL", "", StringComparison.OrdinalIgnoreCase).Replace("₺", "", StringComparison.Ordinal).Trim();
        if (token.Length == 0 || Regex.IsMatch(token, @"^\d{1,2}[./-]\d{1,2}[./-]\d{2,4}$"))
            return false;

        string normalized;
        if (Regex.IsMatch(token, @"^\d{1,3}(\.\d{3})+,\d{2}$"))
            normalized = token.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        else if (Regex.IsMatch(token, @"^\d+,\d{2}$"))
            normalized = token.Replace(',', '.');
        else if (Regex.IsMatch(token, @"^\d{1,3}(,\d{3})+\.\d{2}$"))
            normalized = token.Replace(",", "", StringComparison.Ordinal);
        else if (Regex.IsMatch(token, @"^\d{3,}\.\d{2}$"))
            normalized = token;
        else
            return false;

        if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            return false;
        return value is >= 0 and < 100_000_000m;
    }

    private static bool TryParseQuantity(string raw, out decimal value)
    {
        value = 0;
        var token = raw.Trim().Trim('*', 'x', 'X', '.');
        if (!Regex.IsMatch(token, @"^\d{1,5}([.,]\d{1,3})?$"))
            return false;
        if (Regex.IsMatch(token, @"^\d+,\d{2}$") || Regex.IsMatch(token, @"^\d{3,}\.\d{2}$"))
            return false;

        var normalized = token.Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            return false;
        return value is > 0 and < 100_000m;
    }

    private static bool IsMetaRow(string folded)
    {
        string[] keys =
        [
            "genel toplam", "ara toplam", "toplam tutar", "toplam",
            "kdv", "nakit", "kredi", "para ustu", "iskonto", "indirim",
            "tesekkur", "kasiyer", "fis no", "irsaliye no", "belge no",
            "e-arsiv", "z no", "eku no"
        ];
        foreach (var key in keys)
        {
            var index = folded.IndexOf(key, StringComparison.Ordinal);
            if (index is >= 0 and <= 16)
                return true;
        }

        return Regex.IsMatch(folded, @"^(tarih|saat|tel|telefon|adres|vkn|tckn|vergi|www)\b");
    }

    private static string Clean(string value)
    {
        var collapsed = Regex.Replace(value, @"\s+", " ").Trim().Trim('-', ':', '|', '*');
        return collapsed.Length <= 200 ? collapsed : collapsed[..200];
    }

    private static string Fold(string? value)
    {
        var lower = (value ?? "").ToLower(Turkish);
        var builder = new StringBuilder(lower.Length);
        foreach (var character in lower)
        {
            builder.Append(character switch
            {
                'ç' => 'c',
                'ğ' => 'g',
                'ı' => 'i',
                'ö' => 'o',
                'ş' => 's',
                'ü' => 'u',
                'â' => 'a',
                'î' => 'i',
                'û' => 'u',
                _ => character
            });
        }

        return builder.ToString();
    }

    private sealed record OcrToken(string Text, float Score, float X, float Y, float Height);
}
