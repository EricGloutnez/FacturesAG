using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FacturesAG.Core;
using Microsoft.Win32;

namespace FacturesAG;

public partial class MainWindow : Window
{
    private readonly string _dataDir;
    private readonly string _pdfDir;
    private readonly Store _store;
    private readonly ObservableCollection<LineRow> _lines = [];
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    public MainWindow()
    {
        InitializeComponent();
        // Données dans AppData\Local\Factures AG : dossier personnel que Windows Defender (« Accès contrôlé aux dossiers »)
        // ne bloque jamais, contrairement à Documents ou au Bureau. Modifiable avec FACTURES_AG_DATA (essais).
        _dataDir = AppRuntime.DataDir;
        _pdfDir = Path.Combine(_dataDir, "Factures");
        Title = AppRuntime.WindowTitle;
        TestBanner.Visibility = AppRuntime.IsTestData ? Visibility.Visible : Visibility.Collapsed;
        TestBannerText.Text = $"DONNÉES D'ESSAI — ce ne sont pas vos vraies factures ({_dataDir})";
        try { Directory.CreateDirectory(_pdfDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UserException($"Impossible de créer le dossier des données :\n{_pdfDir}\n\nWindows bloque peut-être l'accès à ce dossier (protection « Accès contrôlé aux dossiers » de Windows Defender).\n\nDétail : {e.Message}");
        }
        // Une copie de la base est faite dans Sauvegardes avant chaque changement de structure.
        _store = new Store(Path.Combine(_dataDir, "factures-ag.db"), Path.Combine(_dataDir, "Sauvegardes"));
        // Et une copie chaque jour (les 30 derniers jours sont gardés).
        try { _store.DailyBackup(); }
        catch (Exception e) { Dispatcher.BeginInvoke(() => Notify($"Sauvegarde du jour impossible : {e.Message}")); }
        // En fermant, la case en cours de saisie est d'abord enregistrée.
        Closing += (_, _) => CommitPendingEdit();
        Closed += (_, _) => _store.Dispose();
        PreviewKeyDown += (_, _) => _lastInput = DateTime.Now;
        PreviewMouseDown += (_, _) => _lastInput = DateTime.Now;

        LinesList.ItemsSource = _lines;
        DataContext = this;
        PaymentDaysBox.ItemsSource = PaymentChoices;
        SettingsPaymentDays.ItemsSource = PaymentChoices;
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        DataPath.Text = _dataDir;
        VersionText.Text = $"Version {UpdateFlow.CurrentVersion} · mises à jour automatiques et obligatoires";
        LoadDraft();

        // Si l'application reste ouverte longtemps, une nouvelle version est quand même installée,
        // mais jamais au milieu d'une saisie (voir TryApplyPendingUpdate).
        _updateTimer.Tick += async (_, _) => await CheckForUpdateWhileRunningAsync();
        _updateTimer.Start();
        _pendingUpdateTimer.Tick += (_, _) => TryApplyPendingUpdate();
    }

    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(4) };
    private readonly DispatcherTimer _pendingUpdateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private UpdateInfo? _pendingUpdate;
    private DateTime _lastInput = DateTime.Now;

    public sealed record PaymentChoice(int Days, string Label);

    public static IReadOnlyList<PaymentChoice> PaymentChoices { get; } =
        Store.PaymentDayChoices.Select(d => new PaymentChoice(d, Store.PaymentDaysLabel(d))).ToList();

    /// <summary>Catégories proposées pour les lignes libres.</summary>
    public ObservableCollection<string> Categories { get; } = [];

    private async Task CheckForUpdateWhileRunningAsync()
    {
        if (_pendingUpdate is not null || await UpdateFlow.CheckQuietlyAsync(TimeSpan.FromSeconds(30)) is not { } update) return;
        _pendingUpdate = update;
        UpdateBannerText.Text = $"La version {update.Version} est prête. Elle s'installera dès que vous ne serez plus en train de saisir (l'application redémarrera).";
        UpdateBanner.Visibility = Visibility.Visible;
        _pendingUpdateTimer.Start();
    }

