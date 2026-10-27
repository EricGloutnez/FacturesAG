using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace FacturesAG.Core;

/// <summary>
/// Stockage SQLite : réglages, grille de tarifs, factures et lignes.
/// Les montants sont gardés en cents pour éviter les erreurs d'arrondi.
/// </summary>
public sealed class Store : IDisposable
{
    public const string CategorieVirtuelle = "Assemblée Virtuelle";

    public static readonly IReadOnlyDictionary<string, string> DefaultSettings = new Dictionary<string, string>
    {
        // Coordonnées et client : à remplir dans les Réglages au premier lancement.
        ["issuer_name"] = "",
        ["issuer_phone"] = "",
        ["issuer_email"] = "",
        ["issuer_street"] = "",
        ["issuer_city"] = "",
        ["client_name"] = "",
        ["client_email"] = "",
        ["client_street"] = "",
        ["client_city"] = "",
        ["payment_terms"] = "Solde payable sur réception par virement Interac, par chèque ou comptant. Net 15 jours suivant la date de facture.",
        ["footer"] = "Merci de votre confiance.",
        ["next_number"] = "1",
    };

    /// <summary>
    /// Évolutions successives de la structure de la base. Chaque version n'est appliquée qu'une fois
    /// (numéro gardé dans PRAGMA user_version). Ne jamais modifier une entrée publiée : en ajouter une nouvelle.
    /// </summary>
    public static readonly IReadOnlyList<Migration> Migrations =
    [
        new(1, """
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS rates (
              id INTEGER PRIMARY KEY, category TEXT NOT NULL, effective_from TEXT NOT NULL,
              hourly_cents INTEGER NOT NULL, min_hours REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS invoices (
              id INTEGER PRIMARY KEY, number INTEGER UNIQUE,
              status TEXT NOT NULL CHECK (status IN ('draft','final','paid')),
              invoice_date TEXT, finalized_at TEXT, paid_at TEXT, payment_method TEXT,
              total_cents INTEGER, source TEXT NOT NULL DEFAULT 'app');
            CREATE TABLE IF NOT EXISTS lines (
              id INTEGER PRIMARY KEY, invoice_id INTEGER NOT NULL REFERENCES invoices(id) ON DELETE CASCADE,
              category TEXT NOT NULL, entity TEXT, date TEXT, start TEXT, end_time TEXT,
              quorum INTEGER NOT NULL DEFAULT 1, override_cents INTEGER, amount_cents INTEGER, description TEXT);
            """),
    ];

    private readonly SqliteConnection _db;
    private readonly string? _backupDir;

