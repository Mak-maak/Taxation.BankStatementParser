using ClosedXML.Excel;
using Taxation.StatementParser.Console.Models;
using Taxation.StatementParser.Console.Parsing;

namespace Taxation.StatementParser.Console.Excel;

/// <summary>
/// Writes parsed transactions into a formatted Excel workbook containing a single worksheet
/// with a native Excel table (banded rows, bold header, auto filter).
/// Values are written using their semantic <see cref="ColumnType"/> so dates sort/format as dates
/// and amounts as numbers, while any value that cannot be safely parsed is preserved as text.
/// </summary>
public sealed class ExcelStatementWriter
{
    private const double MaxColumnWidth = 60;
    private const string AmountNumberFormat = "#,##0.00;(#,##0.00)";
    private const string DateNumberFormat = "dd/MM/yyyy";

    /// <summary>Row that carries the soft verification notice; all data starts on the next row.</summary>
    private const int BannerRow = 1;

    private const string WarningMessage =
        "\u26A0  Please review: this workbook was generated automatically from a PDF statement. " +
        "Kindly verify the parsed transactions and accumulated totals against the original statement before relying on them.";

    public void Write(
        string outputPath,
        IReadOnlyList<string> columns,
        IReadOnlyList<StatementTransaction> transactions,
        IReadOnlyList<ColumnType>? columnTypes = null,
        AccumulationResult? accumulation = null,
        string? openPassword = null,
        StatementAccountInfo? accountInfo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(transactions);

        IReadOnlyList<ColumnType> types = columnTypes ?? ColumnClassifier.ClassifyAll(columns);
        StatementAccountInfo info = accountInfo ?? StatementAccountInfo.Empty;

        using var workbook = new XLWorkbook();
        ApplyConfidentialClassification(workbook);
        WriteTransactionsSheet(workbook, columns, transactions, types, info);

        if (accumulation is not null && accumulation.Groups.Count > 0)
        {
            WriteAccumulatedSheet(workbook, accumulation, info);
        }

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        workbook.SaveAs(outputPath);

        // The green "Public/Confidential" badge Office shows comes from a Microsoft Information
        // Protection (MIP) sensitivity label, which lives in docProps/custom.xml as MSIP_Label_*
        // properties - NOT in the built-in Status property. ClosedXML cannot write arbitrary custom
        // document properties, so we inject them into the saved package here.
        SensitivityLabel.ApplyConfidential(outputPath);

        // Apply a genuine "password to open" as the final step. Encryption wraps the entire OOXML
        // package (including the sensitivity metadata above) inside an encrypted OLE compound file,
        // so it must run after all package-level edits.
        if (!string.IsNullOrEmpty(openPassword))
        {
            ExcelEncryptor.Encrypt(outputPath, openPassword);
        }
    }

    /// <summary>
    /// Sets the human-readable document status to Confidential. This complements the Microsoft
    /// Information Protection sensitivity label applied after save (see <see cref="SensitivityLabel"/>).
    /// </summary>
    private static void ApplyConfidentialClassification(XLWorkbook workbook)
    {
        workbook.Properties.Status = "Confidential";
    }

    /// <summary>
    /// Writes a soft, user-friendly verification notice across the top of a sheet so a consultant
    /// reviewing the workbook is gently reminded to sanity-check the software-generated results.
    /// </summary>
    private static void WriteWarningBanner(IXLWorksheet worksheet, int columnCount)
    {
        int span = Math.Max(columnCount, 1);
        IXLRange banner = worksheet.Range(BannerRow, 1, BannerRow, span);
        banner.Merge();

        IXLCell cell = worksheet.Cell(BannerRow, 1);
        cell.Value = WarningMessage;
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontColor = XLColor.FromArgb(0x8A, 0x61, 0x00);
        cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0xFF, 0xF4, 0xCE);
        cell.Style.Alignment.WrapText = true;
        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = XLColor.FromArgb(0xE0, 0xC2, 0x60);

