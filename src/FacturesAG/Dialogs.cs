using System.Windows;
using System.Windows.Controls;

namespace FacturesAG;

/// <summary>Petites fenêtres de saisie construites en code.</summary>
public static class Dialogs
{
    private static (Window Win, StackPanel Body) Frame(Window owner, string title)
    {
        var body = new StackPanel { Margin = new Thickness(20) };
        var win = new Window
        {
            Title = title, Owner = owner, Content = body, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, MinWidth = 380,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["Surface"],
        };
        return (win, body);
    }

    private static UIElement Field(string label, FrameworkElement input)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        p.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["FieldLabel"] });
        p.Children.Add(input);
        return p;
    }

    private static void Buttons(Window win, StackPanel body, string okLabel, Func<bool> onOk)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = new Button { Content = "Annuler", Style = (Style)Application.Current.Resources["Ghost"], IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = okLabel, Style = (Style)Application.Current.Resources["Primary"], IsDefault = true };
        ok.Click += (_, _) => { if (onOk()) win.DialogResult = true; };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        body.Children.Add(row);
    }

    /// <summary>Nouvelle assemblée : entité, date, heure de début.</summary>
    public static (string Entity, DateTime Date, string Start)? NewLine(Window owner)
    {
        var (win, body) = Frame(owner, "Ajouter une assemblée");
        var entity = new TextBox { Width = 340 };
        var date = new DatePicker { SelectedDate = DateTime.Today };
        var start = new TextBox { Text = "18:30", Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(Field("Entité juridique (ex. : SDC Les Jardins)", entity));
        body.Children.Add(Field("Date", date));
        body.Children.Add(Field("Heure de début", start));
        var error = new TextBlock { Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Danger"], Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(error);
        Buttons(win, body, "Ajouter", () =>
        {
            if (string.IsNullOrWhiteSpace(entity.Text)) { error.Text = "Entrez le nom de l'entité juridique."; return false; }
            if (date.SelectedDate is null) { error.Text = "Choisissez une date."; return false; }
            return true;
        });
        win.Loaded += (_, _) => entity.Focus();
        return win.ShowDialog() == true ? (entity.Text.Trim(), date.SelectedDate!.Value, start.Text) : null;
    }

    /// <summary>Ligne libre : catégorie, description, montant et date.</summary>
    public static (string Category, string Description, decimal Amount, DateTime Date)? FreeLine(Window owner, IEnumerable<string> categories)
    {
        var (win, body) = Frame(owner, "Ajouter une ligne libre");
        var category = new ComboBox { IsEditable = true, ItemsSource = categories.ToList(), Text = "Formation", Width = 340, Padding = new Thickness(6, 5, 6, 5) };
        var description = new TextBox { Width = 340 };
        var amount = new TextBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var date = new DatePicker { SelectedDate = DateTime.Today };
        body.Children.Add(Field("Catégorie (choisissez ou tapez-en une nouvelle)", category));
        body.Children.Add(Field("Description (ex. : 1,5 heure de formation)", description));
        body.Children.Add(Field("Montant ($)", amount));
        body.Children.Add(Field("Date", date));
        var error = new TextBlock { Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Danger"], Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(error);
        decimal value = 0;
        Buttons(win, body, "Ajouter", () =>
        {
            var t = amount.Text.Trim().Replace(',', '.').Replace("$", "").Replace(" ", "");
            if (string.IsNullOrWhiteSpace(category.Text)) { error.Text = "Choisissez une catégorie."; return false; }
            if (string.IsNullOrWhiteSpace(description.Text)) { error.Text = "Entrez une description."; return false; }
            if (!decimal.TryParse(t, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out value) || value < 0)
            { error.Text = "Entrez un montant valide, par exemple 60 ou 60.00."; return false; }
            if (date.SelectedDate is null) { error.Text = "Choisissez une date."; return false; }
            return true;
        });
        win.Loaded += (_, _) => description.Focus();
        return win.ShowDialog() == true ? (category.Text.Trim(), description.Text.Trim(), value, date.SelectedDate!.Value) : null;
    }

    /// <summary>Paiement reçu : date et mode.</summary>
    public static (DateTime Date, string Method)? Payment(Window owner, string invoiceLabel)
    {
        var (win, body) = Frame(owner, $"Paiement · {invoiceLabel}");
        var date = new DatePicker { SelectedDate = DateTime.Today };
        var method = new ComboBox { ItemsSource = new[] { "Virement Interac", "Chèque", "Comptant", "Dépôt direct" }, SelectedIndex = 0, Padding = new Thickness(8, 5, 8, 5) };
        body.Children.Add(Field("Date du paiement", date));
        body.Children.Add(Field("Mode de paiement", method));
        Buttons(win, body, "Enregistrer", () => date.SelectedDate is not null);
        return win.ShowDialog() == true ? (date.SelectedDate!.Value, (string)method.SelectedItem) : null;
    }

    /// <summary>Courriel de la facture, à relire avant l'envoi : destinataire, objet, message (modifiables).</summary>
    public static (string To, string Subject, string Body)? SendInvoice(Window owner, string invoiceLabel, string from,
        string to, string subject, string message, string attachmentName, Action openPdf)
    {
        var (win, body) = Frame(owner, $"Envoyer · {invoiceLabel}");
        var toBox = new TextBox { Text = to, Width = 520 };
        var subjectBox = new TextBox { Text = subject, Width = 520 };
        var messageBox = new TextBox { Text = message, Width = 520, Height = 200, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
        body.Children.Add(Field("De", new TextBlock { Text = from }));
        body.Children.Add(Field("À (plusieurs adresses : séparez-les par « ; »)", toBox));
        body.Children.Add(Field("Objet", subjectBox));
        body.Children.Add(Field("Message", messageBox));
        var attachment = new StackPanel { Orientation = Orientation.Horizontal };
        attachment.Children.Add(new TextBlock { Text = $"📎 {attachmentName}", VerticalAlignment = VerticalAlignment.Center });
        var view = new Button { Content = "Voir le PDF", Style = (Style)Application.Current.Resources["Ghost"], Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        view.Click += (_, _) => openPdf();
        attachment.Children.Add(view);
        body.Children.Add(Field("Pièce jointe", attachment));
        var error = new TextBlock { Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Danger"], Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(error);
        Buttons(win, body, "Envoyer", () =>
        {
            if (string.IsNullOrWhiteSpace(toBox.Text)) { error.Text = "Entrez l'adresse courriel du destinataire."; return false; }
            if (string.IsNullOrWhiteSpace(subjectBox.Text)) { error.Text = "Entrez un objet."; return false; }
            return true;
        });
        win.Loaded += (_, _) => (string.IsNullOrWhiteSpace(to) ? toBox : messageBox).Focus();
        return win.ShowDialog() == true ? (toBox.Text.Trim(), subjectBox.Text.Trim(), messageBox.Text) : null;
    }
}
