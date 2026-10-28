// Tests de FacturesAG.Core, sans dépendance : dotnet run --project tests/FacturesAG.Tests
// Toutes les données (noms, adresses, taux) sont fictives.
using FacturesAG.Core;

var rates = new List<Rate>
{
    new(1, Store.CategorieVirtuelle, "2024-01-01", 25m, 2m),
    new(2, Store.CategorieVirtuelle, "2025-01-01", 30m, 2m),
};

// Base en mémoire avec la même grille et un numéro de départ à 120.
Store Fresh()
{
    var s = new Store(":memory:");
    foreach (var r in rates) s.AddRate(r.EffectiveFrom, r.HourlyRate, r.MinHours);
    s.UpdateSettings(new Dictionary<string, string> { ["next_number"] = "120", ["client_name"] = "Client Test" });
    return s;
}

var tests = new List<(string Name, Action Body)>
{
    ("durée simple et passage de minuit", () =>
    {
        Eq(124, Billing.DurationMinutes("18:30", "20:34"));
        Eq(415, Billing.DurationMinutes("18:30", "01:25"));
        Eq(0, Billing.DurationMinutes("18:30", "18:30"));
    }),
    ("heures facturées : toute heure commencée, minimum 2 h", () =>
    {
        Eq(2m, Billing.BilledHours(56, 2)); Eq(3m, Billing.BilledHours(124, 2));
        Eq(4m, Billing.BilledHours(240, 2)); Eq(5m, Billing.BilledHours(241, 2));
    }),
    ("taux en vigueur selon la date", () =>
    {
        Eq(25m, Billing.RateFor("2024-12-31", rates)!.HourlyRate);
        Eq(30m, Billing.RateFor("2025-01-01", rates)!.HourlyRate);
        Eq<Rate?>(null, Billing.RateFor("2023-12-31", rates));
    }),
    ("montants à 30 $/h selon la durée", () =>
    {
        foreach (var (s, e, expected) in new[] { ("18:30", "20:34", 90m), ("17:30", "19:40", 90m), ("18:30", "20:58", 90m),
                                                 ("18:30", "21:27", 90m), ("18:00", "18:56", 60m), ("18:30", "22:30", 120m) })
            Eq<decimal?>(expected, Billing.ComputeAmount("2026-07-15", s, e, true, rates), $"{s}-{e}");
    }),
    ("ancien taux (25 $/h) et passage de minuit (30 $/h)", () =>
    {
        Eq<decimal?>(125m, Billing.ComputeAmount("2024-06-20", "18:30", "22:32", true, rates));
        Eq<decimal?>(210m, Billing.ComputeAmount("2025-09-20", "18:30", "01:25", true, rates));
    }),
    ("pas de quorum = minimum ; heure de fin manquante = pas de montant", () =>
    {
        Eq<decimal?>(60m, Billing.ComputeAmount("2026-07-15", "18:30", "", false, rates));
        Eq<decimal?>(null, Billing.ComputeAmount("2026-07-15", "18:30", "", true, rates));
    }),
    ("saisie souple des heures", () =>
    {
        Eq("20:34", Billing.NormalizeTime("20:34")); Eq("20:34", Billing.NormalizeTime("20h34"));
        Eq("20:34", Billing.NormalizeTime("2034")); Eq("08:34", Billing.NormalizeTime("834"));
        Eq("01:25", Billing.NormalizeTime("1h25")); Eq("20:00", Billing.NormalizeTime("20h"));
        Eq("09:00", Billing.NormalizeTime("9")); Eq("", Billing.NormalizeTime(" "));
        Eq(null, Billing.NormalizeTime("25:00")); Eq(null, Billing.NormalizeTime("20:75")); Eq(null, Billing.NormalizeTime("abc"));
    }),
    ("description de ligne", () =>
    {
        Eq("9h00", Billing.FormatHour("09:00"));
        Eq("SDC Les Jardins (18h30 - 20h34)", Billing.DescribeLine("SDC Les Jardins", "18:30", "20:34", true));
        Eq("SDC Le Phare - Quorum non atteint", Billing.DescribeLine("SDC Le Phare", "18:30", "", false));
    }),
    ("ag-connect : entité, date, heure et mode", () =>
    {
        var r = AgConnectParser.Parse(Sample.Invitations);
        Eq(4, r.Count);
        Eq(new AgConnectItem("SDC Les Jardins", "2026-10-27", "18:30", "100 rue Principale, Laval", false, "Ouverture"), r[0]);
        Eq(new AgConnectItem("SDC Le Phare Tour Est", "2026-10-05", "18:30", "", true, "Clôturée"), r[1]);
        Eq("Association des copropriétaires du Domaine des Érables Phase 1", r[2].Entity);
        Eq("2026-08-01", r[3].Date); Eq("09:00", r[3].Start);
    }),
    ("ag-connect : texte vide, fins de ligne Windows", () =>
    {
        Eq(0, AgConnectParser.Parse("").Count);
        Eq(0, AgConnectParser.Parse("Tableau de bord\nRien ici").Count);
        var r = AgConnectParser.Parse(Sample.Invitations.Replace("\n", "  \r\n"));
        Eq(4, r.Count); Eq("SDC Le Phare Tour Est", r[1].Entity);
    }),
    ("base neuve : aucun taux, aucune coordonnée, numéro 1", () =>
    {
        using var s = new Store(":memory:");
        Eq(0, s.ListRates().Count);
        Eq("", s.GetSettings()["issuer_name"]);
        Eq("1", s.GetSettings()["next_number"]);
    }),
    ("facture en cours : montants et total", () =>
    {
        using var s = Fresh();
        var a = s.AddLine("SDC Les Jardins", "2026-11-03", "18:30", "20:34");
        s.AddLine("SDC Le Phare", "2026-11-04", "18:30", quorum: false);
        var d = s.GetDraft();
        Eq(2, d.Lines.Count);
        Eq<decimal?>(90m, d.Lines.Single(l => l.Id == a).Amount);
        Eq("SDC Les Jardins (18h30 - 20h34)", d.Lines.Single(l => l.Id == a).Description);
        Eq(150m, d.Total); Eq(0, d.Incomplete);
    }),
    ("heures saisies librement puis normalisées, heure invalide refusée", () =>
    {
        using var s = Fresh();
        var id = s.AddLine("SDC A", "2026-11-03", "18h30");
        s.UpdateLine(id, end: "2034");
        var l = s.GetDraft().Lines[0];
        Eq("18:30", l.Start); Eq("20:34", l.End); Eq<decimal?>(90m, l.Amount);
        Throws<UserException>(() => s.UpdateLine(id, end: "25:00"), "Heure de fin invalide");
    }),
    ("montant forcé à la main", () =>
    {
        using var s = Fresh();
        var id = s.AddLine("SDC Bord-du-Lac", "2026-11-03", "09:00", "12:35");
        Eq<decimal?>(120m, s.GetDraft().Lines[0].Amount);
        s.UpdateLine(id, overrideAmount: 180m);
        Eq<decimal?>(180m, s.GetDraft().Lines[0].Amount);
        s.UpdateLine(id, clearOverride: true);
        Eq<decimal?>(120m, s.GetDraft().Lines[0].Amount);
    }),
    ("impossible de finaliser avec une heure de fin manquante", () =>
    {
        using var s = Fresh();
        s.AddLine("SDC A", "2026-11-03", "18:30");
        Throws<UserException>(() => s.Finalize("2026-11-30"), "sans heure de fin");
    }),
    ("finaliser : numéro, montants figés, nouvelle facture en cours vide", () =>
    {
        using var s = Fresh();
        s.AddLine("SDC A", "2026-11-03", "18:30", "21:00");
        var id = s.Finalize("2026-11-30");
        var inv = s.GetInvoice(id);
        Eq(120, inv.Invoice.Number); Eq(InvoiceStatus.Final, inv.Invoice.Status); Eq(90m, inv.Invoice.Total);
        Eq("121", s.GetSettings()["next_number"]);
        Eq(0, s.GetDraft().Lines.Count);
        True(s.GetDraft().Id != id);
        Throws<UserException>(() => s.UpdateLine(inv.Lines[0].Id, end: "23:00"), "finalisée");
        s.AddRate("2026-01-01", 40m, 2m);
        Eq<decimal?>(90m, s.GetInvoice(id).Lines[0].Amount);
    }),
    ("ag-connect : ignore doublons et assemblées avec adresse", () =>
    {
        using var s = Fresh();
        var items = new[]
        {
            new AgConnectItem("SDC A", "2026-11-03", "18:30", "", true, ""),
            new AgConnectItem("SDC B", "2026-11-04", "19:00", "1 rue X", false, ""),
        };
        Eq(1, s.AddFromAgConnect(items).Added.Count);
        var again = s.AddFromAgConnect(items);
        Eq(0, again.Added.Count); Eq(2, again.Skipped.Count);
    }),
    ("payée / non payée et état des résultats", () =>
    {
        using var s = Fresh();
        s.AddLine("SDC A", "2026-11-03", "18:30", "21:00");
        var id = s.Finalize("2026-11-30");
        Eq(90m, s.GetReport(2026).Unpaid);
        s.MarkPaid(id, "2026-12-05", "Virement Interac");
        var r = s.GetReport(2026);
        Eq(90m, r.Total); Eq(0m, r.Unpaid);
        Eq(Store.CategorieVirtuelle, r.Categories.Single().Category);
        s.MarkUnpaid(id);
        Eq(90m, s.GetReport(2026).Unpaid);
    }),
    ("import d'un export JSON de Finance D", () =>
    {
        using var s = new Store(":memory:");
        Eq(new ImportResult(2, 0), s.ImportFinanceD(Sample.FinanceD));
        Eq(new ImportResult(0, 2), s.ImportFinanceD(Sample.FinanceD));
        var r = s.GetReport(2026);
        Eq(163.65m, r.Total); Eq(60m, r.Unpaid);
        Eq("20", s.GetSettings()["next_number"]);
        Throws<UserException>(() => s.ImportFinanceD("{\"foo\":1}"), "Finance D");
        Throws<UserException>(() => s.ImportFinanceD("pas du json"), "JSON");
    }),
    ("réglages : numéro invalide refusé", () =>
    {
        using var s = Fresh();
        Throws<UserException>(() => s.UpdateSettings(new Dictionary<string, string> { ["next_number"] = "12a" }), "nombre entier");
        s.UpdateSettings(new Dictionary<string, string> { ["client_name"] = "Client X", ["inconnu"] = "ignoré" });
        Eq("Client X", s.GetSettings()["client_name"]);
        False(s.GetSettings().ContainsKey("inconnu"));
    }),
    ("facture PDF générée, nom de fichier avec ou sans client", () =>
    {
        using var s = Fresh();
        s.AddLine("SDC A", "2026-11-03", "18:30", "21:00");
        var inv = s.GetInvoice(s.Finalize("2026-11-30"));
        var pdf = InvoicePdf.Generate(inv, s.GetSettings());
        True(pdf.Length > 1000 && pdf[0] == '%' && pdf[1] == 'P' && pdf[2] == 'D' && pdf[3] == 'F', "en-tête %PDF");
        Eq("Facture-120_Client-Test.pdf", InvoicePdf.FileName(inv, s.GetSettings()));
        s.UpdateSettings(new Dictionary<string, string> { ["client_name"] = "" });
        Eq("Facture-120.pdf", InvoicePdf.FileName(inv, s.GetSettings()));
    }),

    // ---------- Délai de paiement ----------
    ("délai de paiement : réglage par défaut, mémorisé par facture, échéance et retard", () =>
    {
        using var s = Fresh();
        Eq("15", s.GetSettings()["payment_days"]);
        s.AddLine("SDC A", "2026-11-03", "18:30", "21:00");
        var a = s.GetInvoice(s.Finalize("2026-11-30")).Invoice;
        Eq<int?>(15, a.PaymentDays);
        Eq("2026-12-15", a.DueDate);
        False(a.IsOverdue(new DateOnly(2026, 12, 15)));
        True(a.IsOverdue(new DateOnly(2026, 12, 16)), "en retard le lendemain de l'échéance");
        s.UpdateSettings(new Dictionary<string, string> { ["payment_days"] = "30" });
        s.AddLine("SDC B", "2026-12-03", "18:30", "21:00");
        var b = s.GetInvoice(s.Finalize("2026-12-31")).Invoice;
        Eq<int?>(30, b.PaymentDays);
        Eq<int?>(15, s.GetInvoice(a.Id).Invoice.PaymentDays); // une facture déjà émise ne change pas
        s.AddLine("SDC C", "2027-01-03", "18:30", "21:00");
        Eq<int?>(0, s.GetInvoice(s.Finalize("2027-01-31", paymentDays: 0)).Invoice.PaymentDays);
        s.MarkPaid(a.Id, "2026-12-20", "Chèque");
        False(s.GetInvoice(a.Id).Invoice.IsOverdue(new DateOnly(2027, 6, 1)));
        Throws<UserException>(() => s.UpdateSettings(new Dictionary<string, string> { ["payment_days"] = "45" }), "Délai de paiement invalide");
    }),
    ("PDF : phrase du délai de paiement", () =>
    {
        var inv = new InvoiceSummary(1, 120, InvoiceStatus.Final, "2026-11-30", null, null, 90m, "app", 15);
        Eq("Net 15 jours : payable au plus tard le 15 décembre 2026.", InvoicePdf.PaymentTermsSentence(inv));
        Eq("Payable sur réception.", InvoicePdf.PaymentTermsSentence(inv with { PaymentDays = 0 }));
        Eq<string?>(null, InvoicePdf.PaymentTermsSentence(inv with { PaymentDays = null }));
    }),

    // ---------- Lignes libres ----------
    ("ligne libre : ajoutée, modifiée, finalisée avec les assemblées", () =>
    {
        using var s = Fresh();
        s.AddLine("SDC A", "2026-11-03", "18:30", "21:00");
        var f = s.AddFreeLine("Formation", "1,5 heure de formation", 60m, "2026-11-10");
        var d = s.GetDraft();
        Eq(150m, d.Total);
        True(d.Lines.Single(l => l.Id == f).IsFree, "ligne libre");
        s.UpdateFreeLine(f, description: "2 heures de formation", amount: 80m);
        Eq(170m, s.GetDraft().Total);
        Throws<UserException>(() => s.UpdateLine(f, end: "21:00"), "ligne libre");
        Throws<UserException>(() => s.AddFreeLine("Formation", " ", 10m, "2026-11-10"), "description");
        var inv = s.GetInvoice(s.Finalize("2026-11-30"));
        Eq(170m, inv.Invoice.Total);
        Eq("2 heures de formation", inv.Lines.Single(l => l.IsFree).Description);
        True(s.KnownCategories().Contains("Formation"), "catégorie proposée");
    }),

    // ---------- Réouverture ----------
    ("rouvrir : même numéro, assemblée modifiée recalculée, la facture du mois reste de côté", () =>
    {
        using var s = Fresh();
        var line = s.AddLine("SDC A", "2026-11-03", "18:30", "20:34");
        s.AddLine("SDC B", "2026-11-05", "18:30", "20:00");
        var id = s.Finalize("2026-11-30");
        s.AddLine("SDC Décembre", "2026-12-02", "18:30"); // facture du mois suivant, incomplète
        s.Reopen(id);
        var w = s.GetDraft();
        True(w.IsReopened, "facture rouverte");
        Eq<int?>(120, w.Number);
        Eq(2, w.Lines.Count);
        var lineId = w.Lines.Single(l => l.Entity == "SDC A").Id;
        s.UpdateLine(lineId, end: "22:31"); // 4 h 01 → 5 h → 150 $
        Eq(210m, s.GetDraft().Total);
        Throws<UserException>(() => s.Finalize("2026-12-31"), "en cours de modification");
        var r = s.SaveReopened("2026-11-30", 15);
        Eq(120, r.Number); False(r.PaymentReset);
        Eq(210m, s.GetInvoice(id).Invoice.Total);
        Eq("SDC A (18h30 - 22h31)", s.GetInvoice(id).Lines.Single(l => l.Entity == "SDC A").Description);
        var back = s.GetDraft();
        False(back.IsReopened);
        Eq("SDC Décembre", back.Lines.Single().Entity);
        Eq("121", s.GetSettings()["next_number"]);
    }),
    ("rouvrir : une ligne non touchée garde son montant même si la grille a changé", () =>
    {
        using var s = Fresh();
        s.AddLine("SDC A", "2026-11-03", "18:30", "20:34");
        var id = s.Finalize("2026-11-30");
        s.AddRate("2026-01-01", 40m, 2m); // nouveau taux à 40 $/h
        s.Reopen(id);
        s.AddFreeLine("Formation", "Formation", 60m, "2026-11-10");
        Eq(150m, s.GetDraft().Total); // 90 $ conservés + 60 $
        s.SaveReopened("2026-11-30", 15);
        Eq(150m, s.GetInvoice(id).Invoice.Total);
    }),
    ("annuler les modifications : la facture revient exactement comme avant", () =>
    {
        using var s = Fresh();
        s.AddLine("SDC A", "2026-11-03", "18:30", "20:34");
        s.AddLine("SDC B", "2026-11-05", "18:30", "20:00");
        var id = s.Finalize("2026-11-30");
        s.MarkPaid(id, "2026-12-01", "Chèque");
        var before = s.GetInvoice(id);
        s.Reopen(id);
        var w = s.GetDraft();
        s.DeleteLine(w.Lines[0].Id);
        s.UpdateLine(w.Lines[1].Id, end: "23:59");
        s.AddFreeLine("Formation", "Ajout", 99m, "2026-11-10");
        s.CancelReopen();
        var after = s.GetInvoice(id);
        Eq(before.Invoice, after.Invoice);
        Eq(string.Join("|", before.Lines.Select(l => $"{l.Id}:{l.Description}:{l.Amount}")), string.Join("|", after.Lines.Select(l => $"{l.Id}:{l.Description}:{l.Amount}")));
        False(s.GetDraft().IsReopened);
    }),
    ("facture payée rouverte : total changé → « À payer » ; total identique → reste payée", () =>
    {
        using var s = Fresh();
        s.AddLine("SDC A", "2026-11-03", "18:30", "20:34");
        var id = s.Finalize("2026-11-30");
        s.MarkPaid(id, "2026-12-01", "Chèque");
        s.Reopen(id);
        s.SaveReopened("2026-11-29", 30); // seulement la date et le délai
        var same = s.GetInvoice(id).Invoice;
        Eq(InvoiceStatus.Paid, same.Status); Eq("2026-11-29", same.InvoiceDate); Eq<int?>(30, same.PaymentDays);
        s.Reopen(id);
        s.AddFreeLine("Per Diem", "Repas", 25m, "2026-11-03");
        var r = s.SaveReopened("2026-11-29", 30);
        True(r.PaymentReset, "paiement remis à zéro");
        Eq(InvoiceStatus.Final, s.GetInvoice(id).Invoice.Status);
        Eq<string?>(null, s.GetInvoice(id).Invoice.PaidAt);
    }),
    ("rouvrir : une seule à la fois ; facture importée de Finance D modifiable", () =>
    {
        using var s = new Store(":memory:");
        s.ImportFinanceD(Sample.FinanceD);
        var imported = s.ListInvoices().Single(i => i.Number == 18);
        var other = s.ListInvoices().Single(i => i.Number == 19);
        s.Reopen(imported.Id);
        Throws<UserException>(() => s.Reopen(other.Id), "déjà en cours de modification");
        var km = s.GetDraft().Lines.Single(l => l.Category == "Kilométrage");
        True(km.IsFree, "ligne importée = ligne libre");
        s.UpdateFreeLine(km.Id, amount: 14.04m);
        var r = s.SaveReopened("2026-07-30", 15);
        True(r.PaymentReset, "était payée, total changé");
        Eq(104.04m, s.GetInvoice(imported.Id).Invoice.Total);
        Eq<int?>(15, s.GetInvoice(imported.Id).Invoice.PaymentDays);
    }),

    // ---------- Fiabilité des données ----------
    ("réglages : tout ou rien (une valeur invalide n'enregistre rien)", () =>
    {
        using var s = Fresh();
        s.UpdateSettings(new Dictionary<string, string> { ["issuer_name"] = "Avant" });
        Throws<UserException>(() => s.UpdateSettings(new Dictionary<string, string> { ["issuer_name"] = "Après", ["next_number"] = "abc" }), "nombre entier");
        Eq("Avant", s.GetSettings()["issuer_name"]);
        Throws<UserException>(() => s.UpdateSettings(new Dictionary<string, string> { ["next_number"] = "99999999999" }), "nombre entier");
        Throws<UserException>(() => s.UpdateSettings(new Dictionary<string, string> { ["next_number"] = "0" }), "nombre entier");
    }),
    ("réglages : détecte les champs remplis qui seraient effacés", () =>
    {
        using var s = Fresh();
        s.UpdateSettings(new Dictionary<string, string> { ["issuer_name"] = "Moi", ["issuer_phone"] = "" });
        var cleared = s.SettingsThatWouldBeCleared(new Dictionary<string, string> { ["issuer_name"] = "", ["issuer_phone"] = "", ["client_name"] = "Client Test" });
        Eq("issuer_name", string.Join(",", cleared));
    }),
    ("sauvegarde quotidienne : une par jour, 30 gardées, et avant un import", () =>
    {
        var dir = Directory.CreateTempSubdirectory("fag-test-");
        try
        {
            var backups = Path.Combine(dir.FullName, "Sauvegardes");
            Directory.CreateDirectory(backups);
            for (var i = 1; i <= 35; i++) File.WriteAllText(Path.Combine(backups, $"factures-ag-2025{i:0000}-120000-quotidienne.db"), "x");
            using (var s = new Store(Path.Combine(dir.FullName, "base.db"), backups))
            {
                True(s.DailyBackup() is not null, "sauvegarde du jour créée");
                Eq<string?>(null, s.DailyBackup()); // pas deux fois le même jour
                Eq(30, Directory.GetFiles(backups, "*-quotidienne.db").Length);
                True(File.Exists(Directory.GetFiles(backups, $"factures-ag-{DateTime.Now:yyyyMMdd}-*-quotidienne.db").Single()), "la plus récente est gardée");
                s.ImportFinanceD(Sample.FinanceD);
                Eq(1, Directory.GetFiles(backups, "*-avant-import.db").Length);
            }
        }
        finally { ClearPools(); dir.Delete(true); }
    }),
    ("montants arrondis au cent : le total est la somme des lignes", () =>
    {
        using var s = new Store(":memory:");
        s.AddRate("2025-01-01", 47.25m, 1.5m);
        s.AddLine("SDC A", "2026-11-03", "18:30", "", quorum: false);
        s.AddLine("SDC B", "2026-11-04", "18:30", "", quorum: false);
        var d = s.GetDraft();
        Eq<decimal?>(70.88m, d.Lines[0].Amount);
        Eq(141.76m, d.Total);
    }),
    ("durée inhabituelle signalée (faute de frappe probable)", () =>
    {
        using var s = Fresh();
        s.AddLine("SDC A", "2026-11-03", "18:30", "08:30");
        var l = s.GetDraft().Lines[0];
        True(l.IsUnusual, "14 h signalées");
        True(l.Detail.Contains("vérifiez"), "message");
        s.UpdateLine(l.Id, end: "20:30");
        False(s.GetDraft().Lines[0].IsUnusual);
    }),

    ("remplacement du .exe même si un ancien « .old » est encore verrouillé", () =>
    {
        var dir = Directory.CreateTempSubdirectory("fag-test-");
        try
        {
            var current = Path.Combine(dir.FullName, "Factures AG.exe");
            File.WriteAllText(current, "v2");
            File.WriteAllText(current + ".old", "v1");
            var update = Path.Combine(dir.FullName, "telechargement.exe");
            File.WriteAllText(update, "v3");
            using (File.Open(current + ".old", FileMode.Open, FileAccess.Read, FileShare.None)) // ancienne copie encore ouverte
            {
                Updater.Swap(current, update);
            }
            Eq("v3", File.ReadAllText(current));
            Eq(2, Directory.GetFiles(dir.FullName, "Factures AG.exe.old*").Length);
            Updater.CleanupOld(current);
            Eq(0, Directory.GetFiles(dir.FullName, "Factures AG.exe.old*").Length);
        }
        finally { dir.Delete(true); }
    }),

    // ---------- Installation ----------
    ("installation : dossier, détection du .exe téléchargé", () =>
    {
        var dir = @"C:\Users\x\AppData\Local\Programs\FacturesAG";
        Eq(@"C:\Users\x\AppData\Local\Programs\FacturesAG\Factures AG.exe", Installer.InstalledExe(dir));
        False(Installer.ShouldInstall(@"c:\users\X\AppData\Local\Programs\FacturesAG\Factures AG.exe", dir));
        True(Installer.ShouldInstall(@"C:\Users\x\Downloads\FacturesAG-0.4.0-win-x64.exe", dir), "téléchargé");
    }),
    ("installation : copie, remplace une vieille copie, garde une copie plus récente", () =>
    {
        var tmp = Directory.CreateTempSubdirectory("fag-test-");
        try
        {
            var downloaded = Path.Combine(tmp.FullName, "Downloads", "FacturesAG-0.4.0-win-x64.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(downloaded)!);
            File.WriteAllText(downloaded, "nouveau");
            var installDir = Path.Combine(tmp.FullName, "Programs", "FacturesAG");
            var target = Installer.Install(downloaded, new Version(0, 4, 0), null, installDir);
            Eq(Path.Combine(installDir, "Factures AG.exe"), target);
            Eq("nouveau", File.ReadAllText(target));
            File.WriteAllText(target, "ancien");
            Installer.Install(downloaded, new Version(0, 4, 0), new Version(0, 3, 1), installDir);
            Eq("nouveau", File.ReadAllText(target));
            File.WriteAllText(target, "plus récent");
            Installer.Install(downloaded, new Version(0, 4, 0), new Version(0, 5, 0), installDir);
            Eq("plus récent", File.ReadAllText(target));
        }
        finally { tmp.Delete(true); }
    }),
    ("raccourcis : script (apostrophes) et création réelle avec PowerShell", () =>
    {
        var script = Installer.ShortcutScript(@"C:\Users\d'Arcy\AppData\Local\Programs\FacturesAG\Factures AG.exe");
        True(script.Contains(@"'C:\Users\d''Arcy\AppData\Local\Programs\FacturesAG\Factures AG.exe'"), "apostrophe échappée");
        True(script.Contains("GetFolderPath('Desktop')") && script.Contains("GetFolderPath('Programs')"), "Bureau et menu Démarrer");
        True(script.Contains("'Factures AG.lnk'"), "nom du raccourci");
        var tmp = Directory.CreateTempSubdirectory("fag-test-");
        try
        {
            var target = Path.Combine(tmp.FullName, "Factures AG.exe");
            File.WriteAllText(target, "x");
            var links = Path.Combine(tmp.FullName, "Raccourcis");
            Environment.SetEnvironmentVariable("FACTURES_AG_SHORTCUT_DIR", links);
            Installer.CreateShortcuts(target);
            True(File.Exists(Path.Combine(links, "Factures AG.lnk")), "raccourci créé");
        }
        finally { Environment.SetEnvironmentVariable("FACTURES_AG_SHORTCUT_DIR", null); tmp.Delete(true); }
    }),

    // ---------- Migrations de la base ----------
    ("base neuve : structure à jour", () =>
    {
        using var s = new Store(":memory:");
        Eq(Store.Migrations.Max(m => m.Version), s.SchemaVersion);
    }),
    ("base de la version 0.1.0 (sans numéro) : passage à la structure 2 avec sauvegarde, données intactes", () =>
    {
        var dir = Directory.CreateTempSubdirectory("fag-test-");
        try
        {
            var file = Path.Combine(dir.FullName, "v010.db");
            CreateV010Database(file);
            var backups = Path.Combine(dir.FullName, "Sauvegardes");
            using (var s = new Store(file, backups))
            {
                Eq(2, s.SchemaVersion);
                Eq(1, s.ListInvoices().Count);
                Eq(90m, s.ListInvoices()[0].Total);
                Eq<int?>(15, s.ListInvoices()[0].PaymentDays);              // anciennes factures : Net 15
                Eq(Store.DefaultPaymentTerms, s.GetSettings()["payment_terms"]); // « Net 15 » retiré du texte libre
                Eq("15", s.GetSettings()["payment_days"]);
            }
            var saved = Directory.GetFiles(backups);
            Eq(1, saved.Length);
            True(saved[0].EndsWith("-avant-structure-2.db"), "nom de la sauvegarde");
            // Une seconde ouverture ne refait ni migration ni sauvegarde.
            using (new Store(file, backups)) { }
            Eq(1, Directory.GetFiles(backups).Length);
        }
        finally { ClearPools(); dir.Delete(true); }
    }),
    ("nouvelle structure : sauvegarde avant migration, données conservées", () =>
    {
        var dir = Directory.CreateTempSubdirectory("fag-test-");
        try
        {
            var file = Path.Combine(dir.FullName, "base.db");
            var backups = Path.Combine(dir.FullName, "Sauvegardes");
            CreateV010Database(file);
            using (new Store(file)) { } // mise à la structure actuelle
            var next = Store.Migrations.Max(m => m.Version) + 1;
            var withNext = Store.Migrations.Append(new Migration(next, "ALTER TABLE lines ADD COLUMN km REAL;")).ToList();
            using (var s = new Store(file, backups, withNext))
            {
                Eq(next, s.SchemaVersion);
                Eq(90m, s.ListInvoices()[0].Total);
            }
            var saved = Directory.GetFiles(backups);
            Eq(1, saved.Length);
            True(saved[0].EndsWith($"-avant-structure-{next}.db"), "nom de la sauvegarde");
            using (var copy = new Store(saved[0])) { Eq(next - 1, copy.SchemaVersion); Eq(1, copy.ListInvoices().Count); }
            // Une version plus ancienne de l'application refuse d'ouvrir une base plus récente.
            Throws<UserException>(() => new Store(file).Dispose(), "plus récente");
        }
        finally { ClearPools(); dir.Delete(true); }
    }),

    // ---------- Mises à jour ----------
    ("numéro de version lu dans l'étiquette", () =>
    {
        Eq(new Version(0, 2, 0), Updater.ParseTag("v0.2.0"));
        Eq(new Version(1, 0, 0), Updater.ParseTag("1.0"));
        Eq(new Version(0, 3, 1), Updater.ParseTag("v0.3.1-beta"));
        Eq<Version?>(null, Updater.ParseTag("latest"));
    }),
    ("mise à jour détectée seulement si plus récente, avec le .exe et son empreinte", () =>
    {
        var v010 = new Version(0, 1, 0);
        var u = Updater.FindUpdate(Sample.Release("v0.2.0"), v010)!;
        Eq(new Version(0, 2, 0), u.Version);
        Eq("FacturesAG-0.2.0-win-x64.exe", u.AssetName);
        Eq(new string('a', 64), u.Sha256);
        Eq<UpdateInfo?>(null, Updater.FindUpdate(Sample.Release("v0.1.0"), v010));
        Eq<UpdateInfo?>(null, Updater.FindUpdate(Sample.Release("v0.2.0"), new Version(0, 2, 0, 0)));
        Eq<UpdateInfo?>(null, Updater.FindUpdate(Sample.Release("v0.2.0", digest: null), v010));
        Eq<UpdateInfo?>(null, Updater.FindUpdate(Sample.Release("v0.2.0", asset: "autre.zip"), v010));
        Eq<UpdateInfo?>(null, Updater.FindUpdate(Sample.Release("v0.2.0", prerelease: true), v010));
        Eq<UpdateInfo?>(null, Updater.FindUpdate(Sample.Release("v0.2.0", url: "https://exemple.com/x.exe"), v010));
    }),
    ("téléchargement vérifié : empreinte correcte acceptée, altérée refusée et supprimée", () =>
    {
        var dir = Directory.CreateTempSubdirectory("fag-test-");
        try
        {
            var file = Path.Combine(dir.FullName, "new.exe");
            File.WriteAllText(file, "contenu");
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("contenu"u8));
            var ok = new UpdateInfo(new Version(0, 2, 0), "v0.2.0", "a", "https://github.com/x", hash, 7);
            Updater.VerifyFile(file, ok);
            True(File.Exists(file), "fichier conservé");
            Throws<UserException>(() => Updater.VerifyFile(file, ok with { Sha256 = new string('0', 64) }), "altéré");
            False(File.Exists(file));
        }
        finally { dir.Delete(true); }
    }),
    ("remplacement du .exe puis nettoyage de l'ancien", () =>
    {
        var dir = Directory.CreateTempSubdirectory("fag-test-");
        try
        {
            var current = Path.Combine(dir.FullName, "Factures AG.exe");
            var update = Path.Combine(dir.FullName, "telechargement.exe");
            File.WriteAllText(current, "ancien");
            File.WriteAllText(update, "nouveau");
            Updater.Swap(current, update);
            Eq("nouveau", File.ReadAllText(current));
            Eq("ancien", File.ReadAllText(current + ".old"));
            False(File.Exists(update));
            Updater.CleanupOld(current);
            False(File.Exists(current + ".old"));
        }
        finally { dir.Delete(true); }
    }),
};

