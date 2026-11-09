using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using HamulProto.Models;

namespace HamulProto;

public partial class LoginWindow : Window
{
    /// <summary>
    /// Se dispara cuando el usuario inicia sesión correctamente.
    /// Al mostrarse con Show() (no modal), este evento sustituye a DialogResult.
    /// </summary>
    public event EventHandler? LoginSucceeded;

    /// <summary>
    /// True si el login se completó correctamente. Se usa al arrancar la app
    /// para decidir si se abre el panel principal o se cierra todo.
    /// </summary>
    public bool LoginCompleted { get; private set; }

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

        // Activar la ventana al cargarse (sin Topmost: no debe tapar la app)
        this.Loaded += (s, e) => {
            this.Activate();
            this.Focus();
        };
    }

    // ==========================================
    // BOTONES DE LA VENTANA
    // ==========================================
    private void BtnCerrar_Click(object sender, RoutedEventArgs e)
    {
        // Cerrar sin autenticarse: la app sigue funcionando en modo invitado.
        this.Close();
    }

    private void BtnMinimizar_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        // En registro, Escape vuelve al login (salvo que se esté registrando).
        if (RegisterPanel.Visibility == Visibility.Visible)
        {
            if (RegisterSubmitBtn.IsEnabled)
                ShowRegisterView(false);
            return;
        }

        Close();
    }

    // ==========================================
    // LOGIN
    // ==========================================
    private async void LoginBtn_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        SuccessText.Visibility = Visibility.Collapsed;

        try
        {
            SetLoginLoading(true);

            var loginData = new LoginRequest
            {
                Email = EmailBox.Text,
                PasswordHash = PasswordBox.Password
            };
            var response = await ApiClient.PostAsync<LoginRequest, LoginResponse>("/api/auth/login", loginData);

            // Si el usuario cerró la ventana mientras se procesaba la petición,
            // no apliques el resultado (ni guardes la sesión).
            if (!IsLoaded) return;

            if (response.IsSuccess && response.Data is { Token.Length: > 0 } result)
            {
                // "Recordar correo": guardar o borrar según el estado del checkbox
                Config.Current.LastEmail = RememberCheck.IsChecked == true ? EmailBox.Text.Trim() : null;

                CompleteLogin(result.Token, result.Nickname, result.UserId, EmailBox.Text);
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
            SetLoginLoading(false);
        }
    }

    private void SetLoginLoading(bool loading)
    {
        // IsEnabled evita dobles clics; el estilo mantiene el degradado y
        // muestra "Cargando..." cuando Tag = "loading".
        LoginBtn.IsEnabled = !loading;
        LoginBtn.Tag = loading ? "loading" : null;
        RegisterBtn.IsEnabled = !loading;
    }

    // ==========================================
    // REGISTRO
    // ==========================================
    private void RegisterBtn_Click(object sender, RoutedEventArgs e)
    {
        ShowRegisterView(true);
    }

    private void BackToLogin_Click(object sender, RoutedEventArgs e)
    {
        ShowRegisterView(false);
    }

    private void ShowRegisterView(bool show)
    {
        ErrorText.Text = "";
        SuccessText.Visibility = Visibility.Collapsed;

        LoginPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        RegisterPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            if (string.IsNullOrWhiteSpace(RegisterEmailBox.Text))
                RegisterEmailBox.Text = EmailBox.Text.Trim();
            RegisterEmailBox.Focus();
        }
        else
        {
            EmailBox.Focus();
        }
    }

    private async void RegisterSubmitBtn_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        SuccessText.Visibility = Visibility.Collapsed;

        string email = RegisterEmailBox.Text.Trim();
        string nickname = RegisterNicknameBox.Text.Trim();
        string password = RegisterPasswordBox.Password;
        string confirm = RegisterConfirmBox.Password;

        if (string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(nickname) ||
            password.Length < 6)
        {
            ErrorText.Text = "Completa todos los campos. La contraseña debe tener al menos 6 caracteres.";
            return;
        }

        if (password != confirm)
        {
            ErrorText.Text = "Las contraseñas no coinciden.";
            return;
        }

        try
        {
            SetRegisterLoading(true);

            var request = new RegisterRequest
            {
                Email = email,
                Nickname = nickname,
                Password = password,
                // Compatibilidad con el backend antiguo, que solo leía PasswordHash.
                PasswordHash = password
            };

            var response = await ApiClient.PostAsync<RegisterRequest, RegisterResponse>("/api/auth/register", request);
            if (!IsLoaded) return;

            if (!response.IsSuccess || response.Data is null)
            {
                ErrorText.Text = response.Error ?? "No se pudo crear la cuenta.";
                return;
            }

            var result = response.Data;
            Config.Current.LastEmail = email;

            if (!string.IsNullOrWhiteSpace(result.Token))
            {
                // El backend devuelve un token al registrar: entrar directo.
                CompleteLogin(result.Token, result.Nickname, result.UserId, email);
                return;
            }

            // Backend antiguo sin token: volver al login con el correo cargado.
            EmailBox.Text = email;
            ShowRegisterView(false);
            SuccessText.Text = string.IsNullOrWhiteSpace(result.Message)
                ? "Cuenta creada. Ya puedes iniciar sesión."
                : result.Message;
            SuccessText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Error de conexión: " + ex.Message;
        }
        finally
        {
            SetRegisterLoading(false);
        }
    }

    private void SetRegisterLoading(bool loading)
    {
        RegisterSubmitBtn.IsEnabled = !loading;
        RegisterSubmitBtn.Tag = loading ? "loading" : null;
        BackToLoginBtn.IsEnabled = !loading;
    }

    // ==========================================
    // SESIÓN
    // ==========================================
    private void CompleteLogin(string token, string nickname, long userId, string email)
    {
        Config.Current.AuthToken = token;
        Config.Current.Nickname = nickname;
        Config.Current.Email = email.Trim();
        Config.Current.UserId = userId;
        ApiClient.SetToken(token);
        Config.Current.Save();
        Config.NotifySessionChanged();

        // No usar DialogResult: la ventana se muestra con Show(), no con ShowDialog().
        LoginCompleted = true;
        LoginSucceeded?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private sealed class LoginRequest
    {
        public string Email { get; set; } = "";
        public string PasswordHash { get; set; } = "";
    }

    private sealed class RegisterRequest
    {
        [JsonPropertyName("Email")]
        public string Email { get; set; } = "";

        [JsonPropertyName("Nickname")]
        public string Nickname { get; set; } = "";

        [JsonPropertyName("Password")]
        public string Password { get; set; } = "";

        // El backend desplegado en Render puede ser una versión anterior que
        // solo lee PasswordHash. Enviar ambos mantiene compatibilidad.
        [JsonPropertyName("PasswordHash")]
        public string PasswordHash { get; set; } = "";
    }
}
