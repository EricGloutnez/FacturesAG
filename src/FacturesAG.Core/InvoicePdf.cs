using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FacturesAG.Core;

/// <summary>Facture au format PDF (QuestPDF, licence Community).</summary>
public static class InvoicePdf
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-CA");
    private const string Accent = "#1F6F5C";
    private const string Muted = "#6B6B66";

    static InvoicePdf() => QuestPDF.Settings.License = LicenseType.Community;

    /// <summary>« 2 668.14 $ » : séparateur de milliers espace, point décimal, comme les factures existantes.</summary>
    public static string Money(decimal n)
    {
        var s = Math.Abs(n).ToString("#,##0.00", CultureInfo.InvariantCulture).Replace(",", " ");
        return $"{(n < 0 ? "-" : "")}{s} $";
    }

    public static string LongDate(string? iso) =>
        DateOnly.TryParseExact(iso ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("d MMMM yyyy", Fr) : "";

    public static string FileName(InvoiceDetail inv, IReadOnlyDictionary<string, string> settings)
    {
        var client = string.Concat(settings["client_name"].Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-'));
        return client.Length == 0 ? $"Facture-{inv.Invoice.Number}.pdf" : $"Facture-{inv.Invoice.Number}_{client}.pdf";
    }

    /// <summary>Phrase du délai de paiement, ex. « Net 15 jours : payable au plus tard le 14 décembre 2026. »</summary>
    public static string? PaymentTermsSentence(InvoiceSummary i)
    {
        if (i.PaymentDays is not int d) return null;
        if (d == 0) return "Payable sur réception.";
        return i.DueDate is string due && i.Status != InvoiceStatus.Draft
            ? $"Net {d} jours : payable au plus tard le {LongDate(due)}."
            : $"Net {d} jours suivant la date de facture.";
    }

    /// <param name="logo">Image PNG ou JPEG affichée en haut de la facture ; null pour aucun logo.</param>
    public static byte[] Generate(InvoiceDetail inv, IReadOnlyDictionary<string, string> settings, byte[]? logo = null)
    {
        var i = inv.Invoice;
        var paid = i.Status == InvoiceStatus.Paid ? i.Total : 0m;
        var draft = i.Status == InvoiceStatus.Draft;
        var terms = string.Join(" ", new[] { PaymentTermsSentence(i), settings.GetValueOrDefault("payment_terms") }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(40);
            page.DefaultTextStyle(t => t.FontSize(10).FontColor("#1D1D1B"));

            page.Header().BorderBottom(3).BorderColor(Accent).PaddingBottom(12).Row(row =>
            {
                row.RelativeItem().AlignBottom().Column(c =>
                {
                    if (logo is { Length: > 0 }) c.Item().PaddingBottom(8).MaxHeight(70).MaxWidth(220).Image(logo).FitArea();
                    c.Item().Text("FACTURE").FontSize(24).SemiBold().FontColor(Accent);
                });
                row.ConstantItem(240).AlignBottom().Column(c =>
                {
                    void Meta(string label, string value) => c.Item().Row(r =>
                    {
                        r.RelativeItem().AlignRight().Text(label).SemiBold();
                        r.ConstantItem(130).AlignRight().Text(value);
                    });
                    Meta("Numéro :", draft ? "brouillon" : i.Number.ToString(CultureInfo.InvariantCulture));
                    Meta("Date :", draft ? "—" : LongDate(i.InvoiceDate));
                    if (!draft && i.PaymentDays is not null) Meta("Échéance :", i.PaymentDays == 0 ? "sur réception" : LongDate(i.DueDate));
                    Meta("Total :", Money(i.Total));
                });
            });

            page.Content().PaddingTop(18).Column(col =>
            {
                col.Item().Row(row =>
                {
                    void Party(string label, params string[] lines) => row.RelativeItem().Column(c =>
                    {
                        c.Item().Text(label.ToUpperInvariant()).FontSize(8).FontColor(Muted).LetterSpacing(0.08f);
                        c.Item().PaddingTop(3).Text(lines[0]).FontSize(11.5f).SemiBold();
                        foreach (var l in lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l))) c.Item().Text(l);
                    });
                    Party("Facturé par", settings["issuer_name"], settings["issuer_street"], settings["issuer_city"], settings["issuer_phone"], settings["issuer_email"]);
                    Party("Facturé à", settings["client_name"], settings["client_street"], settings["client_city"], settings["client_email"]);
                });

                col.Item().PaddingTop(22).Table(table =>
                {
                    table.ColumnsDefinition(c => { c.ConstantColumn(80); c.ConstantColumn(130); c.RelativeColumn(); });
                    table.Header(h =>
                    {
                        static IContainer Head(IContainer x) => x.BorderBottom(1).BorderColor("#C9C8C1").PaddingVertical(5).PaddingHorizontal(6);
                        h.Cell().Element(Head).AlignRight().Text("MONTANT").FontSize(8).FontColor(Muted);
                        h.Cell().Element(Head).Text("CATÉGORIE").FontSize(8).FontColor(Muted);
                        h.Cell().Element(Head).Text("DESCRIPTION").FontSize(8).FontColor(Muted);
                    });
                    var n = 0;
                    foreach (var l in inv.Lines)
                    {
                        var bg = n++ % 2 == 1 ? "#F7F7F4" : "#FFFFFF";
                        IContainer Cell(IContainer x) => x.Background(bg).BorderBottom(0.5f).BorderColor("#ECEBE6").PaddingVertical(5).PaddingHorizontal(6);
                        table.Cell().Element(Cell).AlignRight().Text(l.Amount is decimal a ? Money(a) : "—");
                        table.Cell().Element(Cell).Text(l.Category);
                        table.Cell().Element(Cell).Text(l.Description);
                    }
                });

                col.Item().PaddingTop(20).ShowEntire().Row(row =>
                {
                    row.RelativeItem().PaddingRight(30).Text(terms).FontSize(9).FontColor("#4A4A46");
                    row.ConstantItem(230).Column(c =>
                    {
                        void Line(string label, string value, bool muted = false) => c.Item().PaddingVertical(2).Row(r =>
                        {
                            var left = r.RelativeItem().Text(label); var right = r.AutoItem().Text(value);
                            if (muted) { left.FontColor(Muted); right.FontColor(Muted); }
                        });
                        Line("Sous-total", Money(i.Total));
                        Line("TPS", "non applicable", true);
                        Line("TVQ", "non applicable", true);
                        c.Item().PaddingTop(4).BorderTop(2).PaddingTop(6).Row(r =>
                        {
                            r.RelativeItem().Text("Total facturé").FontSize(12).SemiBold();
                            r.AutoItem().Text(Money(i.Total)).FontSize(12).SemiBold();
                        });
                        Line("Paiements", Money(paid));
                        c.Item().PaddingVertical(2).Row(r =>
                        {
                            r.RelativeItem().Text("Solde dû").SemiBold().FontColor(Accent);
                            r.AutoItem().Text(Money(i.Total - paid)).SemiBold().FontColor(Accent);
                        });
                    });
                });
            });

            page.Footer().Row(r =>
            {
                r.RelativeItem().Text(settings["footer"]).FontSize(8).FontColor("#8A8A84");
                r.AutoItem().Text(i.Status == InvoiceStatus.Draft ? "Aperçu : le numéro sera attribué à la finalisation" : $"Facture n° {i.Number}").FontSize(8).FontColor("#8A8A84");
            });
        })).GeneratePdf();
    }
}
