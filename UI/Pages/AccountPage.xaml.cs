using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace HamulProto.UI.Pages;

public partial class AccountPage : UserControl
{
    /// <summary>El usuario pidió cerrar la sesión desde la vista de cuenta.</summary>
    public event EventHandler? LogoutRequested;

    private bool _subscribed;

    public AccountPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (!_subscribed)
            {
                Config.SessionChanged += OnSessionChanged;
                _subscribed = true;
            }

            Refresh();
        };

        Unloaded += (_, _) =>
        {
            if (_subscribed)
            {
                Config.SessionChanged -= OnSessionChanged;
                _subscribed = false;
            }
        };
    }

    /// <summary>Vuelve a leer la sesión y actualiza todos los campos visibles.</summary>
    public void Refresh()
    {
        bool logged = !string.IsNullOrWhiteSpace(Config.Current.AuthToken);

        string nickname = logged ? "Usuario" : "Invitado";
        string email = logged ? "No disponible" : "Sin sesión";

        if (logged)
        {
            if (!string.IsNullOrWhiteSpace(Config.Current.Nickname))
                nickname = Config.Current.Nickname.Trim();

            var savedEmail = Config.Current.Email;
            if (string.IsNullOrWhiteSpace(savedEmail))
                savedEmail = Config.Current.LastEmail;
            if (!string.IsNullOrWhiteSpace(savedEmail))
                email = savedEmail;
        }

        NicknameText.Text = nickname;
        NicknameInfoText.Text = nickname;
        EmailText.Text = email;
        EmailInfoText.Text = email;
        AvatarText.Text = nickname[..1].ToUpperInvariant();

        StatusText.Text = logged ? "Sesión activa" : "Sin sesión";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty,
            logged ? "AccentOrange" : "AccentRed");

        LogoutBtn.Visibility = logged ? Visibility.Visible : Visibility.Collapsed;
        LoggedOutHint.Visibility = logged ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(new Action(Refresh)); }
            catch { /* la ventana ya se está cerrando */ }
            return;
        }

        Refresh();
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        LogoutRequested?.Invoke(this, EventArgs.Empty);
    }
}
