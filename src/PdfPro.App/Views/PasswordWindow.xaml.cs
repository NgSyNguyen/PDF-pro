using System.Globalization;
using System.Windows;
using PdfPro.App.Resources;
using Wpf.Ui.Controls;

namespace PdfPro.App.Views;

/// <summary>Asks for a document password. The password is never bound to a view model or kept after use.</summary>
public partial class PasswordWindow : FluentWindow
{
    public PasswordWindow(string fileName, bool isRetry)
    {
        InitializeComponent();
        MessageText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Password_Message, fileName);
        RetryInfo.IsOpen = isRetry;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    /// <summary>Returns the typed password and clears the input.</summary>
    public string TakePassword()
    {
        var password = PasswordInput.Password ?? string.Empty;
        PasswordInput.Password = string.Empty;
        return password;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
