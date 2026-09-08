using FluentAssertions;
using Taxation.StatementParser.Console.Models;
using Taxation.StatementParser.Console.Parsing;
using Xunit;

namespace Taxation.StatementParser.Console.Tests;

public sealed class AccumulationEngineTests
{
    private static readonly string[] Columns = ["Booking Date", "Description", "Credit", "Debit", "Available Balance"];
    private static readonly IReadOnlyList<ColumnType> Types = ColumnClassifier.ClassifyAll(Columns);

    [Theory]
    // Masked account numbers (letters/mask + digits) are recognised.
    [InlineData("PAYMENT TO ACME LTD XXXXXX1234 REF 99", "XXXXXX1234")]
    [InlineData("TFR PK36ABCD0001234567 SALARY", "PK36ABCD0001234567")]
    [InlineData("CARD PURCHASE ACME 4321ABC09", "4321ABC09")]
    // Dates, timestamps and pure-digit references are NOT account numbers.
    [InlineData("ACME STORES 01/02/2025", "")]
    [InlineData("AMAZON UK 8843 REF", "")]
    [InlineData("Direct debit utility", "")]
    [InlineData("TRANSFER 2025-03-15 12:30:00", "")]
    // Real snapshot patterns: long pure-digit account number is recognised; the short STAN is not.
    [InlineData("KUICKPAY 0013034377 From IB STAN(652121)", "0013034377")]
    [InlineData("KUICKPAY 0013019997 From IB STAN(409694)", "0013019997")]
    // Masked "PYxxxx0000" plus a long IBAN-like reference: the longest qualifying token wins.
    [InlineData("Raast P2P Fund transfer from ABDUL MUNTAQIM RAAST PYxxxx0000 SCBLPKKA2025083115643953438188", "SCBLPKKA2025083115643953438188")]
    public void ExtractAccountNumber_ShouldReturnExpected(string description, string expected)
    {
        AccumulationEngine.ExtractAccountNumber(description).Should().Be(expected);
    }

    [Theory]
    [InlineData("XXXXXX1234", true)]
    [InlineData("PK36ABCD0001234567", true)]
    [InlineData("4321ABC09", true)]
    [InlineData("12345678", false)]   // pure digits -> reference, not account
    [InlineData("ABCDEF", false)]     // no digits
    [InlineData("A1B2", false)]       // too short
    public void IsMaskedAccountNumber_ShouldValidateFormat(string token, bool expected)
    {
        AccumulationEngine.IsMaskedAccountNumber(token).Should().Be(expected);
    }

    [Theory]
    [InlineData("XXXXXX1234", true)]              // masked
    [InlineData("PYxxxx0000", true)]              // masked
    [InlineData("0013034377", true)]              // long pure-digit account
    [InlineData("SCBLPKKA2025083115643953438188", true)] // IBAN-like
    [InlineData("652121", false)]                 // short STAN reference
    [InlineData("8843", false)]                   // short numeric reference
    [InlineData("ABCDEF", false)]                 // no digits
    public void IsAccountNumber_ShouldValidateFormat(string token, bool expected)
    {
        AccumulationEngine.IsAccountNumber(token).Should().Be(expected);
    }

    [Fact]
    public void Build_ShouldNotMergeDistinctKuickpayAccounts_AndKeepCleanName()
    {
        IReadOnlyList<StatementTransaction> transactions =
        [
            Make("31/08/2025", "KUICKPAY 0013034377 From IB STAN(652121)", credit: null, debit: "1,000.00"),
            Make("31/08/2025", "KUICKPAY 0013019997 From IB STAN(409694)", credit: null, debit: "2,000.00"),
        ];

        AccumulationResult result = AccumulationEngine.Build(Columns, Types, transactions);

        // Distinct account numbers must NOT merge, even though the payee text is identical.
        result.Groups.Should().HaveCount(2);
        result.Groups.Select(g => g.AccountNumber).Should()
            .BeEquivalentTo(["0013034377", "0013019997"]);
        // The embedded account number and STAN reference are stripped from the name.
        result.Groups.Should().OnlyContain(g => g.Name == "KUICKPAY From IB");
    }

