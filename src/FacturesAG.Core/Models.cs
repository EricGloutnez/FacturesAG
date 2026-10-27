namespace FacturesAG.Core;

public sealed record Rate(long Id, string Category, string EffectiveFrom, decimal HourlyRate, decimal MinHours);

/// <summary>Une ligne de facture telle qu'affichée : montant calculé (ou forcé) et description à jour.</summary>
public sealed record LineView(
    long Id, string Category, string? Entity, string Date, string Start, string End, bool Quorum,
    decimal? Override, decimal? Computed, decimal? Amount, string Description)
{
    public bool IsMissingEnd => Amount is null;

    public string Detail => !Quorum ? "Quorum non atteint · minimum facturé"
        : Billing.DurationMinutes(Start, End) is int m ? Billing.FormatDuration(m) : "Heure de fin à saisir";
}

public sealed record Draft(long Id, IReadOnlyList<LineView> Lines, decimal Total, int Incomplete);

public enum InvoiceStatus { Draft, Final, Paid }

public sealed record InvoiceSummary(
    long Id, int Number, InvoiceStatus Status, string InvoiceDate, string? PaidAt, string? PaymentMethod, decimal Total, string Source);

public sealed record InvoiceDetail(InvoiceSummary Invoice, IReadOnlyList<LineView> Lines);

public sealed record CategoryTotal(string Category, decimal Total, int Lines, decimal Share);

public sealed record Report(int Year, IReadOnlyList<CategoryTotal> Categories, decimal Total, decimal Unpaid, IReadOnlyList<int> Years);

public sealed record AgConnectItem(string Entity, string Date, string Start, string Address, bool Virtual, string Status);

public sealed record PasteResult(IReadOnlyList<AgConnectItem> Added, IReadOnlyList<(AgConnectItem Item, string Reason)> Skipped);

public sealed record ImportResult(int Imported, int Skipped);

/// <summary>Erreur destinée à être montrée telle quelle à l'utilisateur.</summary>
public sealed class UserException(string message) : Exception(message);