        worksheet.Row(BannerRow).Height = 34;
    }

    /// <summary>
    /// Writes the warning banner followed by an account information block (title, account number,
    /// IBAN and statement period) at the top of a sheet, and returns the row on which the data
    /// header should be written. When no account metadata is available the layout is unchanged, so
    /// the header immediately follows the banner.
    /// </summary>
    private static int WriteTopBlock(IXLWorksheet worksheet, int columnCount, StatementAccountInfo info)
    {
        WriteWarningBanner(worksheet, columnCount);

        int row = BannerRow + 1;
        if (info is null || !info.HasAny)
        {
            return row;
        }

        int span = Math.Max(columnCount, 2);

        void AddInfoRow(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            IXLCell labelCell = worksheet.Cell(row, 1);
            labelCell.Value = label;
            labelCell.Style.Font.Bold = true;
            labelCell.Style.Fill.BackgroundColor = XLColor.FromArgb(0xEE, 0xF2, 0xF8);
            labelCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            IXLRange valueRange = worksheet.Range(row, 2, row, span);
            valueRange.Merge();
            IXLCell valueCell = worksheet.Cell(row, 2);
            valueCell.Value = value;
            valueCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            valueCell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            valueCell.Style.NumberFormat.Format = "@";

            row++;
        }

        AddInfoRow("Account Title", info.AccountTitle ?? string.Empty);
        AddInfoRow("Account Number", info.AccountNumber ?? string.Empty);
        AddInfoRow("IBAN", info.Iban ?? string.Empty);
        AddInfoRow("Statement Period", FormatPeriod(info));

        // A blank spacer row between the account block and the data table for readability.
        row++;
        return row;
    }

    private static string FormatPeriod(StatementAccountInfo info)
    {
        string? from = info.FromDate?.ToString(DateNumberFormat, System.Globalization.CultureInfo.InvariantCulture);
        string? to = info.ToDate?.ToString(DateNumberFormat, System.Globalization.CultureInfo.InvariantCulture);

        return (from, to) switch
        {
            (not null, not null) => $"{from} to {to}",
            (not null, null) => $"From {from}",
            (null, not null) => $"To {to}",
            _ => string.Empty,
        };
    }

    private static void WriteTransactionsSheet(
        XLWorkbook workbook,
        IReadOnlyList<string> columns,
        IReadOnlyList<StatementTransaction> transactions,
        IReadOnlyList<ColumnType> types,
        StatementAccountInfo info)
    {
        IXLWorksheet worksheet = workbook.Worksheets.Add("Transactions");

        int headerRow = WriteTopBlock(worksheet, columns.Count, info);

        // Header row.
        for (int c = 0; c < columns.Count; c++)
        {
            worksheet.Cell(headerRow, c + 1).Value = columns[c];
        }

        // Data rows.
        for (int r = 0; r < transactions.Count; r++)
        {
            StatementTransaction transaction = transactions[r];
            for (int c = 0; c < columns.Count; c++)
            {
                IXLCell cell = worksheet.Cell(headerRow + 1 + r, c + 1);
                WriteTypedValue(cell, transaction[columns[c]], types[c]);
            }
        }

        int lastRow = headerRow + transactions.Count;
        int lastColumn = columns.Count;
        IXLRange range = worksheet.Range(headerRow, 1, lastRow, lastColumn);

        IXLTable table = range.CreateTable("Transactions");
        table.Theme = XLTableTheme.TableStyleMedium2;
        table.ShowAutoFilter = true;

        worksheet.Rows().Style.Alignment.SetWrapText(true);
        worksheet.Rows().Style.Alignment.SetVertical(XLAlignmentVerticalValues.Top);
        worksheet.Columns().AdjustToContents();

        foreach (IXLColumn column in worksheet.ColumnsUsed())
        {
            if (column.Width > MaxColumnWidth)
            {
                column.Width = MaxColumnWidth;
            }
        }

        worksheet.SheetView.FreezeRows(headerRow);
    }

    private static void WriteAccumulatedSheet(XLWorkbook workbook, AccumulationResult accumulation, StatementAccountInfo info)
    {
        IXLWorksheet worksheet = workbook.Worksheets.Add("Accumulated");
        AccumulationRoles roles = accumulation.Roles;

        // Summary rows sit above their detail rows in the outline.
        worksheet.Outline.SummaryVLocation = XLOutlineSummaryVLocation.Top;

        string[] headers =
        [
            "Name", "Account No", "Date", "Description",
            "Debit", "Credit", "Net", "Count", "First Date", "Last Date"
        ];

        int headerRow = WriteTopBlock(worksheet, headers.Length, info);
        for (int c = 0; c < headers.Length; c++)
        {
            IXLCell headerCell = worksheet.Cell(headerRow, c + 1);
            headerCell.Value = headers[c];
            headerCell.Style.Font.Bold = true;
            headerCell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x1F, 0x4E, 0x79);
            headerCell.Style.Font.FontColor = XLColor.White;
        }

        int row = headerRow + 1;
        foreach (TransactionGroup group in accumulation.Groups)
        {
            int summaryRow = row;
            WriteGroupSummaryRow(worksheet, summaryRow, group);
            row++;

            int firstDetailRow = row;
            foreach (StatementTransaction transaction in group.Transactions)
            {
                WriteGroupDetailRow(worksheet, row, transaction, roles);
                row++;
            }

            int lastDetailRow = row - 1;
            if (lastDetailRow >= firstDetailRow)
            {
                worksheet.Rows(firstDetailRow, lastDetailRow).Group();
                worksheet.Rows(firstDetailRow, lastDetailRow).Collapse();
            }
        }

        worksheet.Columns().AdjustToContents();
        foreach (IXLColumn column in worksheet.ColumnsUsed())
        {
            if (column.Width > MaxColumnWidth)
            {
                column.Width = MaxColumnWidth;
            }
        }

        worksheet.SheetView.FreezeRows(headerRow);
    }

    private static void WriteGroupSummaryRow(IXLWorksheet worksheet, int row, TransactionGroup group)
    {
        worksheet.Cell(row, 1).Value = group.Name;
        worksheet.Cell(row, 2).Value = group.AccountNumber;

        SetAmount(worksheet.Cell(row, 5), group.TotalDebit);
        SetAmount(worksheet.Cell(row, 6), group.TotalCredit);
        SetAmount(worksheet.Cell(row, 7), group.Net);

        worksheet.Cell(row, 8).Value = group.Count;

        if (group.FirstDate is DateTime first)
        {
            SetDate(worksheet.Cell(row, 9), first);
        }

        if (group.LastDate is DateTime last)
        {
            SetDate(worksheet.Cell(row, 10), last);
        }

        IXLRange summary = worksheet.Range(row, 1, row, 10);
        summary.Style.Font.Bold = true;
        summary.Style.Fill.BackgroundColor = XLColor.FromArgb(0xDD, 0xEB, 0xF7);
    }

    private static void WriteGroupDetailRow(IXLWorksheet worksheet, int row, StatementTransaction transaction, AccumulationRoles roles)
    {
        if (roles.DateColumn is not null && ColumnClassifier.TryParseDate(transaction[roles.DateColumn], out DateTime date))
        {
            SetDate(worksheet.Cell(row, 3), date);
        }
        else if (roles.DateColumn is not null)
        {
            worksheet.Cell(row, 3).Value = transaction[roles.DateColumn];
        }

        worksheet.Cell(row, 4).Value = transaction[roles.DescriptionColumn];

        if (roles.DebitColumn is not null && ColumnClassifier.TryParseAmount(transaction[roles.DebitColumn], out decimal debit))
        {
            SetAmount(worksheet.Cell(row, 5), debit);
        }

        if (roles.CreditColumn is not null && ColumnClassifier.TryParseAmount(transaction[roles.CreditColumn], out decimal credit))
        {
            SetAmount(worksheet.Cell(row, 6), credit);
        }

        worksheet.Cell(row, 4).Style.Alignment.WrapText = true;
    }

    private static void SetAmount(IXLCell cell, decimal value)
    {
        cell.Value = value;
        cell.Style.NumberFormat.Format = AmountNumberFormat;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
    }

    private static void SetDate(IXLCell cell, DateTime value)
    {
        cell.Value = value;
        cell.Style.DateFormat.Format = DateNumberFormat;
    }

    private static void WriteTypedValue(IXLCell cell, string raw, ColumnType type)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            cell.Value = string.Empty;
            return;
        }

        switch (type)
        {
            case ColumnType.Amount when ColumnClassifier.TryParseAmount(raw, out decimal amount):
                cell.Value = amount;
                cell.Style.NumberFormat.Format = AmountNumberFormat;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                break;

            case ColumnType.Date when ColumnClassifier.TryParseDate(raw, out DateTime date):
                cell.Value = date;
                cell.Style.DateFormat.Format = DateNumberFormat;
                break;

            default:
                // Unparseable value or free text: preserve exactly as text (no data loss).
                cell.Value = raw;
                cell.Style.NumberFormat.Format = "@";
                break;
        }
    }
}
