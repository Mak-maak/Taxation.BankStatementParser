using ClosedXML.Excel;
using FluentAssertions;
using System.IO.Packaging;
using System.Xml.Linq;
using Taxation.StatementParser.Console.Excel;
using Taxation.StatementParser.Console.Models;
using Taxation.StatementParser.Console.Parsing;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class ExcelStatementWriterTests
{
    private static readonly string[] Columns = ["Transaction Date", "Description", "Debit", "Credit", "Available Balance"];

    [Fact]
    public void Write_ShouldProduceWorkbook_WithTypedCells()
    {
        var t1 = new StatementTransaction(Columns);
        t1.SetColumn("Transaction Date", "01/01/2025");
        t1.SetColumn("Description", "Opening balance");
        t1.SetColumn("Available Balance", "1,000.00");

        var t2 = new StatementTransaction(Columns);
        t2.SetColumn("Transaction Date", "02/01/2025");
        t2.SetColumn("Description", "Card payment Amazon UK");
        t2.SetColumn("Debit", "50.00");
        t2.SetColumn("Available Balance", "950.00");

        string path = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid()}.xlsx");
        try
        {
            new ExcelStatementWriter().Write(path, Columns, [t1, t2]);

            File.Exists(path).Should().BeTrue();

            using var workbook = new XLWorkbook(path);
            IXLWorksheet worksheet = workbook.Worksheet("Transactions");

            // Row 1 carries the soft verification notice.
            worksheet.Cell(1, 1).GetString().Should().Contain("verify");

            // Header row (row 2, below the banner).
            worksheet.Cell(2, 1).GetString().Should().Be("Transaction Date");
            worksheet.Cell(2, 5).GetString().Should().Be("Available Balance");

            // Date column parsed as a real date.
            worksheet.Cell(3, 1).DataType.Should().Be(XLDataType.DateTime);
            worksheet.Cell(3, 1).GetDateTime().Should().Be(new DateTime(2025, 1, 1));

            // Amount column parsed as a number.
            worksheet.Cell(3, 5).DataType.Should().Be(XLDataType.Number);
            worksheet.Cell(3, 5).GetDouble().Should().Be(1000.00);

            // Description preserved as text (merged multi-line).
            worksheet.Cell(4, 2).GetString().Should().Be("Card payment Amazon UK");
            worksheet.Cell(4, 3).GetDouble().Should().Be(50.00);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_WhenAmountUnparseable_ShouldFallBackToText()
    {
        var t1 = new StatementTransaction(Columns);
        t1.SetColumn("Transaction Date", "not-a-date");
        t1.SetColumn("Debit", "PENDING");

        string path = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid()}.xlsx");
        try
        {
            new ExcelStatementWriter().Write(path, Columns, [t1]);

            using var workbook = new XLWorkbook(path);
            IXLWorksheet worksheet = workbook.Worksheet("Transactions");

            worksheet.Cell(3, 1).GetString().Should().Be("not-a-date");
            worksheet.Cell(3, 3).GetString().Should().Be("PENDING");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_WithNullOutputPath_ShouldThrow()
    {
        Action act = () => new ExcelStatementWriter().Write(null!, Columns, []);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Write_ShouldClassifyWorkbookAsConfidential()
    {
        var t1 = new StatementTransaction(Columns);
        t1.SetColumn("Transaction Date", "01/01/2025");
        t1.SetColumn("Description", "Opening balance");

        string path = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid()}.xlsx");
        try
        {
            new ExcelStatementWriter().Write(path, Columns, [t1]);

            using var workbook = new XLWorkbook(path);
            workbook.Properties.Status.Should().Be("Confidential");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_ShouldApplyConfidentialSensitivityLabel()
    {
        var t1 = new StatementTransaction(Columns);
        t1.SetColumn("Transaction Date", "01/01/2025");
        t1.SetColumn("Description", "Opening balance");

        string path = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid()}.xlsx");
        try
        {
            new ExcelStatementWriter().Write(path, Columns, [t1]);

            // The MIP sensitivity label lives in docProps/custom.xml as MSIP_Label_* properties.
            using Package package = Package.Open(path, FileMode.Open, FileAccess.Read);
            var partUri = new Uri("/docProps/custom.xml", UriKind.Relative);
            package.PartExists(partUri).Should().BeTrue();

            PackagePart part = package.GetPart(partUri);
            using Stream stream = part.GetStream(FileMode.Open, FileAccess.Read);
            XDocument document = XDocument.Load(stream);

            string xml = document.ToString();
            xml.Should().Contain("MSIP_Label_");
            xml.Should().Contain("_Name");
            xml.Should().Contain("Confidential");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_WithAccumulation_ShouldAddAccumulatedSheet()
    {
        var t1 = new StatementTransaction(Columns);
        t1.SetColumn("Transaction Date", "01/01/2025");
        t1.SetColumn("Description", "ACME LTD XXXXXX1234 REF 1");
        t1.SetColumn("Debit", "100.00");

        var t2 = new StatementTransaction(Columns);
        t2.SetColumn("Transaction Date", "05/01/2025");
        t2.SetColumn("Description", "ACME LTD XXXXXX1234 REF 2");
        t2.SetColumn("Debit", "50.00");

        var types = ColumnClassifier.ClassifyAll(Columns);
        var accumulation = AccumulationEngine.Build(Columns, types, [t1, t2]);

        string path = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid()}.xlsx");
        try
        {
            new ExcelStatementWriter().Write(path, Columns, [t1, t2], types, accumulation);

            using var workbook = new XLWorkbook(path);
            workbook.Worksheets.Any(w => w.Name == "Accumulated").Should().BeTrue();

            IXLWorksheet sheet = workbook.Worksheet("Accumulated");

            // Row 1 carries the soft verification notice; headers move to row 2.
            sheet.Cell(1, 1).GetString().Should().Contain("verify");
            sheet.Cell(2, 1).GetString().Should().Be("Name");
            sheet.Cell(2, 2).GetString().Should().Be("Account No");

            // First summary row (row 3) = the single ACME group with debit 150.00.
            sheet.Cell(3, 1).GetString().Should().Be("ACME LTD");
            sheet.Cell(3, 2).GetString().Should().Be("XXXXXX1234");
            sheet.Cell(3, 5).GetDouble().Should().Be(150.00);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_WithAccountInfo_ShouldRenderMetadataBlockOnEverySheet()
    {
        var t1 = new StatementTransaction(Columns);
        t1.SetColumn("Transaction Date", "01/01/2025");
        t1.SetColumn("Description", "ACME LTD XXXXXX1234 REF 1");
        t1.SetColumn("Debit", "100.00");

        var types = ColumnClassifier.ClassifyAll(Columns);
        var accumulation = AccumulationEngine.Build(Columns, types, [t1]);

        var accountInfo = new StatementAccountInfo
        {
            AccountTitle = "MUHAMMAD AZIZ",
            AccountNumber = "XXXXXX1234",
            Iban = "GB29NWBK60161331926819",
            FromDate = new DateTime(2025, 1, 1),
            ToDate = new DateTime(2025, 1, 31),
        };

        string path = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid()}.xlsx");
        try
        {
            new ExcelStatementWriter().Write(path, Columns, [t1], types, accumulation, null, accountInfo);

            using var workbook = new XLWorkbook(path);

            foreach (string sheetName in new[] { "Transactions", "Accumulated" })
            {
                IXLWorksheet sheet = workbook.Worksheet(sheetName);
                string content = string.Join(
                    "\n",
                    sheet.CellsUsed().Select(c => c.GetString()));

                content.Should().Contain("Account Title");
                content.Should().Contain("MUHAMMAD AZIZ");
                content.Should().Contain("Account Number");
                content.Should().Contain("XXXXXX1234");
                content.Should().Contain("IBAN");
                content.Should().Contain("GB29NWBK60161331926819");
                content.Should().Contain("Statement Period");
                content.Should().Contain("01/01/2025 to 31/01/2025");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_WithPassword_ShouldProduceEncryptedCompoundFile()
    {
        var t1 = new StatementTransaction(Columns);
        t1.SetColumn("Transaction Date", "01/01/2025");
        t1.SetColumn("Description", "Opening balance");
        t1.SetColumn("Available Balance", "1,000.00");

        string path = Path.Combine(Path.GetTempPath(), $"enc_{Guid.NewGuid()}.xlsx");
        try
        {
            new ExcelStatementWriter().Write(path, Columns, [t1], null, null, "confidential_123");

            File.Exists(path).Should().BeTrue();

            // Encrypted OOXML is stored in an OLE compound file, which starts with the CFB magic
            // signature D0 CF 11 E0 A1 B1 1A E1 - not the "PK" ZIP header of a plain .xlsx.
            byte[] header = new byte[8];
            using (FileStream fs = File.OpenRead(path))
            {
                fs.ReadExactly(header);
            }

            header.Should().Equal(0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1);

            // The two mandatory streams for agile-encrypted Office documents must be present.
            using (OpenMcdf.RootStorage root = OpenMcdf.RootStorage.OpenRead(path))
            {
                using OpenMcdf.CfbStream info = root.OpenStream("EncryptionInfo");
                using OpenMcdf.CfbStream package = root.OpenStream("EncryptedPackage");
                info.Length.Should().BeGreaterThan(0);
                package.Length.Should().BeGreaterThan(0);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
