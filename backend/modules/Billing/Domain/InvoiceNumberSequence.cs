namespace Billing.Domain;

/// <summary>
/// The per-year counter behind invoice numbers.
///
/// Accounting expects invoice numbers to be sequential and without gaps, so the
/// next one cannot be derived from counting existing rows: two callers issuing at
/// once would both read the same count and produce the same number. The counter
/// is a row that is incremented atomically, and the repository holds the only
/// path to it.
/// </summary>
public sealed class InvoiceNumberSequence
{
    public int Year { get; private set; }

    public int LastNumber { get; private set; }

    private InvoiceNumberSequence()
    {
    }

    public InvoiceNumberSequence(int year, int lastNumber = 0)
    {
        Year = year;
        LastNumber = lastNumber;
    }

    /// <summary>Formats a reserved value, e.g. <c>2026-000042</c>.</summary>
    public static string Format(int year, int number) => $"{year}-{number:D6}";
}