// Base telle que créée par la version 0.1.0 : tables de la structure 1, sans numéro de version, une facture.
static void CreateV010Database(string file)
{
    using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False");
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = Store.Migrations[0].Sql + """
        INSERT INTO invoices (id, number, status, invoice_date, total_cents) VALUES (1, 7, 'final', '2026-01-31', 9000);
        INSERT INTO lines (invoice_id, category, entity, date, amount_cents, description) VALUES (1, 'Assemblée Virtuelle', 'SDC A', '2026-01-10', 9000, 'SDC A');
        """;
    cmd.ExecuteNonQuery();
}

// Libère les fichiers SQLite pour pouvoir supprimer les dossiers temporaires.
static void ClearPools() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

int passed = 0, failed = 0;
foreach (var (name, body) in tests)
{
    try { body(); passed++; Console.WriteLine($"✔ {name}"); }
    catch (Exception e)
    {
        failed++;
        var where = e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Program.cs") || l.Contains("FacturesAG.Core"))?.Trim();
        Console.WriteLine($"✖ {name}\n    {e.Message}\n    {where}");
    }
}
Console.WriteLine($"\n{passed} réussi(s), {failed} échec(s)");
return failed == 0 ? 0 : 1;

static void Eq<T>(T expected, T actual, string? what = null)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{what} attendu : {expected?.ToString() ?? "null"} — obtenu : {actual?.ToString() ?? "null"}");
}
static void True(bool cond, string what = "condition") { if (!cond) throw new Exception($"{what} fausse"); }
static void False(bool cond) => True(!cond);
static void Throws<TEx>(Action a, string contains) where TEx : Exception
{
    try { a(); }
    catch (TEx e) when (e.Message.Contains(contains)) { return; }
    catch (Exception e) { throw new Exception($"exception inattendue : {e.GetType().Name} {e.Message}"); }
    throw new Exception($"aucune exception (attendu : « {contains} »)");
}

