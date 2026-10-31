using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using FacturesAG.Core;

namespace FacturesAG;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Fmt
{
    public static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-CA");
    public static string Money(decimal? n) => n is decimal d ? InvoicePdf.Money(d) : "—";
    public static string Iso(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateTime? FromIso(string? s) =>
        DateTime.TryParseExact(s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    public static string MonthTitle(DateTime d) { var t = d.ToString("MMMM yyyy", Fr); return char.ToUpper(t[0], Fr) + t[1..]; }
}

/// <summary>
/// Ligne modifiable de la facture en cours. Chaque modification est enregistrée aussitôt (via <see cref="Save"/>),
/// puis les champs calculés sont mis à jour sur place pour ne pas perdre le focus pendant la saisie.
/// </summary>
public sealed class LineRow : Observable
{
    private string _entity = "", _start = "", _end = "", _override = "", _category = "", _description = "", _amount = "";
    private DateTime? _date;
    private bool _quorum = true;
    private LineView _view;

    public LineRow(LineView view, Action<LineRow, string> save) { _view = view; Save = save; Apply(view); }

    public Action<LineRow, string> Save { get; }
    public long Id => _view.Id;
    public LineView View => _view;

    public string Entity { get => _entity; set { if (value == _entity) return; _entity = value; Save(this, nameof(Entity)); } }
    public DateTime? Date
    {
        get => _date;
        set
        {
            if (value == _date) return;
            // Une date effacée n'est pas enregistrée : on réaffiche aussitôt celle qui est dans la base.
            if (value is null) { System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Apply(_view)); return; }
            _date = value;
            Save(this, nameof(Date));
        }
    }
    public string Start { get => _start; set { if (value == _start) return; _start = value; Save(this, nameof(Start)); } }
    public string End { get => _end; set { if (value == _end) return; _end = value; Save(this, nameof(End)); } }
    public bool Quorum { get => _quorum; set { if (value == _quorum) return; _quorum = value; Save(this, nameof(Quorum)); } }
    public string OverrideText { get => _override; set { if (value == _override) return; _override = value; Save(this, nameof(OverrideText)); } }

    // Ligne libre (description et montant saisis à la main)
    public bool IsFree => _view.IsFree;
    public bool IsAssembly => !_view.IsFree;
    public string Category { get => _category; set { if (value == _category) return; _category = value; Save(this, nameof(Category)); } }
    public string Description { get => _description; set { if (value == _description) return; _description = value; Save(this, nameof(Description)); } }
    public string AmountText { get => _amount; set { if (value == _amount) return; _amount = value; Save(this, nameof(AmountText)); } }

    public string Placeholder => _view.Computed is decimal c ? c.ToString("0.00", CultureInfo.InvariantCulture) : "—";
    public bool ShowPlaceholder => _override.Length == 0;
    public string Detail => _view.Detail;
    public bool IsMissing => _view.Amount is null;
    public bool IsForced => _view.Override is not null;
    public string ForcedNote => _view.Override is not null ? $"forcé (calcul : {Fmt.Money(_view.Computed)})" : "";

    /// <summary>Recharge la ligne depuis la base sans déclencher d'enregistrement.</summary>
    public void Apply(LineView v)
    {
        _view = v;
        _entity = v.Entity ?? ""; _date = Fmt.FromIso(v.Date); _start = v.Start; _end = v.End; _quorum = v.Quorum;
        _override = v.Override is decimal o ? o.ToString("0.00", CultureInfo.InvariantCulture) : "";
        _category = v.Category; _description = v.Description;
        _amount = v.IsFree && v.Amount is decimal a ? a.ToString("0.00", CultureInfo.InvariantCulture) : "";
        foreach (var p in new[] { nameof(Entity), nameof(Date), nameof(Start), nameof(End), nameof(Quorum), nameof(OverrideText),
                                  nameof(Placeholder), nameof(ShowPlaceholder), nameof(Detail), nameof(IsMissing), nameof(IsForced), nameof(ForcedNote),
                                  nameof(IsFree), nameof(IsAssembly), nameof(Category), nameof(Description), nameof(AmountText) })
            Raise(p);
    }
}

public sealed class PasteRow : Observable
{
    public PasteRow(AgConnectItem item, bool known)
    {
        Item = item;
        Reason = known ? "déjà ajoutée" : !item.Virtual ? "avec adresse (hybride) : pas encore géré" : "";
        Selected = Reason.Length == 0;
    }

    public AgConnectItem Item { get; }
    public string Reason { get; }
    public bool CanAdd => Reason.Length == 0;
    public bool Selected { get; set; }
    public string Title => Item.Entity;
    public string When => $"{InvoicePdf.LongDate(Item.Date)} à {Item.Start}";
}

public sealed class InvoiceRow(InvoiceSummary summary, bool hasPdf)
{
    public InvoiceSummary Summary => summary;
    public string Number => summary.Number.ToString(CultureInfo.InvariantCulture);
    public string Source => summary.Source == "finance-d" ? "Finance D" : "";
    public string Date => InvoicePdf.LongDate(summary.InvoiceDate);
    public string Total => Fmt.Money(summary.Total);
    public bool IsPaid => summary.Status == InvoiceStatus.Paid;
    public bool IsEditing => summary.Editing;
    public bool IsOverdue => summary.IsOverdue(DateOnly.FromDateTime(DateTime.Today));
    public string Status => IsEditing ? "En modification" : IsPaid ? "Payée" : IsOverdue ? "En retard" : "À payer";
    /// <summary>Couleur de l'étiquette : ok (payée), danger (en retard), warn (autres).</summary>
    public string Tone => IsEditing ? "info" : IsPaid ? "ok" : IsOverdue ? "danger" : "warn";
    public string PaidInfo => IsPaid ? $"{InvoicePdf.LongDate(summary.PaidAt)}{(string.IsNullOrEmpty(summary.PaymentMethod) ? "" : " · " + summary.PaymentMethod)}" : "";
    public string Due => summary.PaymentDays switch
    {
        null => "—",
        0 => "Sur réception",
        _ => InvoicePdf.LongDate(summary.DueDate),
    };
    public string PdfLabel => hasPdf ? "PDF" : "Créer le PDF";
    public string PaidLabel => IsPaid ? "Annuler le paiement" : "Marquer payée";
    public string ReopenLabel => IsEditing ? "Continuer la modification" : "Rouvrir";
    public bool CanSend => !IsEditing;
    public string SendLabel => summary.SentAt is null ? "Envoyer" : "Renvoyer";
    /// <summary>« envoyée le 31 oct. » (date du dernier envoi par courriel), vide sinon.</summary>
    public string SentInfo => summary.SentAt is string at && at.Length >= 10 ? $"envoyée le {InvoicePdf.ShortDate(at[..10])}" : "";
}

public sealed class RateRow(Rate rate)
{
    public Rate Rate => rate;
    public string From => InvoicePdf.LongDate(rate.EffectiveFrom);
    public string Hourly => $"{Fmt.Money(rate.HourlyRate)}/h";
    public string Min => $"{rate.MinHours.ToString("0.#", CultureInfo.InvariantCulture)} h";
}

public sealed record ReportRow(string Category, string Share, string Total, bool IsTotal = false);
