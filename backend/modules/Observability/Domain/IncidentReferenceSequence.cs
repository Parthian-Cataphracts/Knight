namespace Observability.Domain;

/// <summary>
/// The per-year counter behind incident references.
///
/// The next reference cannot be derived from counting existing incidents: two
/// rules opening one at the same instant would both read the same count and
/// produce <c>INC-2026-0042</c> twice. During an outage — which is precisely when
/// several rules fire at once — that is the moment two different problems become
/// impossible to tell apart in the chat window where people are discussing them.
///
/// So it is a row that is incremented atomically, and the repository holds the
/// only path to it.
/// </summary>
public sealed class IncidentReferenceSequence
{
    public int Year { get; private set; }

    public int LastValue { get; private set; }

    private IncidentReferenceSequence()
    {
    }

    public IncidentReferenceSequence(int year, int lastValue = 0)
    {
        Year = year;
        LastValue = lastValue;
    }
}
