using ClosedXML.Excel;
using FiscOCRApp.Models;

namespace FiscOCRApp.Services;

public interface IExcelExportService
{
    byte[] Export(FiscalDocumentDto document);
    byte[] ExportMany(IReadOnlyList<FiscalDocumentDto> documents);
}

public sealed class ExcelExportService : IExcelExportService
{
    public byte[] Export(FiscalDocumentDto document) =>
        BuildWorkbook([document]);

    public byte[] ExportMany(IReadOnlyList<FiscalDocumentDto> documents)
    {
        if (documents is null || documents.Count == 0)
            throw new InvalidOperationException("Aktarılacak belge yok.");
        return BuildWorkbook(documents);
    }

    private static byte[] BuildWorkbook(IReadOnlyList<FiscalDocumentDto> documents)
    {
        using var workbook = new XLWorkbook();
        workbook.Properties.Title = "FiscOCR Belge Aktarımı";
        workbook.Properties.Author = "FiscOCR";
        workbook.Properties.Company = "FiscOCR";

        if (documents.Count == 1)
        {
            WriteDocumentSheet(workbook, documents[0], SheetName(documents[0], 1));
        }
        else
        {
            WriteSummarySheet(workbook, documents);
            for (var i = 0; i < documents.Count; i++)
                WriteDocumentSheet(workbook, documents[i], SheetName(documents[i], i + 1));
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteSummarySheet(XLWorkbook workbook, IReadOnlyList<FiscalDocumentDto> documents)
    {
        var sheet = workbook.Worksheets.Add("Özet");
        sheet.Style.Font.FontName = "Calibri";
        sheet.Style.Font.FontSize = 11;

        string[] headers = ["No", "Firma", "Belge Türü", "Belge No", "Tarih", "Ara Toplam", "KDV", "Genel Toplam", "Kalem"];
        for (var column = 0; column < headers.Length; column++)
            StyleHeader(sheet.Cell(1, column + 1), headers[column]);

        for (var i = 0; i < documents.Count; i++)
        {
            var doc = documents[i];
            var row = i + 2;
            sheet.Cell(row, 1).Value = i + 1;
            WriteText(sheet, row, 2, doc.Company);
            WriteText(sheet, row, 3, doc.DocumentType);
            WriteText(sheet, row, 4, doc.DocumentNo);
            WriteText(sheet, row, 5, doc.Date);
            WriteMoney(sheet, row, 6, doc.SubTotal);
            WriteMoney(sheet, row, 7, doc.TaxTotal);
            WriteMoney(sheet, row, 8, doc.Total);
            sheet.Cell(row, 9).Value = doc.Lines?.Count ?? 0;
        }

        var totalRow = documents.Count + 2;
        sheet.Cell(totalRow, 5).Value = "TOPLAM";
        sheet.Cell(totalRow, 5).Style.Font.Bold = true;
        WriteMoney(sheet, totalRow, 6, documents.Sum(d => d.SubTotal));
        WriteMoney(sheet, totalRow, 7, documents.Sum(d => d.TaxTotal));
        WriteMoney(sheet, totalRow, 8, documents.Sum(d => d.Total));
        sheet.Range(totalRow, 6, totalRow, 8).Style.Font.Bold = true;
        sheet.Range(totalRow, 6, totalRow, 8).Style.Fill.BackgroundColor = XLColor.FromHtml("#E7EEF3");

        FinalizeSheet(sheet, 1, totalRow, 9);
    }

    private static void WriteDocumentSheet(XLWorkbook workbook, FiscalDocumentDto document, string sheetName)
    {
        var sheet = workbook.Worksheets.Add(sheetName);
        sheet.Style.Font.FontName = "Calibri";
        sheet.Style.Font.FontSize = 11;
        sheet.ShowGridLines = true;

        sheet.Cell(1, 1).Value = "FiscOCR — " + (document.DocumentType ?? "Belge");
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;
        sheet.Range(1, 1, 1, 7).Merge();

        sheet.Cell(2, 1).Value = "Firma";
        sheet.Cell(2, 2).Value = Sanitize(document.Company);
        sheet.Cell(2, 3).Value = "VKN/TCKN";
        sheet.Cell(2, 4).Value = Sanitize(document.TaxNumber);
        sheet.Cell(3, 1).Value = "Belge No";
        sheet.Cell(3, 2).Value = Sanitize(document.DocumentNo);
        sheet.Cell(3, 3).Value = "Tarih";
        sheet.Cell(3, 4).Value = Sanitize(document.Date);
        sheet.Range(2, 1, 3, 1).Style.Font.Bold = true;
        sheet.Range(2, 3, 3, 3).Style.Font.Bold = true;

        string[] headers =
        [
            "İşletme Adı", "Belge No", "Tarih",
            "Ürün / Kalem Adı", "Adet", "Birim Fiyat (₺)", "Toplam Tutar (₺)"
        ];

        const int headerRow = 5;
        for (var column = 0; column < headers.Length; column++)
            StyleHeader(sheet.Cell(headerRow, column + 1), headers[column]);

        sheet.Row(headerRow).Height = 22;
        var lines = document.Lines ?? [];
        var currentRow = headerRow + 1;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var row = currentRow + index;
            WriteText(sheet, row, 1, document.Company);
            WriteText(sheet, row, 2, document.DocumentNo);
            WriteText(sheet, row, 3, document.Date);
            WriteText(sheet, row, 4, line.Description);

            sheet.Cell(row, 5).Value = (double)line.Quantity;
            sheet.Cell(row, 5).Style.NumberFormat.Format = "#,##0.###";
            sheet.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            WriteMoney(sheet, row, 6, line.UnitPrice);
            WriteMoney(sheet, row, 7, line.Amount);

            if (index % 2 == 1)
                sheet.Range(row, 1, row, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#F7F8FA");
        }

        var lastDataRow = headerRow + Math.Max(lines.Count, 1);
        var tableRange = sheet.Range(headerRow, 1, lastDataRow, 7);
        tableRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        tableRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        tableRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#D7DDE3");
        tableRange.Style.Border.InsideBorderColor = XLColor.FromHtml("#D7DDE3");

        if (lines.Count > 0)
            sheet.Range(headerRow, 1, headerRow, 7).SetAutoFilter();

        var araRow = lastDataRow + 1;
        WriteSummaryRow(sheet, araRow, "ARA TOPLAM", document.SubTotal > 0 ? document.SubTotal : lines.Sum(l => l.Amount), "#F7F8FA");

        var kdvRow = lastDataRow + 2;
        WriteSummaryRow(sheet, kdvRow, "TOP KDV", document.TaxTotal, "#F2F4F7");

        var totalRow = lastDataRow + 3;
        WriteSummaryRow(sheet, totalRow, "GENEL TOPLAM", document.Total, "#E7EEF3");

        sheet.Range(araRow, 1, totalRow, 7).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        sheet.Range(araRow, 1, totalRow, 7).Style.Border.OutsideBorderColor = XLColor.FromHtml("#B0BEC5");

        sheet.SheetView.FreezeRows(headerRow);
        FinalizeSheet(sheet, 1, totalRow, 7);
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.PagesWide = 1;
    }

    private static void WriteSummaryRow(IXLWorksheet sheet, int row, string label, decimal amount, string fill)
    {
        sheet.Range(row, 1, row, 6).Merge();
        var labelCell = sheet.Cell(row, 1);
        labelCell.Value = label;
        labelCell.Style.Font.Bold = true;
        labelCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

        var amountCell = sheet.Cell(row, 7);
        amountCell.Value = (double)amount;
        amountCell.Style.NumberFormat.Format = "#,##0.00";
        amountCell.Style.Font.Bold = true;
        amountCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        amountCell.Style.Fill.BackgroundColor = XLColor.FromHtml(fill);
    }

    private static void StyleHeader(IXLCell cell, string text)
    {
        cell.Value = text;
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontColor = XLColor.FromHtml("#2C333A");
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF1F4");
        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static void FinalizeSheet(IXLWorksheet sheet, int fromRow, int toRow, int columns)
    {
        foreach (var column in sheet.Columns(1, columns))
        {
            column.AdjustToContents();
            if (column.Width < 12) column.Width = 12;
            if (column.Width > 45) column.Width = 45;
        }
    }

    private static string SheetName(FiscalDocumentDto document, int index)
    {
        var type = string.IsNullOrWhiteSpace(document.DocumentType) ? "Belge" : document.DocumentType.Trim();
        var company = Sanitize(document.Company);
        if (company.Length > 18)
            company = company[..18];
        var raw = $"{index}-{type}-{company}";
        foreach (var ch in Path.GetInvalidFileNameChars().Concat([':', '\\', '/', '?', '*', '[', ']']))
            raw = raw.Replace(ch, '-');
        raw = raw.Trim('-', ' ');
        if (string.IsNullOrWhiteSpace(raw))
            raw = $"Belge-{index}";
        return raw.Length <= 31 ? raw : raw[..31];
    }

    private static void WriteText(IXLWorksheet sheet, int row, int column, string? value) =>
        sheet.Cell(row, column).Value = Sanitize(value);

    private static void WriteMoney(IXLWorksheet sheet, int row, int column, decimal value)
    {
        sheet.Cell(row, column).Value = (double)value;
        sheet.Cell(row, column).Style.NumberFormat.Format = "#,##0.00";
        sheet.Cell(row, column).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var text = value.Trim();
        if (text.Length > 300)
            text = text[..300];
        return text[0] is '=' or '+' or '-' or '@' ? "'" + text : text;
    }
}
