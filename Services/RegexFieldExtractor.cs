using System.Globalization;
using System.Text.RegularExpressions;
using FiscOCRApp.Models;

namespace FiscOCRApp.Services;

/// <summary>
/// LLM beklemeden sabit alanları milisaniyeler içinde çıkarır.
/// </summary>
public interface IRegexFieldExtractor
{
    RegexExtractionSnapshot Extract(string rawText);
    void ApplyToDocument(FiscalDocumentDto document, RegexExtractionSnapshot snapshot);
}

public sealed class RegexFieldExtractor : IRegexFieldExtractor
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private static readonly Regex TaxRegex = new(
        @"\b(?:vkn|tckn|vergi\s*no|vn)\s*[:\-]?\s*(\d{10,11})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DocNoRegex = new(
        @"\b(?:fis|fi[sş]|irsaliye|fatura|belge|evrak|e-?fatura)\s*(?:no|numarasi|numarası)?\s*[:\-]?\s*([a-z0-9][a-z0-9\-/]{0,30})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Date4 = new(
        @"\b(\d{2})[./-](\d{2})[./-](\d{4})\b",
        RegexOptions.Compiled);

    private static readonly Regex IsoDate = new(
        @"\b(\d{4})[./-](\d{2})[./-](\d{2})\b",
        RegexOptions.Compiled);

    private static readonly Regex TotalRegex = new(
        @"(?:genel\s*toplam|odenecek|ödenecek|toplam\s*tutar|toplam)\s*[:\-]?\s*[₺*]?\s*([\d.,]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TaxTotalRegex = new(
        @"(?:top\s*kdv|toplam\s*kdv|kdv\s*tutari|kdv\s*tutarı)\s*[:\-]?\s*[₺*]?\s*([\d.,]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SubTotalRegex = new(
        @"(?:ara\s*toplam|matrah|kdv\s*matrah)\s*[:\-]?\s*[₺*]?\s*([\d.,]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public RegexExtractionSnapshot Extract(string rawText)
    {
        var snapshot = new RegexExtractionSnapshot();
        if (string.IsNullOrWhiteSpace(rawText))
            return snapshot;

        var folded = Fold(rawText);
        snapshot.Date = FindDate(rawText);
        snapshot.TaxNumber = FindTax(folded, rawText);
        snapshot.DocumentNo = FindDocNo(folded);
        snapshot.DocumentType = DetectType(folded);
        snapshot.Total = FindMoney(folded, TotalRegex, preferLast: true);
        snapshot.TaxTotal = FindMoney(folded, TaxTotalRegex, preferLast: false);
        snapshot.SubTotal = FindMoney(folded, SubTotalRegex, preferLast: false);
        snapshot.CompanyHint = FindCompanyHint(rawText);
        return snapshot;
    }

    public void ApplyToDocument(FiscalDocumentDto document, RegexExtractionSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(document.Date) && !string.IsNullOrWhiteSpace(snapshot.Date))
            document.Date = snapshot.Date;
        if (string.IsNullOrWhiteSpace(document.TaxNumber) && !string.IsNullOrWhiteSpace(snapshot.TaxNumber))
            document.TaxNumber = snapshot.TaxNumber;
        if (string.IsNullOrWhiteSpace(document.DocumentNo) && !string.IsNullOrWhiteSpace(snapshot.DocumentNo))
            document.DocumentNo = snapshot.DocumentNo;
        if ((string.IsNullOrWhiteSpace(document.DocumentType) || document.DocumentType == "Belge")
            && !string.IsNullOrWhiteSpace(snapshot.DocumentType))
            document.DocumentType = snapshot.DocumentType!;
        if (document.Total <= 0 && snapshot.Total is > 0)
            document.Total = snapshot.Total.Value;
        if (document.TaxTotal <= 0 && snapshot.TaxTotal is > 0)
            document.TaxTotal = snapshot.TaxTotal.Value;
        if (document.SubTotal <= 0 && snapshot.SubTotal is > 0)
            document.SubTotal = snapshot.SubTotal.Value;
        if (string.IsNullOrWhiteSpace(document.Company) && !string.IsNullOrWhiteSpace(snapshot.CompanyHint))
            document.Company = snapshot.CompanyHint!;
    }

    private static string? FindDate(string text)
    {
        foreach (Match match in IsoDate.Matches(text))
        {
            if (TryDate(
                    int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                    out var iso))
                return iso;
        }

        foreach (Match match in Date4.Matches(text))
        {
            if (TryDate(
                    int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
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

    private static string? FindTax(string folded, string original)
    {
        var match = TaxRegex.Match(folded);
        if (match.Success)
            return match.Groups[1].Value;
        var digits = Regex.Match(original, @"\b(\d{10,11})\b");
        return digits.Success ? digits.Groups[1].Value : null;
    }

    private static string? FindDocNo(string folded)
    {
        var match = DocNoRegex.Match(folded);
        return match.Success ? match.Groups[1].Value.ToUpper(Turkish) : null;
    }

    private static string? DetectType(string folded)
    {
        if (Regex.IsMatch(folded, @"\b(sevk\s*)?irsaliye\b"))
            return "İrsaliye";
        if (Regex.IsMatch(folded, @"\b(e-?\s*fatura|fatura)\b"))
            return "Fatura";
        if (Regex.IsMatch(folded, @"\b(fis|yazar\s*kasa|pos)\b"))
            return "Fiş";
        return null;
    }

    private static decimal? FindMoney(string folded, Regex regex, bool preferLast)
    {
        var matches = regex.Matches(folded);
        if (matches.Count == 0)
            return null;
        var pick = preferLast ? matches[^1] : matches[0];
        return TryParseMoney(pick.Groups[1].Value, out var value) ? value : null;
    }

    private static bool TryParseMoney(string raw, out decimal value)
    {
        value = 0;
        var token = raw.Trim().Trim('₺', '*');
        if (token.Length == 0)
            return false;
        string normalized;
        if (Regex.IsMatch(token, @"^\d{1,3}(\.\d{3})+,\d{2}$"))
            normalized = token.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        else if (Regex.IsMatch(token, @"^\d+,\d{1,2}$"))
            normalized = token.Replace(',', '.');
        else if (Regex.IsMatch(token, @"^\d+\.\d{2}$"))
            normalized = token;
        else
            return false;
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
            && value is >= 0 and < 100_000_000m;
    }

    private static string? FindCompanyHint(string rawText)
    {
        var lines = rawText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines.Take(8))
        {
            var folded = Fold(line);
            if (folded.Contains("a.s", StringComparison.Ordinal)
                || folded.Contains("ltd", StringComparison.Ordinal)
                || folded.Contains("ticaret", StringComparison.Ordinal)
                || folded.Contains("magaza", StringComparison.Ordinal)
                || folded.Contains("mağaza", StringComparison.Ordinal))
            {
                var clean = Regex.Replace(line, @"\s+", " ").Trim();
                return clean.Length <= 200 ? clean : clean[..200];
            }
        }

        return null;
    }

    private static string Fold(string value)
    {
        var lower = value.ToLower(Turkish);
        return lower
            .Replace('ı', 'i').Replace('İ', 'i').Replace('ş', 's').Replace('ğ', 'g')
            .Replace('ü', 'u').Replace('ö', 'o').Replace('ç', 'c');
    }
}
