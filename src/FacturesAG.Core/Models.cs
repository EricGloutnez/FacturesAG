namespace FacturesAG.Core;

public sealed record Rate(long Id, string Category, string EffectiveFrom, decimal HourlyRate, decimal MinHours);

/// <summary>Une ligne de facture telle qu'affichée : montant calculé (ou forcé) et description à jour.</summary>
public sealed record LineView(
    long Id, string Category, string? Entity, string Date, string Start, string End, bool Quorum,
    decimal? Override, decimal? Computed, decimal? Amount, string Description)
{
    public bool IsMissingEnd => Amount is null;

    /// <summary>Ligne libre (description et montant saisis à la main), par opposition à une assemblée calculée.</summary>
    public bool IsFree => Entity is null;

    public string Detail => IsFree ? "" : !Quorum ? "Quorum non atteint · minimum facturé"
        : Billing.DurationMinutes(Start, End) is int m
            ? Billing.FormatDuration(m) + (m > Billing.UnusualMinutes ? " · durée inhabituelle : vérifiez l'heure de fin" : "")
            : "Heure de fin à saisir";

    public bool IsUnusual => !IsFree && Quorum && Billing.DurationMinutes(Start, End) is int m && m > Billing.UnusualMinutes;
}

/// <summary>
/// Facture en cours de saisie : la facture du mois (brouillon) ou une facture finalisée rouverte pour modification.
/// </summary>
public sealed record Draft(long Id, IReadOnlyList<LineView> Lines, decimal Total, int Incomplete,
    int? Number = null, string? InvoiceDate = null, int? PaymentDays = null, bool IsReopened = false,
    bool WasPaid = false, decimal OriginalTotal = 0m);

public enum InvoiceStatus { Draft, Final, Paid }

public sealed record InvoiceSummary(
    long Id, int Number, InvoiceStatus Status, string InvoiceDate, string? PaidAt, string? PaymentMethod, decimal Total, string Source,
    int? PaymentDays = null, bool Editing = false)
{
    /// <summary>Date d'échéance (aaaa-mm-jj), ou null si le délai est inconnu (factures importées).</summary>
    public string? DueDate => PaymentDays is int d && DateOnly.TryParse(InvoiceDate, System.Globalization.CultureInfo.InvariantCulture, out var date)
        ? date.AddDays(d).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null;

    public bool IsOverdue(DateOnly today) =>
        Status == InvoiceStatus.Final && !Editing && DueDate is string due && string.CompareOrdinal(due, today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)) < 0;
}

/// <summary>Résultat de l'enregistrement d'une facture rouverte.</summary>
public sealed record ReopenSaveResult(long Id, int Number, bool PaymentReset);

public sealed record InvoiceDetail(InvoiceSummary Invoice, IReadOnlyList<LineView> Lines);

public sealed record CategoryTotal(string Category, decimal Total, int Lines, decimal Share);

public sealed record Report(int Year, IReadOnlyList<CategoryTotal> Categories, decimal Total, decimal Unpaid, IReadOnlyList<int> Years);

public sealed record AgConnectItem(string Entity, string Date, string Start, string Address, bool Virtual, string Status);

public sealed record PasteResult(IReadOnlyList<AgConnectItem> Added, IReadOnlyList<(AgConnectItem Item, string Reason)> Skipped);

public sealed record ImportResult(int Imported, int Skipped);

/// <summary>Évolution de la structure de la base : SQL appliqué une seule fois pour atteindre <paramref name="Version"/>.</summary>
public sealed record Migration(int Version, string Sql);

/// <summary>Erreur destinée à être montrée telle quelle à l'utilisateur.</summary>
public sealed class UserException(string message) : Exception(message);
