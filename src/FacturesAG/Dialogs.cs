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
}
