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

    public static byte[] Generate(InvoiceDetail inv, IReadOnlyDictionary<string, string> settings) =>
        Create(inv, settings).GeneratePdf();

    /// <summary>Images PNG des pages (aperçu à l'écran, vérifications).</summary>
    public static IEnumerable<byte[]> GenerateImages(InvoiceDetail inv, IReadOnlyDictionary<string, string> settings, int dpi = 110) =>
        Create(inv, settings).GenerateImages(new ImageGenerationSettings { RasterDpi = dpi });

    private const string AccentSoft = "#E3F1EC";

    /// <summary>« 3 nov. » à partir de « 2026-11-03 ».</summary>
    public static string ShortDate(string? iso) =>
        DateOnly.TryParseExact(iso ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("d MMM", Fr) : "";

    /// <summary>« 30 novembre 2026 · échéance 15 décembre 2026 » (sous le numéro de facture).</summary>
    public static string HeaderDates(InvoiceSummary i)
    {
        if (i.Status == InvoiceStatus.Draft) return "Le numéro et la date seront attribués à la finalisation";
        var date = LongDate(i.InvoiceDate);
        return i.PaymentDays switch
        {
            null => date,
            0 => $"{date} · payable sur réception",
            _ => $"{date} · échéance {LongDate(i.DueDate)}",
        };
    }

    /// <summary>Lignes regroupées par catégorie, dans l'ordre où chaque catégorie apparaît, puis par date.</summary>
    public static IEnumerable<IGrouping<string, LineView>> GroupByCategory(IEnumerable<LineView> lines) =>
        lines.Select((l, index) => (l, index))
             .GroupBy(x => x.l.Category)
             .OrderBy(g => g.Min(x => x.index))
             .Select(g => g.OrderBy(x => x.l.Date, StringComparer.Ordinal).ThenBy(x => x.index).Select(x => x.l)
                           .GroupBy(_ => g.Key).Single());

    private static IDocument Create(InvoiceDetail inv, IReadOnlyDictionary<string, string> settings)
    {
        var i = inv.Invoice;
        var paid = i.Status == InvoiceStatus.Paid ? i.Total : 0m;
        var draft = i.Status == InvoiceStatus.Draft;

        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(40);
            page.DefaultTextStyle(t => t.FontSize(10).FontColor("#1D1D1B"));

            // En-tête (modèle D « détaillée en colonnes ») : logo à gauche, numéro et dates à droite.
            page.Header().BorderBottom(2).BorderColor(Accent).PaddingBottom(10).Row(row =>
            {
                row.RelativeItem().AlignMiddle().Row(logo =>
                {
                    logo.ConstantItem(40).Height(40).Svg(Brand.IconSvg);
                    logo.AutoItem().PaddingLeft(8).AlignMiddle().Column(t =>
                    {
                        t.Item().Text(Brand.Name).FontSize(14).Bold().FontColor("#1D1D1B");
                        t.Item().Text(Brand.Tagline).FontSize(7).FontColor(Muted).LetterSpacing(0.12f);
                    });
                });
                row.AutoItem().AlignMiddle().Column(c =>
                {
                    c.Item().AlignRight().Text(draft ? "FACTURE — BROUILLON" : $"FACTURE N° {i.Number}").FontSize(17).Bold().FontColor(Accent);
                    c.Item().AlignRight().Text(HeaderDates(i)).FontSize(9).FontColor(Muted);
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
                    Party("De", settings["issuer_name"], settings["issuer_street"], settings["issuer_city"], settings["issuer_phone"], settings["issuer_email"]);
                    Party("À", settings["client_name"], settings["client_street"], settings["client_city"], settings["client_email"]);
                });

                // Lignes regroupées par catégorie, avec date, heures et durée en colonnes, puis un sous-total par catégorie.
                col.Item().PaddingTop(20).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(52);   // Date
                        c.RelativeColumn();     // Assemblée / description
                        c.ConstantColumn(80);   // Heures
                        c.ConstantColumn(50);   // Durée
                        c.ConstantColumn(78);   // Montant
                    });
                    table.Header(h =>
                    {
                        static IContainer Head(IContainer x) => x.BorderBottom(1).BorderColor("#C9C8C1").PaddingVertical(5).PaddingHorizontal(5);
                        h.Cell().Element(Head).Text("DATE").FontSize(8).FontColor(Muted);
                        h.Cell().Element(Head).Text("ASSEMBLÉE / DESCRIPTION").FontSize(8).FontColor(Muted);
                        h.Cell().Element(Head).Text("HEURES").FontSize(8).FontColor(Muted);
                        h.Cell().Element(Head).AlignRight().Text("DURÉE").FontSize(8).FontColor(Muted);
                        h.Cell().Element(Head).AlignRight().Text("MONTANT").FontSize(8).FontColor(Muted);
                    });

                    static IContainer Cell(IContainer x) => x.BorderBottom(0.5f).BorderColor("#ECEBE6").PaddingVertical(4).PaddingHorizontal(5);
                    foreach (var group in GroupByCategory(inv.Lines))
                    {
                        table.Cell().ColumnSpan(5).Background(AccentSoft).PaddingVertical(4).PaddingHorizontal(5)
                            .Text(group.Key).SemiBold().FontColor(Accent);
                        foreach (var l in group)
                        {
                            table.Cell().Element(Cell).Text(ShortDate(l.Date));
                            if (l.IsFree)
                            {
                                table.Cell().ColumnSpan(3).Element(Cell).Text(l.Description);
                            }
                            else
                            {
                                table.Cell().Element(Cell).Text(l.Entity);
                                if (!l.Quorum)
                                    table.Cell().ColumnSpan(2).Element(Cell).Text("quorum non atteint").FontColor(Muted);
                                else
                                {
                                    table.Cell().Element(Cell).Text($"{Billing.FormatHour(l.Start)}–{Billing.FormatHour(l.End)}");
                                    table.Cell().Element(Cell).AlignRight().Text(Billing.DurationMinutes(l.Start, l.End) is int m ? Billing.FormatDuration(m) : "");
                                }
                            }
                            table.Cell().Element(Cell).AlignRight().Text(l.Amount is decimal a ? Money(a) : "—");
                        }
                        table.Cell().ColumnSpan(4).PaddingVertical(4).PaddingHorizontal(5).AlignRight()
                            .Text($"Sous-total · {group.Key} ({group.Count()})").FontColor(Muted);
                        table.Cell().PaddingVertical(4).PaddingHorizontal(5).AlignRight()
                            .Text(Money(group.Sum(l => l.Amount ?? 0m))).SemiBold();
                    }
                });

                // Totaux à droite (sans mention des taxes ni texte de conditions : l'échéance est dans l'en-tête).
                col.Item().PaddingTop(20).ShowEntire().Row(row =>
                {
                    row.RelativeItem();
                    row.ConstantItem(230).Column(c =>
                    {
                        void Line(string label, string value, bool muted = false) => c.Item().PaddingVertical(2).Row(r =>
                        {
                            var left = r.RelativeItem().Text(label); var right = r.AutoItem().Text(value);
                            if (muted) { left.FontColor(Muted); right.FontColor(Muted); }
                        });
                        Line("Sous-total", Money(i.Total));
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
        }));
    }
}