static class Sample
{
    // Réponse de l'API GitHub « releases/latest », réduite aux champs utilisés.
    public static string Release(string tag, string? digest = "sha256:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string asset = "FacturesAG-0.2.0-win-x64.exe", bool prerelease = false, string? url = null) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease,
            assets = new[]
            {
                new { name = asset, size = 1234L, digest,
                      browser_download_url = url ?? $"https://github.com/EricGloutnez/FacturesAG/releases/download/{tag}/{asset}" },
            },
        });

    // Texte au format de la page « Mes invitations » d'ag-connect (données fictives).
    public const string Invitations = """
        Tableau de bord
        Mes invitations
        5 éléments
        Octobre 2026
        3
        Ouverture
        SDC Les Jardins
        Assemblée Générale Annuelle / Annual General Meeting
        Organisateur A
        27 octobre 2026 à 18:30 (heure de Montreal)
        100 rue Principale, Laval -
        334 participants
        Voir l'assemblée
        Clôturée
        SDC Le Phare Tour Est
        Assemblée Générale Annuelle
        Organisateur B
        05 octobre 2026 à 18:30 (heure de Montreal)
        -
        133 participants
        Voir l'assemblée
        Septembre 2026
        Clôturée
        Association des copropriétaires du Domaine des Érables Phase 1
        Assemblée Générale Annuelle
        organisateur@example.com
        29 septembre 2026 à 18:30 (heure de Montreal)
        -
        129 participants
        Voir l'assemblée
        Convoquée
        SDC Août
        Assemblée Générale Spéciale
        Organisateur C
        1 août 2026 à 9:00 (heure de Montreal)
        -
        12 participants
        Voir l'assemblée
        """;

    public const string FinanceD = """
        { "data": { "facturesRevenus": {
          "a": { "numero": "18", "date": { "num": "20260730" }, "solde": 0, "transactions": [
            { "montant": "90.00", "total": "90.00", "categorie": "Assemblée Virtuelle", "description": "SDC Les Jardins (18h30 - 20h34)" },
            { "montant": "13.65", "total": "13.65", "categorie": "Kilométrage", "description": "19.5 KM aller" } ] },
          "b": { "numero": "19", "date": { "num": "20260805" }, "solde": 60, "transactions": [
            { "montant": "60.00", "total": "60.00", "categorie": "Formation", "description": "Formation" } ] }
        } } }
        """;
}