    [Theory]
    [InlineData("ACME LTD XXXXXX1234", "XXXXXX1234", "ACME LTD")]
    [InlineData("ACME STORES LTD 01/02/2025", "", "ACME STORES LTD")]
    [InlineData("   ", "", "(Unknown)")]
    // Real statement descriptions: the STAN reference must be stripped so the payee is stable.
    [InlineData("Money Transferred to HAMZA NAVEED JazzCash XXXX6645361 STAN(813409)", "XXXX6645361", "Money Transferred to HAMZA NAVEED JazzCash")]
    [InlineData("Money Transferred to HAMZA NAVEED JazzCash XXXX6645361 STAN(454522)", "XXXX6645361", "Money Transferred to HAMZA NAVEED JazzCash")]
    [InlineData("Charges Taxes Plus FED STAN(813409)", "", "Charges Taxes Plus FED")]
    public void NormalizeName_ShouldStripAccountAndDates(string description, string account, string expected)
    {
        AccumulationEngine.NormalizeName(description, account).Should().Be(expected);
    }

    [Fact]
    public void Build_ShouldAccumulate_WhenNameAndMaskedAccountMatch()
    {
        IReadOnlyList<StatementTransaction> transactions =
        [
            Make("01/01/2025", "ACME LTD XXXXXX1234 REF 1", credit: null, debit: "100.00"),
            Make("05/01/2025", "ACME LTD XXXXXX1234 REF 2", credit: null, debit: "50.00"),
            Make("03/01/2025", "JANE DOE YYYYYY9999", credit: "2,000.00", debit: null)
        ];

        AccumulationResult result = AccumulationEngine.Build(Columns, Types, transactions);

        result.Groups.Should().HaveCount(2);

        TransactionGroup acme = result.Groups.Single(g => g.Name == "ACME LTD");
        acme.AccountNumber.Should().Be("XXXXXX1234");
        acme.TotalDebit.Should().Be(150.00m);
        acme.Count.Should().Be(2);
        acme.FirstDate.Should().Be(new DateTime(2025, 1, 1));
        acme.LastDate.Should().Be(new DateTime(2025, 1, 5));

        TransactionGroup jane = result.Groups.Single(g => g.AccountNumber == "YYYYYY9999");
        jane.TotalCredit.Should().Be(2000.00m);
        jane.Net.Should().Be(2000.00m);
    }

    [Fact]
    public void Build_ShouldNotAccumulate_WhenAccountNumbersDiffer()
    {
        IReadOnlyList<StatementTransaction> transactions =
        [
            Make("01/01/2025", "ACME LTD XXXXXX1111", credit: null, debit: "100.00"),
            Make("02/01/2025", "ACME LTD XXXXXX2222", credit: null, debit: "100.00")
        ];

        AccumulationResult result = AccumulationEngine.Build(Columns, Types, transactions);

        result.Groups.Should().HaveCount(2);
    }

    [Fact]
    public void Build_ShouldResolveDateDebitCreditRoles()
    {
        AccumulationResult result = AccumulationEngine.Build(Columns, Types, []);

        result.Roles.DescriptionColumn.Should().Be("Description");
        result.Roles.DateColumn.Should().Be("Booking Date");
        result.Roles.DebitColumn.Should().Be("Debit");
        result.Roles.CreditColumn.Should().Be("Credit");
    }

    [Fact]
    public void Build_ShouldAccumulate_RealDescriptions_IgnoringStanReferences()
    {
        // Two transfers to the same payee/account differing only by STAN reference must group as one.
        IReadOnlyList<StatementTransaction> transactions =
        [
            Make("29/08/2025", "Money Transferred to HAMZA NAVEED JazzCash XXXX6645361 STAN(813409)", credit: null, debit: "5,000.00"),
            Make("29/08/2025", "Money Transferred to HAMZA NAVEED JazzCash XXXX6645361 STAN(454522)", credit: null, debit: "2,500.00"),
        ];

        AccumulationResult result = AccumulationEngine.Build(Columns, Types, transactions);

        result.Groups.Should().HaveCount(1);
        TransactionGroup group = result.Groups.Single();
        group.Name.Should().Be("Money Transferred to HAMZA NAVEED JazzCash");
        group.AccountNumber.Should().Be("XXXX6645361");
        group.TotalDebit.Should().Be(7500.00m);
        group.Count.Should().Be(2);
    }

    private static StatementTransaction Make(string date, string description, string? credit, string? debit)
    {
        var t = new StatementTransaction(Columns);
        t.SetColumn("Booking Date", date);
        t.SetColumn("Description", description);
        if (credit is not null)
        {
            t.SetColumn("Credit", credit);
        }

        if (debit is not null)
        {
            t.SetColumn("Debit", debit);
        }

        return t;
    }
}
