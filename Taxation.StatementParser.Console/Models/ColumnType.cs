namespace Taxation.StatementParser.Console.Models;

/// <summary>
/// The semantic data type of a statement column, inferred from its heading.
/// Drives how the value is parsed and formatted in the generated Excel file.
/// </summary>
public enum ColumnType
{
    /// <summary>Free text (e.g. Transaction / Description). Preserved verbatim.</summary>
    Text,

    /// <summary>A calendar date (e.g. Booking Date / Transaction Date).</summary>
    Date,

    /// <summary>A monetary amount (e.g. Debit, Credit, Available Balance).</summary>
    Amount
}
