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
        // Données dans Documents\Factures AG (modifiable avec la variable FACTURES_AG_DATA, utile pour les essais).
        _dataDir = Environment.GetEnvironmentVariable("FACTURES_AG_DATA")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Factures AG");
        _pdfDir = Path.Combine(_dataDir, "Factures");
        Directory.CreateDirectory(_pdfDir);
        // Une copie de la base est faite dans Sauvegardes avant chaque changement de structure.
        _store = new Store(Path.Combine(_dataDir, "factures-ag.db"), Path.Combine(_dataDir, "Sauvegardes"));
        Closed += (_, _) => _store.Dispose();

        LinesList.ItemsSource = _lines;
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        DataPath.Text = _dataDir;
        VersionText.Text = $"Version {UpdateFlow.CurrentVersion} · mises à jour automatiques et obligatoires";
        LoadDraft();

        // Si l'application reste ouverte longtemps, une nouvelle version est quand même installée.
        _updateTimer.Tick += async (_, _) => await CheckForUpdateWhileRunningAsync();
        _updateTimer.Start();
    }

    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(4) };

    private async Task CheckForUpdateWhileRunningAsync()
    {
        if (await UpdateFlow.CheckQuietlyAsync(TimeSpan.FromSeconds(30)) is not { } update) return;
        _updateTimer.Stop();
        // Enregistre la saisie en cours (les champs s'enregistrent en quittant la case).
        if (Keyboard.FocusedElement is TextBox box) box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        UpdateFlow.InstallAndRestart(update, this);
        Application.Current.Shutdown(); // redémarrage lancé, ou « Quitter » après un échec
    }

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

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || ViewDraft is null) return;
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
    private void LoadDraft()
    {
        var settings = _store.GetSettings();
        var draft = _store.GetDraft();
        _lines.Clear();
        foreach (var l in draft.Lines) _lines.Add(new LineRow(l, SaveLine));
        ClientNameRun.Text = settings["client_name"] is { Length: > 0 } client ? client : "client à définir dans les Réglages";
        FinalizeBtn.Content = $"Finaliser la facture n° {settings["next_number"]}";
        FinalizeDate.SelectedDate ??= DateTime.Today;
        RefreshDraftSummary(draft);
    }

    private void RefreshDraftSummary(Draft draft)
    {
        var first = draft.Lines.Select(l => Fmt.FromIso(l.Date)).FirstOrDefault(d => d is not null);
        DraftTitle.Text = Fmt.MonthTitle(first ?? DateTime.Today);
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
                    var text = row.OverrideText.Trim().Replace(',', '.').Replace("$", "").Replace(" ", "");
                    if (text.Length == 0) _store.UpdateLine(row.Id, clearOverride: true);
                    else if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) _store.UpdateLine(row.Id, overrideAmount: amount);
                    else throw new UserException("Montant invalide : écrivez par exemple 180 ou 180.00.");
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

    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        if (Dialogs.NewLine(this) is not var (entity, date, start)) return;
        try { _store.AddLine(entity, Fmt.Iso(date), start); LoadDraft(); Notify($"« {entity} » ajoutée. Saisissez l'heure de fin."); }
        catch (Exception ex) { Error(ex); }
    }

    private void DeleteLine_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not LineRow row || !Confirm($"Retirer « {row.Entity} » de la facture en cours ?")) return;
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
            File.WriteAllBytes(file, InvoicePdf.Generate(_store.DraftAsInvoice(), _store.GetSettings()));
            OpenFile(file);
        }
        catch (Exception ex) { Error(ex); }
    }

    private void Finalize_Click(object sender, RoutedEventArgs e)
    {
        // Laisse d'abord la cellule en cours de saisie s'enregistrer.
        FinalizeBtn.Focus();
        var draft = _store.GetDraft();
        var settings = _store.GetSettings();
        var date = FinalizeDate.SelectedDate ?? DateTime.Today;
        if (!Confirm($"Finaliser la facture n° {settings["next_number"]} datée du {date.ToString("d MMMM yyyy", Fmt.Fr)} pour {Fmt.Money(draft.Total)} ?\n\nElle ne pourra plus être modifiée.")) return;
        try
        {
            var id = _store.Finalize(Fmt.Iso(date));
            var file = WritePdf(_store.GetInvoice(id));
            FinalizeDate.SelectedDate = DateTime.Today;
            LoadDraft();
            if (Confirm($"Facture n° {_store.GetInvoice(id).Invoice.Number} finalisée.\nLe PDF est enregistré dans :\n{file}\n\nL'ouvrir maintenant ?")) OpenFile(file);
        }
        catch (Exception ex) { Error(ex); }
    }

    private string PdfPath(InvoiceDetail inv) => Path.Combine(_pdfDir, InvoicePdf.FileName(inv, _store.GetSettings()));

    private string WritePdf(InvoiceDetail inv)
    {
        var file = PdfPath(inv);
        File.WriteAllBytes(file, InvoicePdf.Generate(inv, _store.GetSettings()));
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
            var inv = _store.GetInvoice(row.Summary.Id);
            var file = PdfPath(inv);
            if (!File.Exists(file)) { WritePdf(inv); LoadInvoices(); }
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

    private void LoadSettings()
    {
        var s = _store.GetSettings();
        foreach (var box in SettingBoxes()) box.Text = s.GetValueOrDefault((string)box.Tag, "");
        RatesList.ItemsSource = _store.ListRates().Select(r => new RateRow(r)).ToList();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _store.UpdateSettings(SettingBoxes().ToDictionary(b => (string)b.Tag, b => b.Text.Trim()));
            Notify("Réglages enregistrés.");
        }
        catch (Exception ex) { Error(ex); }
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
            LoadSettings();
            Notify("Taux ajouté. Il s'applique aux assemblées à partir de cette date.");
        }
        catch (Exception ex) { Error(ex); }
    }

    private void DeleteRate_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not RateRow row || !Confirm($"Supprimer le taux en vigueur depuis le {row.From} ?\nLes factures déjà finalisées ne changent pas.")) return;
        try { _store.DeleteRate(row.Rate.Id); LoadSettings(); } catch (Exception ex) { Error(ex); }
    }

    private void ImportFinanceD_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Export Finance D (*.json)|*.json", Title = "Choisir l'export JSON de Finance D" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var r = _store.ImportFinanceD(File.ReadAllText(dlg.FileName));
            LoadSettings();
            Notify($"{r.Imported} facture(s) importée(s), {r.Skipped} ignorée(s).");
        }
        catch (Exception ex) { Error(ex); }
    }
}
