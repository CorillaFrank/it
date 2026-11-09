using System.Windows;
using HamulProto.Models;

namespace HamulProto;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();

        // "Recordar correo": precargar el último email usado
        var saved = Config.Current;
        if (!string.IsNullOrEmpty(saved.LastEmail))
        {
            EmailBox.Text = saved.LastEmail;
            RememberCheck.IsChecked = true;
            PasswordBox.Focus(); // el email ya está: ir directo a la contraseña
        }

        // Forzar que la ventana se active y se vea al cargarse
        this.Loaded += (s, e) => {
            this.Activate();
            this.Focus();
        };
    }
    private async void LoginBtn_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        LoginBtn.IsEnabled = false;

        try
        {
            var loginData = new LoginRequest
            {
                Email = EmailBox.Text,
                PasswordHash = PasswordBox.Password
            };
            var response = await ApiClient.PostAsync<LoginRequest, LoginResponse>("/api/auth/login", loginData);

            if (response.IsSuccess && response.Data is { Token.Length: > 0 } result)
            {
                Config.Current.AuthToken = result.Token;
                Config.Current.Nickname = result.Nickname;
                Config.Current.UserId = result.UserId;
                ApiClient.SetToken(result.Token);

                // "Recordar correo": guardar o borrar según el estado del checkbox
                Config.Current.LastEmail = RememberCheck.IsChecked == true ? EmailBox.Text.Trim() : null;

                Config.Current.Save();

                this.DialogResult = true;
                this.Close();
            }
            else
            {
                ErrorText.Text = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Email o contraseña incorrectos."
                    : response.Error ?? "No se pudo iniciar sesión.";
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Error de conexión: " + ex.Message;
        }
        finally
        {
            LoginBtn.IsEnabled = true;
        }
    }

    private void RegisterBtn_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "Registro próximamente. Contacta al admin.";
    }

    private sealed class LoginRequest
    {
        public string Email { get; set; } = "";
        public string PasswordHash { get; set; } = "";
    }
}