    /// <param name="file">Fichier SQLite (ou ":memory:").</param>
    /// <param name="backupDir">Dossier des sauvegardes faites avant chaque migration ; null pour ne pas sauvegarder.</param>
    /// <param name="migrations">Liste de migrations à utiliser (par défaut <see cref="Migrations"/> ; sert aux tests).</param>
    public Store(string file, string? backupDir = null, IReadOnlyList<Migration>? migrations = null)
    {
        _backupDir = backupDir;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, ForeignKeys = true }.ToString());
        _db.Open();
        try { Migrate(migrations ?? Migrations); }
        catch
        {
            // Ne pas laisser le fichier verrouillé si la base ne peut pas être ouverte.
            _db.Dispose();
            throw;
        }
    }

    /// <summary>Version de structure de la base ouverte.</summary>
    public int SchemaVersion => (int)(long)Scalar("PRAGMA user_version")!;

    /// <summary>Copie complète et cohérente de la base dans le dossier des sauvegardes. Retourne le chemin, ou null.</summary>
    public string? Backup(string reason)
    {
        if (_backupDir is null) return null;
        Directory.CreateDirectory(_backupDir);
        var file = Path.Combine(_backupDir, $"factures-ag-{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.db");
        using var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        dest.Open();
        _db.BackupDatabase(dest);
        return file;
    }

    public void Dispose() => _db.Dispose();

    // ---------- Accès SQL ----------
    private SqliteCommand Cmd(string sql, params object?[] args)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        for (var i = 0; i < args.Length; i++) c.Parameters.AddWithValue($"${i + 1}", args[i] ?? DBNull.Value);
        return c;
    }

    private int Exec(string sql, params object?[] args) { using var c = Cmd(sql, args); return c.ExecuteNonQuery(); }

    private object? Scalar(string sql, params object?[] args) { using var c = Cmd(sql, args); var v = c.ExecuteScalar(); return v is DBNull ? null : v; }

    private long Insert(string sql, params object?[] args) { Exec(sql, args); return (long)Scalar("SELECT last_insert_rowid()")!; }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params object?[] args)
    {
        using var c = Cmd(sql, args);
        using var r = c.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private T InTransaction<T>(Func<T> work)
    {
        using var tx = _db.BeginTransaction();
        var result = work();
        tx.Commit();
        return result;
    }

    private static string? Str(SqliteDataReader r, string col) => r[col] is DBNull ? null : (string)r[col];
    private static long? Long(SqliteDataReader r, string col) => r[col] is DBNull ? null : (long)r[col];
    private static long ToCents(decimal d) => (long)Math.Round(d * 100m, MidpointRounding.AwayFromZero);
    private static decimal FromCents(long c) => c / 100m;

    private static bool IsIsoDate(string? s) =>
        s is not null && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static string CheckTime(string? value, string label) =>
        Billing.NormalizeTime(value) ?? throw new UserException($"{label} invalide : écrivez par exemple 20:34, 20h34 ou 2034.");

    // Journal classique (pas WAL) : un seul fichier, plus sûr si le dossier est synchronisé par OneDrive.
    private void Migrate(IReadOnlyList<Migration> migrations)
    {
        var target = migrations.Count == 0 ? 0 : migrations.Max(m => m.Version);
        var current = SchemaVersion;
        // Base créée par la version 0.1.0 (sans numéro) : ses tables correspondent à la version 1.
        if (current == 0 && Scalar("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'invoices'") is not null)
        {
            Exec("PRAGMA user_version = 1");
            current = 1;
        }
        if (current > target)
            throw new UserException($"Ces données ont été créées par une version plus récente de Factures AG (structure {current}). Mettez l'application à jour.");

        var pending = migrations.Where(m => m.Version > current).OrderBy(m => m.Version).ToList();
        // Sauvegarde avant de toucher à une base existante.
        if (pending.Count > 0 && current > 0) Backup($"avant-structure-{pending[^1].Version}");
        foreach (var m in pending)
        {
            InTransaction(() =>
            {
                Exec(m.Sql);
                Exec($"PRAGMA user_version = {m.Version.ToString(CultureInfo.InvariantCulture)}");
                return 0;
            });
        }
        foreach (var (k, v) in DefaultSettings) Exec("INSERT OR IGNORE INTO settings (key, value) VALUES ($1, $2)", k, v);
    }

    // ---------- Réglages et tarifs ----------
    public Dictionary<string, string> GetSettings() =>
        Query("SELECT key, value FROM settings", r => (Key: (string)r["key"], Value: (string)r["value"])).ToDictionary(x => x.Key, x => x.Value);

    public void UpdateSettings(IDictionary<string, string> patch)
    {
        foreach (var (k, v) in patch)
        {
            if (!DefaultSettings.ContainsKey(k)) continue;
            if (k == "next_number" && !Regex.IsMatch(v ?? "", @"^\d+$")) throw new UserException("Le prochain numéro de facture doit être un nombre entier.");
            Exec("INSERT INTO settings (key, value) VALUES ($1, $2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", k, v ?? "");
        }
    }

    public List<Rate> ListRates() =>
        Query("SELECT * FROM rates WHERE category = $1 ORDER BY effective_from",
            r => new Rate((long)r["id"], (string)r["category"], (string)r["effective_from"], FromCents((long)r["hourly_cents"]), (decimal)(double)r["min_hours"]),
            CategorieVirtuelle);

    public void AddRate(string effectiveFrom, decimal hourlyRate, decimal minHours)
    {
        if (!IsIsoDate(effectiveFrom)) throw new UserException("Date d'entrée en vigueur invalide.");
        if (hourlyRate <= 0 || minHours < 0) throw new UserException("Taux ou minimum invalide.");
        Exec("INSERT INTO rates (category, effective_from, hourly_cents, min_hours) VALUES ($1, $2, $3, $4)", CategorieVirtuelle, effectiveFrom, ToCents(hourlyRate), (double)minHours);
    }

    public void DeleteRate(long id) => Exec("DELETE FROM rates WHERE id = $1", id);

    // ---------- Facture en cours ----------
    private long DraftId() =>
        Scalar("SELECT id FROM invoices WHERE status = 'draft' ORDER BY id LIMIT 1") is long id
            ? id
            : Insert("INSERT INTO invoices (status) VALUES ('draft')");

    private sealed record RawLine(long Id, string Category, string? Entity, string Date, string Start, string End, bool Quorum,
        long? OverrideCents, long? AmountCents, string? Description);

    private static RawLine ReadLine(SqliteDataReader r) => new(
        (long)r["id"], (string)r["category"], Str(r, "entity"), Str(r, "date") ?? "", Str(r, "start") ?? "", Str(r, "end_time") ?? "",
        (long)r["quorum"] != 0, Long(r, "override_cents"), Long(r, "amount_cents"), Str(r, "description"));

    private static LineView Present(RawLine l, IReadOnlyList<Rate> rates)
    {
        var computed = l.Entity is not null ? Billing.ComputeAmount(l.Date, l.Start, l.End, l.Quorum, rates) : null;
        decimal? amount = l.OverrideCents is long o ? FromCents(o) : l.AmountCents is long a ? FromCents(a) : computed;
        var description = l.Description ?? (l.Entity is not null ? Billing.DescribeLine(l.Entity, l.Start, l.End, l.Quorum) : "");
        return new LineView(l.Id, l.Category, l.Entity, l.Date, l.Start, l.End, l.Quorum,
            l.OverrideCents is long ov ? FromCents(ov) : null, computed, amount, description);
    }

    public Draft GetDraft()
    {
        var id = DraftId();
        var rates = ListRates();
        var lines = Query("SELECT * FROM lines WHERE invoice_id = $1 ORDER BY date, start, id", ReadLine, id).Select(l => Present(l, rates)).ToList();
        return new Draft(id, lines, lines.Sum(l => l.Amount ?? 0m), lines.Count(l => l.Amount is null));
    }

    /// <summary>Une assemblée déjà facturée ou en cours (même entité, même date) n'est pas ajoutée deux fois.</summary>
    public bool IsKnown(string entity, string date) => Scalar("SELECT 1 FROM lines WHERE entity = $1 AND date = $2", entity, date) is not null;

    public long AddLine(string entity, string date, string? start = "", string? end = "", bool quorum = true)
    {
        if (string.IsNullOrWhiteSpace(entity)) throw new UserException("Le nom de l'entité juridique est requis.");
        if (!IsIsoDate(date)) throw new UserException("Date invalide.");
        return Insert("INSERT INTO lines (invoice_id, category, entity, date, start, end_time, quorum) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            DraftId(), CategorieVirtuelle, entity.Trim(), date, CheckTime(start, "Heure de début"), CheckTime(end, "Heure de fin"), quorum ? 1 : 0);
    }

    public PasteResult AddFromAgConnect(IEnumerable<AgConnectItem> items) => InTransaction(() =>
    {
        var added = new List<AgConnectItem>();
        var skipped = new List<(AgConnectItem, string)>();
        foreach (var it in items)
        {
            if (!it.Virtual) skipped.Add((it, "Assemblée avec adresse (hybride) : non gérée pour l'instant"));
            else if (IsKnown(it.Entity, it.Date)) skipped.Add((it, "Déjà présente"));
            else { AddLine(it.Entity, it.Date, it.Start); added.Add(it); }
        }
        return new PasteResult(added, skipped);
    });

    private RawLine EditableLine(long id)
    {
        var status = Scalar("SELECT i.status FROM lines l JOIN invoices i ON i.id = l.invoice_id WHERE l.id = $1", id) as string
            ?? throw new UserException("Ligne introuvable.");
        if (status != "draft") throw new UserException("Cette facture est finalisée : ses lignes ne peuvent plus être modifiées.");
        return Query("SELECT * FROM lines WHERE id = $1", ReadLine, id)[0];
    }

    /// <summary>Modifie une ligne de la facture en cours. Un paramètre null n'est pas modifié.</summary>
    public void UpdateLine(long id, string? entity = null, string? date = null, string? start = null, string? end = null,
        bool? quorum = null, decimal? overrideAmount = null, bool clearOverride = false)
    {
        var l = EditableLine(id);
        var e = entity?.Trim() ?? l.Entity;
        var d = date ?? l.Date;
        if (string.IsNullOrWhiteSpace(e)) throw new UserException("Le nom de l'entité juridique est requis.");
        if (!IsIsoDate(d)) throw new UserException("Date invalide.");
        if (overrideAmount is < 0) throw new UserException("Le montant ne peut pas être négatif.");
        long? ov = clearOverride ? null : overrideAmount is decimal o ? ToCents(o) : l.OverrideCents;
        Exec("UPDATE lines SET entity = $1, date = $2, start = $3, end_time = $4, quorum = $5, override_cents = $6 WHERE id = $7",
            e, d, start is null ? l.Start : CheckTime(start, "Heure de début"), end is null ? l.End : CheckTime(end, "Heure de fin"),
            (quorum ?? l.Quorum) ? 1 : 0, ov, id);
    }

    public void DeleteLine(long id)
    {
        EditableLine(id);
        Exec("DELETE FROM lines WHERE id = $1", id);
    }

    /// <summary>Fige la facture en cours : numéro, date, montants et descriptions définitifs.</summary>
    public long Finalize(string invoiceDate)
    {
        if (!IsIsoDate(invoiceDate)) throw new UserException("Date de facture invalide.");
        var draft = GetDraft();
        if (draft.Lines.Count == 0) throw new UserException("La facture en cours est vide.");
        if (draft.Incomplete > 0) throw new UserException($"{draft.Incomplete} assemblée(s) sans heure de fin : complétez-les avant de finaliser.");
        return InTransaction(() =>
        {
            var number = int.Parse(GetSettings()["next_number"], CultureInfo.InvariantCulture);
            if (Scalar("SELECT 1 FROM invoices WHERE number = $1", number) is not null)
                throw new UserException($"Le numéro {number} est déjà utilisé : corrigez le prochain numéro dans les réglages.");
            foreach (var l in draft.Lines)
                Exec("UPDATE lines SET amount_cents = $1, description = $2 WHERE id = $3", ToCents(l.Amount!.Value), l.Description, l.Id);
            Exec("UPDATE invoices SET status = 'final', number = $1, invoice_date = $2, finalized_at = $3, total_cents = $4 WHERE id = $5",
                number, invoiceDate, DateTime.UtcNow.ToString("o"), ToCents(draft.Total), draft.Id);
            Exec("UPDATE settings SET value = $1 WHERE key = 'next_number'", (number + 1).ToString(CultureInfo.InvariantCulture));
            return draft.Id;
        });
    }

    // ---------- Factures finalisées ----------
    private static InvoiceSummary ReadSummary(SqliteDataReader r) => new(
        (long)r["id"], (int)(long)r["number"],
        (string)r["status"] == "paid" ? InvoiceStatus.Paid : InvoiceStatus.Final,
        Str(r, "invoice_date") ?? "", Str(r, "paid_at"), Str(r, "payment_method"), FromCents(Long(r, "total_cents") ?? 0), (string)r["source"]);

    public List<InvoiceSummary> ListInvoices() =>
        Query("SELECT * FROM invoices WHERE status != 'draft' ORDER BY number DESC", ReadSummary);

    public InvoiceDetail GetInvoice(long id)
    {
        var inv = Query("SELECT * FROM invoices WHERE id = $1 AND status != 'draft'", ReadSummary, id).FirstOrDefault()
            ?? throw new UserException("Facture introuvable.");
        var rates = ListRates();
        var lines = Query("SELECT * FROM lines WHERE invoice_id = $1 ORDER BY id", ReadLine, id).Select(l => Present(l, rates)).ToList();
        return new InvoiceDetail(inv, lines);
    }

    /// <summary>La facture en cours sous forme de facture (numéro 0) pour l'aperçu PDF.</summary>
    public InvoiceDetail DraftAsInvoice()
    {
        var d = GetDraft();
        return new InvoiceDetail(new InvoiceSummary(d.Id, 0, InvoiceStatus.Draft, "", null, null, d.Total, "app"), d.Lines);
    }

    public void MarkPaid(long id, string paidAt, string? paymentMethod)
    {
        if (!IsIsoDate(paidAt)) throw new UserException("Date de paiement invalide.");
        if (Exec("UPDATE invoices SET status = 'paid', paid_at = $1, payment_method = $2 WHERE id = $3 AND status != 'draft'", paidAt, paymentMethod ?? "", id) == 0)
            throw new UserException("Facture introuvable.");
    }

    public void MarkUnpaid(long id) =>
        Exec("UPDATE invoices SET status = 'final', paid_at = NULL, payment_method = NULL WHERE id = $1 AND status = 'paid'", id);

    // ---------- État des résultats ----------
    public Report GetReport(int year)
    {
        var y = year.ToString(CultureInfo.InvariantCulture);
        var rows = Query("""
            SELECT l.category, SUM(l.amount_cents) cents, COUNT(*) n
            FROM lines l JOIN invoices i ON i.id = l.invoice_id
            WHERE i.status != 'draft' AND substr(i.invoice_date, 1, 4) = $1
            GROUP BY l.category ORDER BY cents DESC
            """, r => (Category: (string)r["category"], Cents: (long)r["cents"], N: (int)(long)r["n"]), y);
        var total = rows.Sum(r => r.Cents);
        var unpaid = (long)Scalar("SELECT COALESCE(SUM(total_cents), 0) FROM invoices WHERE status = 'final' AND substr(invoice_date, 1, 4) = $1", y)!;
        var years = Query("SELECT DISTINCT substr(invoice_date, 1, 4) y FROM invoices WHERE status != 'draft' ORDER BY y DESC",
            r => int.Parse((string)r["y"], CultureInfo.InvariantCulture));
        return new Report(year,
            rows.Select(r => new CategoryTotal(r.Category, FromCents(r.Cents), r.N, total == 0 ? 0 : (decimal)r.Cents / total)).ToList(),
            FromCents(total), FromCents(unpaid), years);
    }

    // ---------- Import de l'export JSON de Finance D ----------
    public ImportResult ImportFinanceD(string json)
    {
        JsonElement factures;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("facturesRevenus", out var f) || f.ValueKind != JsonValueKind.Object)
                throw new UserException("Ce fichier n'est pas un export JSON de Finance D (facturesRevenus manquant).");
            factures = f.Clone();
        }
        catch (JsonException) { throw new UserException("Ce fichier n'est pas un JSON valide."); }

        return InTransaction(() =>
        {
            int imported = 0, skipped = 0;
            foreach (var prop in factures.EnumerateObject())
            {
                var f = prop.Value;
                var num = f.TryGetProperty("numero", out var n) ? n.ToString() : "";
                var dateNum = f.TryGetProperty("date", out var dt) && dt.TryGetProperty("num", out var dn) ? dn.ToString() : "";
                if (!int.TryParse(num, out var number) || !Regex.IsMatch(dateNum, @"^\d{8}$")
                    || Scalar("SELECT 1 FROM invoices WHERE number = $1", number) is not null) { skipped++; continue; }

                var date = $"{dateNum[..4]}-{dateNum[4..6]}-{dateNum[6..8]}";
                var lines = new List<(long Cents, string Category, string Description)>();
                if (f.TryGetProperty("transactions", out var tr) && tr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tr.EnumerateArray())
                    {
                        var amount = FirstNumber(t, "total", "sousTotal", "montant");
                        lines.Add((ToCents(amount), Prop(t, "categorie") is { Length: > 0 } c ? c.Trim() : "Sans catégorie", (Prop(t, "description") ?? "").Trim()));
                    }
                }
                var total = lines.Sum(l => l.Cents);
                var solde = f.TryGetProperty("solde", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDecimal() : -1;
                var paid = solde == 0 && total > 0;
                var id = Insert("INSERT INTO invoices (number, status, invoice_date, paid_at, payment_method, total_cents, source) VALUES ($1, $2, $3, $4, $5, $6, 'finance-d')",
                    number, paid ? "paid" : "final", date, paid ? date : null, paid ? "Import Finance D" : null, total);
                foreach (var l in lines)
                    Exec("INSERT INTO lines (invoice_id, category, entity, date, quorum, amount_cents, description) VALUES ($1, $2, NULL, $3, 1, $4, $5)", id, l.Category, date, l.Cents, l.Description);
                imported++;
            }
            var max = Scalar("SELECT MAX(number) FROM invoices") is long m ? m : 0;
            if (int.Parse(GetSettings()["next_number"], CultureInfo.InvariantCulture) <= max)
                Exec("UPDATE settings SET value = $1 WHERE key = 'next_number'", (max + 1).ToString(CultureInfo.InvariantCulture));
            return new ImportResult(imported, skipped);
        });
    }

    private static string? Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    private static decimal FirstNumber(JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (Prop(e, name) is string s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d;
        }
        return 0m;
    }
}
