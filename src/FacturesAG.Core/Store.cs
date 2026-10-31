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
        ["payment_terms"] = DefaultPaymentTerms,
        ["payment_days"] = "15",
        ["footer"] = "Merci de votre confiance.",
        ["next_number"] = "1",
        // Envoi par courriel (Gmail) : adresse d'envoi (vide = « Courriel » des coordonnées), objet et message.
        ["email_from"] = "",
        ["email_subject"] = InvoiceMail.DefaultSubject,
        ["email_body"] = InvoiceMail.DefaultBody,
    };

    public const string DefaultPaymentTerms = "Paiement par virement Interac, par chèque ou comptant.";

    /// <summary>Délais de paiement proposés (jours après la date de facture).</summary>
    public static readonly IReadOnlyList<int> PaymentDayChoices = [0, 15, 30];

    public static string PaymentDaysLabel(int days) => days == 0 ? "Net 0 (payable sur réception)" : $"Net {days} jours";

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
        // 0.3.0 : délai de paiement mémorisé par facture, réouverture d'une facture (copie pour annuler).
        new(2, """
            ALTER TABLE invoices ADD COLUMN payment_days INTEGER;
            ALTER TABLE invoices ADD COLUMN editing INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE invoices ADD COLUMN snapshot TEXT;
            UPDATE invoices SET payment_days = 15 WHERE source = 'app' AND status != 'draft';
            UPDATE settings SET value = 'Paiement par virement Interac, par chèque ou comptant.'
              WHERE key = 'payment_terms'
                AND value = 'Solde payable sur réception par virement Interac, par chèque ou comptant. Net 15 jours suivant la date de facture.';
            """),
        // 0.5.0 : date et destinataire du dernier envoi par courriel.
        new(3, """
            ALTER TABLE invoices ADD COLUMN sent_at TEXT;
            ALTER TABLE invoices ADD COLUMN sent_to TEXT;
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

    /// <summary>Copie complète et cohérente de la base ouverte vers <paramref name="file"/> (remplacé s'il existe).</summary>
    public void CopyDatabaseTo(string file)
    {
        var temp = file + ".copie";
        if (File.Exists(temp)) File.Delete(temp);
        using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
        {
            dest.Open();
            _db.BackupDatabase(dest);
        }
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>
    /// Sauvegarde quotidienne : une copie par jour (la première ouverture du jour), en gardant les
    /// <paramref name="keep"/> plus récentes. Retourne le fichier créé, ou null s'il y en avait déjà une aujourd'hui.
    /// </summary>
    public string? DailyBackup(int keep = 30)
    {
        if (_backupDir is null) return null;
        Directory.CreateDirectory(_backupDir);
        var daily = Directory.GetFiles(_backupDir, "factures-ag-*-quotidienne.db").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var today = $"factures-ag-{DateTime.Now:yyyyMMdd}-";
        string? created = null;
        if (!daily.Any(f => Path.GetFileName(f).StartsWith(today, StringComparison.Ordinal)))
        {
            created = Backup("quotidienne")!;
            daily.Add(created);
        }
        foreach (var old in daily.OrderBy(f => f, StringComparer.Ordinal).SkipLast(keep))
        {
            try { File.Delete(old); } catch (IOException) { }
        }
        return created;
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

    /// <summary>
    /// Enregistre les réglages : tout est validé d'abord, puis écrit d'un seul coup (tout ou rien).
    /// Seules les valeurs qui changent sont écrites.
    /// </summary>
    public void UpdateSettings(IDictionary<string, string> patch)
    {
        var changes = new List<(string Key, string Value)>();
        var current = GetSettings();
        foreach (var (k, raw) in patch)
        {
            if (!DefaultSettings.ContainsKey(k)) continue;
            var v = raw ?? "";
            if (k == "next_number" && !(int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n < 1_000_000_000))
                throw new UserException("Le prochain numéro de facture doit être un nombre entier positif.");
            if (k == "payment_days" && !(int.TryParse(v, out var days) && PaymentDayChoices.Contains(days)))
                throw new UserException("Délai de paiement invalide : choisissez Net 0, Net 15 ou Net 30.");
            if (!current.TryGetValue(k, out var old) || old != v) changes.Add((k, v));
        }
        InTransaction(() =>
        {
            foreach (var (k, v) in changes)
                Exec("INSERT INTO settings (key, value) VALUES ($1, $2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", k, v);
            return 0;
        });
    }

    /// <summary>Clés dont la valeur remplie serait effacée par <paramref name="patch"/> (pour demander confirmation).</summary>
    public List<string> SettingsThatWouldBeCleared(IDictionary<string, string> patch)
    {
        var current = GetSettings();
        return patch.Where(p => DefaultSettings.ContainsKey(p.Key) && string.IsNullOrWhiteSpace(p.Value)
                                && current.TryGetValue(p.Key, out var old) && !string.IsNullOrWhiteSpace(old))
                    .Select(p => p.Key).ToList();
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

    // ---------- Facture en cours (brouillon du mois ou facture rouverte) ----------
    private long DraftId() =>
        Scalar("SELECT id FROM invoices WHERE status = 'draft' ORDER BY id LIMIT 1") is long id
            ? id
            : Insert("INSERT INTO invoices (status) VALUES ('draft')");

    private long? EditingId() => Scalar("SELECT id FROM invoices WHERE editing = 1 ORDER BY id LIMIT 1") as long?;

    /// <summary>Facture que l'écran de saisie modifie : la facture rouverte s'il y en a une, sinon le brouillon du mois.</summary>
    private long WorkingId() => EditingId() ?? DraftId();

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

    private List<LineView> LinesOf(long invoiceId)
    {
        var rates = ListRates();
        return Query("SELECT * FROM lines WHERE invoice_id = $1 ORDER BY date, start, id", ReadLine, invoiceId).Select(l => Present(l, rates)).ToList();
    }

    public Draft GetDraft()
    {
        if (EditingId() is long editing)
        {
            var inv = Query("SELECT * FROM invoices WHERE id = $1", ReadSummary, editing)[0];
            var original = JsonDocument.Parse(Scalar("SELECT snapshot FROM invoices WHERE id = $1", editing) as string ?? "{}").RootElement;
            var originalTotal = original.TryGetProperty("total_cents", out var t) && t.ValueKind == JsonValueKind.Number ? FromCents(t.GetInt64()) : inv.Total;
            var lines = LinesOf(editing);
            return new Draft(editing, lines, lines.Sum(l => l.Amount ?? 0m), lines.Count(l => l.Amount is null),
                inv.Number, inv.InvoiceDate, inv.PaymentDays, IsReopened: true, WasPaid: inv.Status == InvoiceStatus.Paid, OriginalTotal: originalTotal);
        }
        var id = DraftId();
        var draftLines = LinesOf(id);
        return new Draft(id, draftLines, draftLines.Sum(l => l.Amount ?? 0m), draftLines.Count(l => l.Amount is null));
    }

    /// <summary>Une assemblée déjà facturée ou en cours (même entité, même date) n'est pas ajoutée deux fois.</summary>
    public bool IsKnown(string entity, string date) => Scalar("SELECT 1 FROM lines WHERE entity = $1 AND date = $2", entity, date) is not null;

    public long AddLine(string entity, string date, string? start = "", string? end = "", bool quorum = true)
    {
        if (string.IsNullOrWhiteSpace(entity)) throw new UserException("Le nom de l'entité juridique est requis.");
        if (!IsIsoDate(date)) throw new UserException("Date invalide.");
        return Insert("INSERT INTO lines (invoice_id, category, entity, date, start, end_time, quorum) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            WorkingId(), CategorieVirtuelle, entity.Trim(), date, CheckTime(start, "Heure de début"), CheckTime(end, "Heure de fin"), quorum ? 1 : 0);
    }

    /// <summary>Ligne libre : catégorie, description et montant saisis à la main (formation, per diem, kilométrage…).</summary>
    public long AddFreeLine(string category, string description, decimal amount, string date)
    {
        CheckFree(category, description, amount);
        if (!IsIsoDate(date)) throw new UserException("Date invalide.");
        return Insert("INSERT INTO lines (invoice_id, category, entity, date, start, end_time, quorum, amount_cents, description) VALUES ($1, $2, NULL, $3, '', '', 1, $4, $5)",
            WorkingId(), category.Trim(), date, ToCents(amount), description.Trim());
    }

    private static void CheckFree(string? category, string? description, decimal? amount)
    {
        if (category is not null && string.IsNullOrWhiteSpace(category)) throw new UserException("La catégorie est requise.");
        if (description is not null && string.IsNullOrWhiteSpace(description)) throw new UserException("La description est requise.");
        if (amount is < 0) throw new UserException("Le montant ne peut pas être négatif.");
    }

    /// <summary>Catégories déjà utilisées (pour proposer une liste lors de la saisie d'une ligne libre).</summary>
    public List<string> KnownCategories() =>
        Query("SELECT DISTINCT category FROM lines ORDER BY category", r => (string)r["category"])
            .Union([CategorieVirtuelle, "Formation", "Kilométrage", "Per Diem"]).Distinct().OrderBy(c => c, StringComparer.CurrentCulture).ToList();

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
        var row = Query("SELECT i.status, i.editing FROM lines l JOIN invoices i ON i.id = l.invoice_id WHERE l.id = $1",
            r => (Status: (string)r["status"], Editing: (long)r["editing"] != 0), id).FirstOrDefault();
        if (row.Status is null) throw new UserException("Ligne introuvable.");
        if (row.Status != "draft" && !row.Editing)
            throw new UserException("Cette facture est finalisée : rouvrez-la pour la modifier.");
        return Query("SELECT * FROM lines WHERE id = $1", ReadLine, id)[0];
    }

    /// <summary>
    /// Modifie une assemblée de la facture en cours. Un paramètre null n'est pas modifié.
    /// Une assemblée modifiée est recalculée ; une assemblée non touchée d'une facture rouverte garde son montant.
    /// </summary>
    public void UpdateLine(long id, string? entity = null, string? date = null, string? start = null, string? end = null,
        bool? quorum = null, decimal? overrideAmount = null, bool clearOverride = false)
    {
        var l = EditableLine(id);
        if (l.Entity is null) throw new UserException("Cette ligne est une ligne libre : modifiez sa description ou son montant.");
        var e = entity?.Trim() ?? l.Entity;
        var d = date ?? l.Date;
        if (string.IsNullOrWhiteSpace(e)) throw new UserException("Le nom de l'entité juridique est requis.");
        if (!IsIsoDate(d)) throw new UserException("Date invalide.");
        if (overrideAmount is < 0) throw new UserException("Le montant ne peut pas être négatif.");
        var s = start is null ? l.Start : CheckTime(start, "Heure de début");
        var en = end is null ? l.End : CheckTime(end, "Heure de fin");
        var q = quorum ?? l.Quorum;
        long? ov = clearOverride ? null : overrideAmount is decimal o ? ToCents(o) : l.OverrideCents;
        // Un changement qui influe sur le calcul (ou l'abandon d'un montant forcé) efface le montant figé : il sera recalculé.
        var recompute = clearOverride || e != l.Entity || d != l.Date || s != l.Start || en != l.End || q != l.Quorum;
        var describe = e != l.Entity || s != l.Start || en != l.End || q != l.Quorum;
        Exec("UPDATE lines SET entity = $1, date = $2, start = $3, end_time = $4, quorum = $5, override_cents = $6, amount_cents = $7, description = $8 WHERE id = $9",
            e, d, s, en, q ? 1 : 0, ov, recompute ? null : l.AmountCents, describe ? null : l.Description, id);
    }

    /// <summary>Modifie une ligne libre. Un paramètre null n'est pas modifié.</summary>
    public void UpdateFreeLine(long id, string? category = null, string? description = null, decimal? amount = null)
    {
        var l = EditableLine(id);
        if (l.Entity is not null) throw new UserException("Cette ligne est une assemblée : modifiez ses heures.");
        CheckFree(category, description, amount);
        Exec("UPDATE lines SET category = $1, description = $2, amount_cents = $3, override_cents = NULL WHERE id = $4",
            category?.Trim() ?? l.Category, description?.Trim() ?? l.Description,
            amount is decimal a ? ToCents(a) : l.OverrideCents ?? l.AmountCents, id);
    }

    public void DeleteLine(long id)
    {
        EditableLine(id);
        Exec("DELETE FROM lines WHERE id = $1", id);
    }

    private static int CheckPaymentDays(int days) =>
        PaymentDayChoices.Contains(days) ? days : throw new UserException("Délai de paiement invalide : choisissez Net 0, Net 15 ou Net 30.");

    private void CheckComplete(Draft d, string emptyMessage)
    {
        if (d.Lines.Count == 0) throw new UserException(emptyMessage);
        if (d.Incomplete > 0) throw new UserException($"{d.Incomplete} assemblée(s) sans heure de fin : complétez-les avant de continuer.");
    }

    private void FreezeLines(Draft d)
    {
        foreach (var l in d.Lines)
            Exec("UPDATE lines SET amount_cents = $1, description = $2 WHERE id = $3", ToCents(l.Amount!.Value), l.Description, l.Id);
    }

    /// <summary>Fige la facture du mois : numéro, date, délai de paiement, montants et descriptions définitifs.</summary>
    public long Finalize(string invoiceDate, int? paymentDays = null)
    {
        if (!IsIsoDate(invoiceDate)) throw new UserException("Date de facture invalide.");
        if (EditingId() is not null) throw new UserException("Une facture est en cours de modification : enregistrez ou annulez d'abord ses modifications.");
        var days = CheckPaymentDays(paymentDays ?? int.Parse(GetSettings()["payment_days"], CultureInfo.InvariantCulture));
        var draft = GetDraft();
        CheckComplete(draft, "La facture en cours est vide.");
        return InTransaction(() =>
        {
            var number = int.Parse(GetSettings()["next_number"], CultureInfo.InvariantCulture);
            if (Scalar("SELECT 1 FROM invoices WHERE number = $1", number) is not null)
                throw new UserException($"Le numéro {number} est déjà utilisé : corrigez le prochain numéro dans les réglages.");
            FreezeLines(draft);
            Exec("UPDATE invoices SET status = 'final', number = $1, invoice_date = $2, finalized_at = $3, total_cents = $4, payment_days = $5 WHERE id = $6",
                number, invoiceDate, DateTime.UtcNow.ToString("o"), ToCents(draft.Total), days, draft.Id);
            Exec("UPDATE settings SET value = $1 WHERE key = 'next_number'", (number + 1).ToString(CultureInfo.InvariantCulture));
            return draft.Id;
        });
    }

    // ---------- Réouverture d'une facture finalisée ----------
    /// <summary>Rouvre une facture pour la modifier. Une copie est gardée pour pouvoir annuler.</summary>
    public void Reopen(long id)
    {
        if (EditingId() is long other)
        {
            var n = Scalar("SELECT number FROM invoices WHERE id = $1", other);
            throw new UserException($"La facture n° {n} est déjà en cours de modification : enregistrez ou annulez d'abord ses modifications.");
        }
        var inv = Query("SELECT * FROM invoices WHERE id = $1 AND status != 'draft'", r => new
        {
            status = (string)r["status"], invoice_date = Str(r, "invoice_date"), paid_at = Str(r, "paid_at"),
            payment_method = Str(r, "payment_method"), total_cents = Long(r, "total_cents"), payment_days = Long(r, "payment_days"),
        }, id).FirstOrDefault() ?? throw new UserException("Facture introuvable.");
        var lines = Query("SELECT * FROM lines WHERE invoice_id = $1 ORDER BY id", r => new
        {
            id = (long)r["id"], category = (string)r["category"], entity = Str(r, "entity"), date = Str(r, "date"), start = Str(r, "start"),
            end_time = Str(r, "end_time"), quorum = (long)r["quorum"], override_cents = Long(r, "override_cents"),
            amount_cents = Long(r, "amount_cents"), description = Str(r, "description"),
        }, id);
        var snapshot = JsonSerializer.Serialize(new { inv.status, inv.invoice_date, inv.paid_at, inv.payment_method, inv.total_cents, inv.payment_days, lines });
        Exec("UPDATE invoices SET editing = 1, snapshot = $1 WHERE id = $2", snapshot, id);
    }

    /// <summary>
    /// Enregistre la facture rouverte (même numéro). Si elle était payée et que son total change,
    /// elle repasse « À payer ».
    /// </summary>
    public ReopenSaveResult SaveReopened(string invoiceDate, int paymentDays)
    {
        if (!IsIsoDate(invoiceDate)) throw new UserException("Date de facture invalide.");
        CheckPaymentDays(paymentDays);
        var d = GetDraft();
        if (!d.IsReopened) throw new UserException("Aucune facture n'est en cours de modification.");
        CheckComplete(d, "La facture est vide : ajoutez au moins une ligne.");
        var paymentReset = d.WasPaid && d.Total != d.OriginalTotal;
        InTransaction(() =>
        {
            FreezeLines(d);
            Exec("""
                UPDATE invoices SET invoice_date = $1, payment_days = $2, total_cents = $3, editing = 0, snapshot = NULL,
                  status = CASE WHEN $4 = 1 THEN 'final' ELSE status END,
                  paid_at = CASE WHEN $4 = 1 THEN NULL ELSE paid_at END,
                  payment_method = CASE WHEN $4 = 1 THEN NULL ELSE payment_method END
                WHERE id = $5
                """, invoiceDate, paymentDays, ToCents(d.Total), paymentReset ? 1 : 0, d.Id);
            return 0;
        });
        return new ReopenSaveResult(d.Id, d.Number!.Value, paymentReset);
    }

    /// <summary>Annule les modifications : la facture rouverte revient exactement comme avant.</summary>
    public void CancelReopen()
    {
        if (EditingId() is not long id) return;
        using var snap = JsonDocument.Parse(Scalar("SELECT snapshot FROM invoices WHERE id = $1", id) as string
            ?? throw new UserException("Copie de la facture introuvable : impossible d'annuler."));
        var s = snap.RootElement;
        static object? V(JsonElement e, string name) => e.GetProperty(name).ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number => e.GetProperty(name).GetInt64(),
            _ => e.GetProperty(name).GetString(),
        };
        InTransaction(() =>
        {
            Exec("DELETE FROM lines WHERE invoice_id = $1", id);
            foreach (var l in s.GetProperty("lines").EnumerateArray())
            {
                Exec("INSERT INTO lines (id, invoice_id, category, entity, date, start, end_time, quorum, override_cents, amount_cents, description) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)",
                    V(l, "id"), id, V(l, "category"), V(l, "entity"), V(l, "date"), V(l, "start"), V(l, "end_time"), V(l, "quorum"),
                    V(l, "override_cents"), V(l, "amount_cents"), V(l, "description"));
            }
            Exec("UPDATE invoices SET status = $1, invoice_date = $2, paid_at = $3, payment_method = $4, total_cents = $5, payment_days = $6, editing = 0, snapshot = NULL WHERE id = $7",
                V(s, "status"), V(s, "invoice_date"), V(s, "paid_at"), V(s, "payment_method"), V(s, "total_cents"), V(s, "payment_days"), id);
            return 0;
        });
    }

    // ---------- Factures finalisées ----------
    private static InvoiceSummary ReadSummary(SqliteDataReader r) => new(
        (long)r["id"], (int)(long)r["number"],
        (string)r["status"] == "paid" ? InvoiceStatus.Paid : InvoiceStatus.Final,
        Str(r, "invoice_date") ?? "", Str(r, "paid_at"), Str(r, "payment_method"), FromCents(Long(r, "total_cents") ?? 0), (string)r["source"],
        Long(r, "payment_days") is long d ? (int)d : null, (long)r["editing"] != 0, Str(r, "sent_at"), Str(r, "sent_to"));

    public List<InvoiceSummary> ListInvoices() =>
        Query("SELECT * FROM invoices WHERE status != 'draft' ORDER BY number DESC", ReadSummary);

    public InvoiceDetail GetInvoice(long id)
    {
        var inv = Query("SELECT * FROM invoices WHERE id = $1 AND status != 'draft'", ReadSummary, id).FirstOrDefault()
            ?? throw new UserException("Facture introuvable.");
        return new InvoiceDetail(inv, LinesOf(id));
    }

    /// <summary>La facture en cours sous forme de facture, pour l'aperçu PDF.</summary>
    public InvoiceDetail DraftAsInvoice(string? invoiceDate = null, int? paymentDays = null)
    {
        var d = GetDraft();
        var days = paymentDays ?? d.PaymentDays ?? int.Parse(GetSettings()["payment_days"], CultureInfo.InvariantCulture);
        var summary = d.IsReopened
            ? new InvoiceSummary(d.Id, d.Number!.Value, InvoiceStatus.Final, invoiceDate ?? d.InvoiceDate ?? "", null, null, d.Total, "app", days)
            : new InvoiceSummary(d.Id, 0, InvoiceStatus.Draft, "", null, null, d.Total, "app", days);
        return new InvoiceDetail(summary, d.Lines);
    }

    public void MarkPaid(long id, string paidAt, string? paymentMethod)
    {
        if (!IsIsoDate(paidAt)) throw new UserException("Date de paiement invalide.");
        if (Exec("UPDATE invoices SET status = 'paid', paid_at = $1, payment_method = $2 WHERE id = $3 AND status != 'draft'", paidAt, paymentMethod ?? "", id) == 0)
            throw new UserException("Facture introuvable.");
    }

    /// <summary>Note l'envoi par courriel (date et heure locales « aaaa-mm-jj hh:mm », destinataires).</summary>
    public void MarkSent(long id, string sentAt, string sentTo)
    {
        if (Exec("UPDATE invoices SET sent_at = $1, sent_to = $2 WHERE id = $3 AND status != 'draft'", sentAt, sentTo, id) == 0)
            throw new UserException("Facture introuvable.");
    }

    public void MarkUnpaid(long id) =>
        Exec("UPDATE invoices SET status = 'final', paid_at = NULL, payment_method = NULL WHERE id = $1 AND status = 'paid'", id);

    // ---------- État des résultats ----------
    public Report GetReport(int year)
    {
        var y = year.ToString(CultureInfo.InvariantCulture);
        var rows = Query("""
            SELECT l.category, SUM(COALESCE(l.amount_cents, l.override_cents, 0)) cents, COUNT(*) n
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

        Backup("avant-import");
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