    /// <summary>
    /// Installe la mise à jour en attente seulement à un moment sûr : aucune fenêtre ouverte, aucun réglage
    /// non enregistré, rien de collé en attente, et 2 minutes sans saisie.
    /// </summary>
    private void TryApplyPendingUpdate(bool now = false)
    {
        if (_pendingUpdate is null) return;
        var busy = OwnedWindows.Count > 0 || IsSettingsDirty() || !string.IsNullOrWhiteSpace(PasteText.Text)
                   || DateTime.Now - _lastInput < TimeSpan.FromMinutes(2);
        if (busy && !now) return;
        if (now && IsSettingsDirty() && !Confirm("Des réglages ne sont pas enregistrés et seraient perdus. Continuer la mise à jour ?")) return;
        _pendingUpdateTimer.Stop();
        CommitPendingEdit();
        UpdateFlow.InstallAndRestart(_pendingUpdate, this);
        Application.Current.Shutdown(); // redémarrage lancé, ou « Quitter » après un échec
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e) => TryApplyPendingUpdate(now: true);

    // ---------- Outils ----------
    private void Notify(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void CloseToast_Click(object sender, RoutedEventArgs e) => Toast.Visibility = Visibility.Collapsed;

    private void Error(Exception e) =>
        MessageBox.Show(this, e is UserException ? e.Message : $"Erreur inattendue : {e.Message}", "Factures AG", MessageBoxButton.OK,
            e is UserException ? MessageBoxImage.Warning : MessageBoxImage.Error);

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, "Factures AG", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private TabItem? _currentTab;
    private bool _switchingBack;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || ViewDraft is null || _switchingBack) return;
        // Quitter les Réglages avec des modifications non enregistrées : on demande, on ne perd rien en silence.
        if (_currentTab == TabSettings && !TabSettings.IsSelected && IsSettingsDirty())
        {
            var answer = MessageBox.Show(this, "Des réglages ont été modifiés sans être enregistrés.\n\nLes enregistrer maintenant ?",
                "Factures AG", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !SaveSettings()))
            {
                _switchingBack = true;
                TabSettings.IsSelected = true;
                _switchingBack = false;
                return;
            }
            _settingsSnapshot = null; // « Non » : modifications abandonnées volontairement
        }
        _currentTab = Tabs.SelectedItem as TabItem;
        ViewDraft.Visibility = TabDraft.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        ViewInvoices.Visibility = TabInvoices.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        ViewReport.Visibility = TabReport.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        ViewSettings.Visibility = TabSettings.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            if (TabDraft.IsSelected) LoadDraft();
            else if (TabInvoices.IsSelected) LoadInvoices();
            else if (TabReport.IsSelected) LoadReport(null);
            else LoadSettings();
        }
        catch (Exception ex) { Error(ex); }
    }

    // ---------- Facture en cours ----------
    /// <summary>Id de la facture affichée la dernière fois : sert à réinitialiser date et délai quand on change de facture.</summary>
    private long? _shownInvoiceId;

    private void LoadDraft()
    {
        var settings = _store.GetSettings();
        var draft = _store.GetDraft();
        _lines.Clear();
        foreach (var l in draft.Lines) _lines.Add(new LineRow(l, SaveLine));
        Categories.Clear();
        foreach (var c in _store.KnownCategories()) Categories.Add(c);
        ClientNameRun.Text = settings["client_name"] is { Length: > 0 } client ? client : "client à définir dans les Réglages";

        var reopened = draft.IsReopened;
        DraftEyebrow.Text = reopened ? "Facture rouverte · " : "Facture en cours · ";
        DraftBadgeText.Text = reopened ? "Modification" : "Brouillon";
        DraftBadge.Background = (System.Windows.Media.Brush)FindResource(reopened ? "AccentSoft" : "WarnBg");
        DraftBadgeText.Foreground = (System.Windows.Media.Brush)FindResource(reopened ? "Accent" : "Warn");
        ReopenBanner.Visibility = reopened ? Visibility.Visible : Visibility.Collapsed;
        ReopenBannerText.Text = reopened
            ? $"Vous modifiez la facture n° {draft.Number} (son numéro ne change pas). Votre facture du mois est mise de côté et reviendra intacte après l'enregistrement ou l'annulation."
              + (draft.WasPaid ? " Cette facture est payée : si son total change, elle repassera « À payer »." : "")
            : "";
        FinalizeBtn.Visibility = reopened ? Visibility.Collapsed : Visibility.Visible;
        SaveReopenBtn.Visibility = CancelReopenBtn.Visibility = reopened ? Visibility.Visible : Visibility.Collapsed;
        FinalizeBtn.Content = $"Finaliser la facture n° {settings["next_number"]}";

        // Date et délai : ceux de la facture rouverte, sinon aujourd'hui et le délai par défaut (sans écraser un choix en cours).
        if (_shownInvoiceId != draft.Id)
        {
            _shownInvoiceId = draft.Id;
            FinalizeDate.SelectedDate = reopened ? Fmt.FromIso(draft.InvoiceDate) ?? DateTime.Today : DateTime.Today;
            PaymentDaysBox.SelectedValue = draft.PaymentDays ?? int.Parse(settings["payment_days"], CultureInfo.InvariantCulture);
        }
        RefreshDraftSummary(draft);
    }

    private int SelectedPaymentDays =>
        PaymentDaysBox.SelectedValue is int d ? d : int.Parse(_store.GetSettings()["payment_days"], CultureInfo.InvariantCulture);

    private void RefreshDraftSummary(Draft draft)
    {
        var first = draft.Lines.Select(l => Fmt.FromIso(l.Date)).FirstOrDefault(d => d is not null);
        DraftTitle.Text = draft.IsReopened ? $"Modification de la facture n° {draft.Number}" : Fmt.MonthTitle(first ?? DateTime.Today);
        DraftEmpty.Visibility = draft.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DraftTotal.Text = Fmt.Money(draft.Total);
        var rate = _store.ListRates().LastOrDefault();
        RateHelp.Text = rate is null ? "Aucun taux défini : ajoutez-en un dans les réglages."
            : $"Grille : {Fmt.Money(rate.HourlyRate)} par heure commencée · minimum {rate.MinHours:0.#} h · TPS/TVQ non applicables";
        DraftWarning.Visibility = draft.Incomplete > 0 ? Visibility.Visible : Visibility.Collapsed;
        DraftWarningText.Text = $"{draft.Incomplete} assemblée(s) sans heure de fin : complétez-les pour finaliser.";
    }

    /// <summary>Enregistre le champ modifié, puis met à jour les lignes sur place (sans perdre le focus).</summary>
    private void SaveLine(LineRow row, string field)
    {
        try
        {
            switch (field)
            {
                case nameof(LineRow.Entity): _store.UpdateLine(row.Id, entity: row.Entity); break;
                case nameof(LineRow.Date): _store.UpdateLine(row.Id, date: Fmt.Iso(row.Date!.Value)); break;
                case nameof(LineRow.Start): _store.UpdateLine(row.Id, start: row.Start); break;
                case nameof(LineRow.End): _store.UpdateLine(row.Id, end: row.End); break;
                case nameof(LineRow.Quorum): _store.UpdateLine(row.Id, quorum: row.Quorum); break;
                case nameof(LineRow.OverrideText):
                    if (ParseAmount(row.OverrideText) is decimal forced) _store.UpdateLine(row.Id, overrideAmount: forced);
                    else _store.UpdateLine(row.Id, clearOverride: true);
                    break;
                case nameof(LineRow.Category): _store.UpdateFreeLine(row.Id, category: row.Category); break;
                case nameof(LineRow.Description): _store.UpdateFreeLine(row.Id, description: row.Description); break;
                case nameof(LineRow.AmountText):
                    _store.UpdateFreeLine(row.Id, amount: ParseAmount(row.AmountText) ?? throw new UserException("Entrez le montant de la ligne."));
                    break;
            }
        }
        catch (Exception e) { Error(e); }

        // Recharge depuis la base : valeurs normalisées (ex. « 2034 » devient « 20:34 ») ou annulation d'une saisie refusée.
        var draft = _store.GetDraft();
        foreach (var r in _lines)
        {
            var v = draft.Lines.FirstOrDefault(l => l.Id == r.Id);
            if (v is not null) Dispatcher.BeginInvoke(() => r.Apply(v));
        }
        RefreshDraftSummary(draft);
    }

    /// <summary>« 180 », « 180,00 », « 180.00 $ » → 180 ; vide → null ; invalide → erreur.</summary>
    private static decimal? ParseAmount(string? text)
    {
        var t = (text ?? "").Trim().Replace(',', '.').Replace("$", "").Replace(" ", "").Replace(" ", "");
        if (t.Length == 0) return null;
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? amount : throw new UserException("Montant invalide : écrivez par exemple 180 ou 180.00.");
    }

    private void AddFreeLine_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.FreeLine(this, Categories) is not var (category, description, amount, date)) return;
        try { _store.AddFreeLine(category, description, amount, Fmt.Iso(date)); LoadDraft(); }
        catch (Exception ex) { Error(ex); }
    }

    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.NewLine(this) is not var (entity, date, start)) return;
        try { _store.AddLine(entity, Fmt.Iso(date), start); LoadDraft(); Notify($"« {entity} » ajoutée. Saisissez l'heure de fin."); }
        catch (Exception ex) { Error(ex); }
    }

    private void DeleteLine_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not LineRow row || !Confirm($"Retirer « {(row.IsFree ? row.Description : row.Entity)} » de la facture ?")) return;
        try { _store.DeleteLine(row.Id); LoadDraft(); } catch (Exception ex) { Error(ex); }
    }

    private void OpenPaste_Click(object sender, RoutedEventArgs e)
    {
        PastePanel.Visibility = Visibility.Visible;
        if (string.IsNullOrWhiteSpace(PasteText.Text) && Clipboard.ContainsText() && Clipboard.GetText().Contains("participants"))
            PasteText.Text = Clipboard.GetText();
        PasteText.Focus();
    }

    private void ClosePaste_Click(object sender, RoutedEventArgs e)
    {
        PastePanel.Visibility = Visibility.Collapsed;
        PasteText.Text = "";
        PasteList.ItemsSource = null;
        PasteAddBtn.Visibility = Visibility.Collapsed;
    }

    private void ReadPaste_Click(object sender, RoutedEventArgs e)
    {
        var items = AgConnectParser.Parse(PasteText.Text);
        if (items.Count == 0)
        {
            Error(new UserException("Aucune assemblée reconnue. Copiez toute la page « Mes invitations » (Ctrl+A puis Ctrl+C) et collez-la ici."));
            return;
        }
        PasteList.ItemsSource = items.Select(it => new PasteRow(it, _store.IsKnown(it.Entity, it.Date))).ToList();
        PasteAddBtn.Visibility = Visibility.Visible;
    }

    private void AddPaste_Click(object sender, RoutedEventArgs e)
    {
        var chosen = ((IEnumerable<PasteRow>)PasteList.ItemsSource).Where(r => r.Selected && r.CanAdd).Select(r => r.Item).ToList();
        if (chosen.Count == 0) { Notify("Aucune assemblée sélectionnée."); return; }
        try
        {
            var r = _store.AddFromAgConnect(chosen);
            ClosePaste_Click(sender, e);
            LoadDraft();
            Notify($"{r.Added.Count} assemblée(s) ajoutée(s). Saisissez l'heure de fin de chacune.");
        }
        catch (Exception ex) { Error(ex); }
    }

    private void PreviewDraft_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = Path.Combine(Path.GetTempPath(), "Factures AG - apercu.pdf");
            var date = FinalizeDate.SelectedDate is DateTime d ? Fmt.Iso(d) : null;
            File.WriteAllBytes(file, InvoicePdf.Generate(_store.DraftAsInvoice(date, SelectedPaymentDays), _store.GetSettings()));
            OpenFile(file);
        }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>Enregistre la cellule en cours de saisie (les champs s'enregistrent en quittant la case).</summary>
    private static void CommitPendingEdit()
    {
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void Finalize_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdit();
        var draft = _store.GetDraft();
        var settings = _store.GetSettings();
        var date = FinalizeDate.SelectedDate ?? DateTime.Today;
        var days = SelectedPaymentDays;
        if (!Confirm($"Finaliser la facture n° {settings["next_number"]} datée du {date.ToString("d MMMM yyyy", Fmt.Fr)} pour {Fmt.Money(draft.Total)} ({Store.PaymentDaysLabel(days)}) ?\n\nVous pourrez la rouvrir plus tard depuis l'onglet Factures si besoin.")) return;
        long id;
        try { id = _store.Finalize(Fmt.Iso(date), days); }
        catch (Exception ex) { Error(ex); return; }
        // La facture est enregistrée : l'écran est rafraîchi même si le PDF échoue ensuite.
        LoadDraft();
        var number = _store.GetInvoice(id).Invoice.Number;
        if (TryWritePdf(_store.GetInvoice(id), keepPrevious: false, out var file)
            && Confirm($"Facture n° {number} finalisée.\nLe PDF est enregistré dans :\n{file}\n\nL'ouvrir maintenant ?")) OpenFile(file);
    }

    /// <summary>Crée le PDF ; en cas d'échec, explique que la facture est bien enregistrée et comment recréer le PDF.</summary>
    private bool TryWritePdf(InvoiceDetail inv, bool keepPrevious, out string file)
    {
        try { file = WritePdf(inv, keepPrevious); return true; }
        catch (Exception ex)
        {
            file = "";
            Error(new UserException($"La facture n° {inv.Invoice.Number} est bien enregistrée, mais son PDF n'a pas pu être créé :\n{ex.Message}\n\n" +
                                    "Fermez le PDF s'il est ouvert dans un autre programme, puis cliquez sur « PDF » dans l'onglet Factures."));
            return false;
        }
    }

    private void SaveReopen_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingEdit();
        var draft = _store.GetDraft();
        if (!draft.IsReopened) { LoadDraft(); return; }
        var date = FinalizeDate.SelectedDate ?? DateTime.Today;
        var days = SelectedPaymentDays;
        var change = draft.Total == draft.OriginalTotal ? "" : $"\nTotal : {Fmt.Money(draft.OriginalTotal)} → {Fmt.Money(draft.Total)}";
        if (!Confirm($"Enregistrer les modifications de la facture n° {draft.Number} ?{change}\n\nSon PDF sera recréé ; l'ancien est gardé dans « Versions remplacées »." +
                     (draft.WasPaid && change.Length > 0 ? "\n\nComme son total change, elle repassera « À payer »." : ""))) return;
        ReopenSaveResult r;
        try { r = _store.SaveReopened(Fmt.Iso(date), days); }
        catch (Exception ex) { Error(ex); return; }
        // Les modifications sont enregistrées : l'écran revient à la facture du mois même si le PDF échoue.
        LoadDraft();
        var paid = r.PaymentReset ? "\n\nSon total a changé : elle est de nouveau « À payer »." : "";
        if (TryWritePdf(_store.GetInvoice(r.Id), keepPrevious: true, out var file)
            && Confirm($"Facture n° {r.Number} enregistrée.{paid}\n\nLe nouveau PDF est enregistré dans :\n{file}\n\nL'ouvrir maintenant ?")) OpenFile(file);
        else if (paid.Length > 0) Notify(paid.Trim());
    }

    private void CancelReopen_Click(object sender, RoutedEventArgs e)
    {
        var draft = _store.GetDraft();
        if (draft.IsReopened && !Confirm($"Annuler toutes les modifications de la facture n° {draft.Number} ?\nElle reviendra exactement comme avant.")) return;
        try { _store.CancelReopen(); LoadDraft(); Notify("Modifications annulées. Votre facture du mois est de retour."); }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>
    /// Fichier PDF de la facture : celui qui existe déjà pour ce numéro (même si le nom du client a changé depuis),
    /// sinon le nom habituel.
    /// </summary>
    private string PdfPath(InvoiceDetail inv)
    {
        var n = inv.Invoice.Number.ToString(CultureInfo.InvariantCulture);
        var existing = Directory.Exists(_pdfDir)
            ? Directory.GetFiles(_pdfDir, $"Facture-{n}*.pdf").FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f) is var name && (name == $"Facture-{n}" || name.StartsWith($"Facture-{n}_", StringComparison.Ordinal)))
            : null;
        return existing ?? Path.Combine(_pdfDir, InvoicePdf.FileName(inv, _store.GetSettings()));
    }

    /// <summary>
    /// Écrit le PDF : il est d'abord créé dans un fichier temporaire, puis mis en place, pour ne jamais laisser
    /// un PDF à moitié écrit. Avec <paramref name="keepPrevious"/>, l'ancien est gardé dans « Versions remplacées ».
    /// </summary>
    private string WritePdf(InvoiceDetail inv, bool keepPrevious = false)
    {
        var file = PdfPath(inv);
        var temp = file + ".tmp";
        File.WriteAllBytes(temp, InvoicePdf.Generate(inv, _store.GetSettings()));
        try
        {
            if (keepPrevious && File.Exists(file))
            {
                var archive = Path.Combine(_pdfDir, "Versions remplacées");
                Directory.CreateDirectory(archive);
                var name = $"{Path.GetFileNameWithoutExtension(file)} (remplacée le {DateTime.Now:yyyy-MM-dd HH'h'mm'm'ss}).pdf";
                File.Copy(file, Path.Combine(archive, name), overwrite: true);
            }
            File.Move(temp, file, overwrite: true);
        }
        catch (IOException) when (File.Exists(file))
        {
            throw new UserException($"Le fichier « {Path.GetFileName(file)} » est ouvert dans un autre programme (lecteur PDF).");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
        return file;
    }

    // ---------- Factures ----------
    private void LoadInvoices()
    {
        var rows = _store.ListInvoices().Select(s => new InvoiceRow(s, File.Exists(PdfPath(_store.GetInvoice(s.Id))))).ToList();
        InvoicesGrid.ItemsSource = rows;
        InvoicesGrid.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InvoicesEmpty.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void InvoicePdf_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not InvoiceRow row) return;
        try
        {
            // Le PDF est recréé à chaque ouverture : il correspond toujours à la facture enregistrée.
            var inv = _store.GetInvoice(row.Summary.Id);
            var file = WritePdf(inv);
            if (!row.PdfLabel.Equals("PDF", StringComparison.Ordinal)) LoadInvoices();
            OpenFile(file);
        }
        catch (Exception ex) { Error(ex); }
    }

    private void InvoicePaid_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not InvoiceRow row) return;
        try
        {
            if (row.IsPaid)
            {
                if (!Confirm($"Remettre la facture n° {row.Number} « À payer » ?")) return;
                _store.MarkUnpaid(row.Summary.Id);
            }
            else
            {
                if (Dialogs.Payment(this, $"facture n° {row.Number}") is not var (date, method)) return;
                _store.MarkPaid(row.Summary.Id, Fmt.Iso(date), method);
                Notify($"Paiement de la facture n° {row.Number} enregistré.");
            }
            LoadInvoices();
        }
        catch (Exception ex) { Error(ex); }
    }

    private void InvoiceReopen_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not InvoiceRow row) return;
        try
        {
            if (!row.IsEditing)
            {
                var paid = row.IsPaid ? "\n\nElle est payée : si son total change, elle repassera « À payer »." : "";
                if (!Confirm($"Rouvrir la facture n° {row.Number} pour la modifier ?\n\nSon numéro ne change pas. Votre facture du mois est mise de côté le temps de la modification.{paid}")) return;
                _store.Reopen(row.Summary.Id);
            }
            TabDraft.IsSelected = true; // affiche la facture rouverte dans l'écran de saisie
        }
        catch (Exception ex) { Error(ex); }
    }

    private void OpenPdfFolder_Click(object sender, RoutedEventArgs e) => OpenFile(_pdfDir);
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFile(_dataDir);

    // ---------- État des résultats ----------
    private bool _loadingReport;

    private void LoadReport(int? year)
    {
        _loadingReport = true;
        try
        {
            var r = _store.GetReport(year ?? DateTime.Today.Year);
            var years = r.Years.Append(DateTime.Today.Year).Append(r.Year).Distinct().OrderDescending().ToList();
            ReportYear.ItemsSource = years;
            ReportYear.SelectedItem = r.Year;
            ReportTotal.Text = Fmt.Money(r.Total);
            ReportUnpaid.Text = Fmt.Money(r.Unpaid);
            var rows = r.Categories.Select(c => new ReportRow(c.Category, (c.Share * 100).ToString("0.0", Fmt.Fr) + " %", Fmt.Money(c.Total))).ToList();
            if (rows.Count > 0) rows.Add(new ReportRow("Total des revenus", "", Fmt.Money(r.Total), IsTotal: true));
            ReportGrid.ItemsSource = rows;
        }
        finally { _loadingReport = false; }
    }

    private void ReportYear_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingReport || ReportYear.SelectedItem is not int y) return;
        try { LoadReport(y); } catch (Exception ex) { Error(ex); }
    }

    // ---------- Réglages ----------
    private IEnumerable<TextBox> SettingBoxes() => FindChildren<TextBox>(SettingsForm).Where(t => t.Tag is string);

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var c in FindChildren<T>(child)) yield return c;
        }
    }

    private static readonly Dictionary<string, string> SettingLabels = new()
    {
        ["issuer_name"] = "Votre nom", ["issuer_phone"] = "Votre téléphone", ["issuer_email"] = "Votre courriel",
        ["issuer_street"] = "Votre adresse", ["issuer_city"] = "Votre ville", ["client_name"] = "Nom du client",
        ["client_email"] = "Courriel du client", ["client_street"] = "Adresse du client", ["client_city"] = "Ville du client",
        ["payment_terms"] = "Modes de paiement", ["footer"] = "Message de bas de page", ["next_number"] = "Prochain numéro",
    };

    /// <summary>Valeurs affichées au chargement des Réglages ; null quand le formulaire n'est pas chargé.</summary>
    private Dictionary<string, string>? _settingsSnapshot;

    private Dictionary<string, string> SettingsFormValues()
    {
        var values = SettingBoxes().ToDictionary(b => (string)b.Tag, b => b.Text.Trim());
        if (SettingsPaymentDays.SelectedValue is int days) values["payment_days"] = days.ToString(CultureInfo.InvariantCulture);
        return values;
    }

    private bool IsSettingsDirty() =>
        _settingsSnapshot is not null && SettingsFormValues().Any(kv => _settingsSnapshot.GetValueOrDefault(kv.Key, "") != kv.Value);

    private void LoadSettings()
    {
        var s = _store.GetSettings();
        foreach (var box in SettingBoxes()) box.Text = s.GetValueOrDefault((string)box.Tag, "");
        SettingsPaymentDays.SelectedValue = int.Parse(s["payment_days"], CultureInfo.InvariantCulture);
        RefreshRates();
        _settingsSnapshot = SettingsFormValues();
    }

    private void RefreshRates() => RatesList.ItemsSource = _store.ListRates().Select(r => new RateRow(r)).ToList();

    private void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveSettings();

    /// <summary>Enregistre le formulaire (tout ou rien). Demande confirmation avant d'effacer un champ rempli.</summary>
    private bool SaveSettings()
    {
        try
        {
            var values = SettingsFormValues();
            var cleared = _store.SettingsThatWouldBeCleared(values);
            if (cleared.Count > 0 && !Confirm("Ces informations seront effacées :\n\n• " +
                    string.Join("\n• ", cleared.Select(k => SettingLabels.GetValueOrDefault(k, k))) + "\n\nContinuer ?"))
                return false;
            _store.UpdateSettings(values);
            _settingsSnapshot = SettingsFormValues();
            _shownInvoiceId = null; // la facture du mois reprendra le nouveau délai par défaut
            Notify("Réglages enregistrés.");
            return true;
        }
        catch (Exception ex) { Error(ex); return false; }
    }

    private void AddRate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RateFrom.SelectedDate is not DateTime from) throw new UserException("Choisissez la date d'entrée en vigueur.");
            if (!decimal.TryParse(RateHourly.Text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var hourly)) throw new UserException("Taux horaire invalide.");
            if (!decimal.TryParse(RateMin.Text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var min)) throw new UserException("Minimum d'heures invalide.");
            _store.AddRate(Fmt.Iso(from), hourly, min);
            RateHourly.Text = "";
            RefreshRates(); // seulement la grille : les réglages en cours de saisie restent intacts
            Notify("Taux ajouté. Il s'applique aux assemblées à partir de cette date.");
        }
        catch (Exception ex) { Error(ex); }
    }

    private void DeleteRate_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not RateRow row || !Confirm($"Supprimer le taux en vigueur depuis le {row.From} ?\nLes factures déjà finalisées ne changent pas.")) return;
        try { _store.DeleteRate(row.Rate.Id); RefreshRates(); } catch (Exception ex) { Error(ex); }
    }

    private void ImportFinanceD_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Export Finance D (*.json)|*.json", Title = "Choisir l'export JSON de Finance D" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var r = _store.ImportFinanceD(File.ReadAllText(dlg.FileName));
            // L'import peut avancer le prochain numéro : on ne met à jour que ce champ.
            var next = _store.GetSettings()["next_number"];
            if (SettingBoxes().FirstOrDefault(b => (string)b.Tag == "next_number") is { } box) box.Text = next;
            if (_settingsSnapshot is not null) _settingsSnapshot["next_number"] = next;
            Notify($"{r.Imported} facture(s) importée(s), {r.Skipped} ignorée(s). Une copie de sécurité a été faite avant l'import.");
        }
        catch (Exception ex) { Error(ex); }
    }
}
