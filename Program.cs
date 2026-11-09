using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using HamulProto;
using NAudio.CoreAudioApi;

// ---------------------------------------------------------------
// Hamul - Programa principal
// ---------------------------------------------------------------

// --- Una sola instancia (evita cambiar el micrófono dos veces) ---
var mutex = new Mutex(true, "Hamul-SingleInstance", out bool isFirst);
bool owned = isFirst;
if (!isFirst)
{
    try { owned = mutex.WaitOne(TimeSpan.FromSeconds(4)); }
    catch (AbandonedMutexException) { owned = true; }
}
if (!owned)
{
    System.Windows.MessageBox.Show("Hamul ya está abierto. Búscalo junto al reloj de Windows.", "Hamul");
    return;
}


// ---------------------------------------------------------------------------
// Login obligatorio al iniciar.
// Se muestra ANTES de crear la bandeja, el audio o la rueda: si el usuario
// cierra la ventana sin autenticarse, el proceso termina por completo.
// ---------------------------------------------------------------------------
bool loginSucceeded = false;
Exception? loginError = null;

var loginThread = new Thread(() =>
{
    try
    {
        var login = new HamulProto.LoginWindow
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
            Topmost = false,
            ShowInTaskbar = true
        };

        login.ShowDialog();
        loginSucceeded = login.LoginCompleted;
    }
    catch (Exception ex)
    {
        loginError = ex;
    }
});
loginThread.SetApartmentState(ApartmentState.STA);
loginThread.IsBackground = true;
loginThread.Start();
loginThread.Join();

if (loginError != null)
{
    System.Windows.MessageBox.Show("Error al abrir el login:\n" + loginError.Message, "Hamul");
    return;
}

if (!loginSucceeded)
{
    // Cerró el login (X, Escape o Alt+F4) sin autenticarse:
    // no dejar la app en segundo plano en la bandeja.
    return;
}

// --- Registro de errores ---
string logPath = Path.Combine(Config.Dir, "hamul.log");
void Log(string text)
{
    try
    {
        Directory.CreateDirectory(Config.Dir);
        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
    }
    catch { }
}

const int Rate = 48000;
var enumerator = new MMDeviceEnumerator();
bool noSwitch = args.Contains("--no-switch");

// --- Recuperación: si la vez anterior se cerró mal, restaurar el micrófono original
if (Config.Current.OriginalMicId != null)
{
    PolicyConfig.SetDefaultCapture(Config.Current.OriginalMicId);
    Config.Current.OriginalMicId = null;
    Config.Current.Save();
}

// --- Detectar VB-Cable
var allCapture = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
var allRender = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

bool IsCable(MMDevice d) => d.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase);

var cableMic = allCapture.FirstOrDefault(d => d.FriendlyName.Contains("CABLE Output") && !d.FriendlyName.Contains("16ch"));
var cableIn = allRender.FirstOrDefault(d => d.FriendlyName.Contains("CABLE Input") && !d.FriendlyName.Contains("16ch"));

if (cableMic == null || cableIn == null)
{
    var r = System.Windows.MessageBox.Show(
        "No encontré VB-Cable, el micrófono virtual que necesita Hamul.\n\n" +
        "Si acabas de instalarlo, pulsa 'Sí' para intentar detectarlo sin reiniciar el PC.\n" +
        "Si no lo tienes instalado, pulsa 'No' para ir a la página de descarga.",
        "Hamul", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);

    if (r == System.Windows.MessageBoxResult.Yes)
    {
        Native.RestartAudioService();
        System.Threading.Thread.Sleep(3000);
        System.Diagnostics.Process.Start(Environment.ProcessPath!);
        Environment.Exit(0);
    }
    else
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
    }
    return;
}

// --- Dispositivos
MMDevice? ById(string? id)
{
    if (id == null) return null;
    try { var d = enumerator.GetDevice(id); return d.State == DeviceState.Active ? d : null; }
    catch { return null; }
}

string? DefaultId(DataFlow flow)
{
    try { return enumerator.GetDefaultAudioEndpoint(flow, Role.Console).ID; } catch { return null; }
}

MMDevice? PickDefault(List<MMDevice> list, DataFlow flow)
{
    var id = DefaultId(flow);
    return list.FirstOrDefault(d => d.ID == id) ?? list.FirstOrDefault();
}

var realMics = allCapture.Where(d => !IsCable(d)).ToList();
var realOuts = allRender.Where(d => !IsCable(d)).ToList();

var mic = ById(Config.Current.MicId) ?? PickDefault(realMics, DataFlow.Capture);
var monitor = ById(Config.Current.MonitorId) ?? PickDefault(realOuts, DataFlow.Render);
if (mic == null || monitor == null)
{
    System.Windows.MessageBox.Show("No encontré un micrófono o una salida de audio activos.", "Hamul");
    return;
}
Config.Current.MicId = mic.ID;
Config.Current.MonitorId = monitor.ID;
Config.Current.Save();

// --- Sonidos por usuario ---
// Cada cuenta tiene su propia carpeta. Una cuenta nueva empieza vacía:
// no se copian sonidos de ejemplo ni de otras cuentas.
var soundsDir = Config.Current.UserSoundsDir;
Directory.CreateDirectory(soundsDir);

// --- Sectores de la rueda (8 fijos). Se guardan en slots.json desde el panel de control.
const int SlotCount = 8;
string slotsPath = Path.Combine(Config.Current.UserDir, "slots.json");
var slotFiles = new string?[SlotCount];
var soundCache = new Dictionary<string, CachedSound>(StringComparer.OrdinalIgnoreCase);
var sounds = new List<CachedSound>();
var names = new List<string>();
DateTime slotsStamp = DateTime.MinValue;

// Lee slots.json. Devuelve false si no pudo leerlo (en ese caso no cambia nada).
bool LoadSlotFiles()
{
    var next = new string?[SlotCount];

    if (File.Exists(slotsPath))
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(slotsPath);
            var saved = JsonSerializer.Deserialize<string?[]>(File.ReadAllText(slotsPath));
            if (saved != null)
            {
                for (int i = 0; i < SlotCount && i < saved.Length; i++)
                    next[i] = saved[i] != null && File.Exists(saved[i]) ? saved[i] : null;
            }
            slotsStamp = stamp;
        }
        catch (IOException) { return false; } // el panel lo está guardando: reintentar luego
        catch (Exception ex)
        {
            Log("slots.json: " + ex.Message);
            try { slotsStamp = File.GetLastWriteTimeUtc(slotsPath); } catch { }
            return false;
        }
    }
    else
    {
        // Sin slots.json: la rueda empieza vacía. El usuario asigna sus propios
        // sonidos desde el panel; no se autoasigna ni se cargan ejemplos.
    }

    for (int i = 0; i < SlotCount; i++) slotFiles[i] = next[i];
    return true;
}

// Carga en memoria el audio de cada sector (usa caché para no recargar lo que no cambió)
void RebuildSounds()
{
    var newSounds = new List<CachedSound>();
    var newNames = new List<string>();

    for (int i = 0; i < SlotCount; i++)
    {
        string? f = slotFiles[i];
        CachedSound? cs = null;

        if (f != null && !soundCache.TryGetValue(f, out cs))
        {
            try
            {
                cs = new CachedSound(f, Rate);
                soundCache[f] = cs;
            }
            catch (Exception ex)
            {
                Log($"No pude cargar {f}: {ex.Message}");
                cs = null;
                slotFiles[i] = null;
            }
        }

        newSounds.Add(cs ?? CachedSound.Empty);
        newNames.Add(slotFiles[i] != null ? Path.GetFileNameWithoutExtension(slotFiles[i]!) : "");
    }

    sounds = newSounds;
    names = newNames;
}

LoadSlotFiles();
RebuildSounds();

// --- Motor de audio
var engine = new AudioEngine();
engine.SetSounds(sounds);
try
{
    engine.Start(mic, cableIn, monitor);
}
catch (Exception ex)
{
    Log("No pude iniciar el audio: " + ex);
    System.Windows.MessageBox.Show("No pude iniciar el audio:\n" + ex.Message, "Hamul");
    return;
}

// --- Micrófono predeterminado
bool switched = false;
string? originalMicId = DefaultId(DataFlow.Capture);
if (originalMicId == null || originalMicId == cableMic.ID) originalMicId = mic.ID;

void Restore()
{
    if (!switched) return;
    switched = false;
    if (originalMicId != null) PolicyConfig.SetDefaultCapture(originalMicId);
    Config.Current.OriginalMicId = null;
    Config.Current.Save();
}

if (!noSwitch)
{
    Config.Current.OriginalMicId = originalMicId;
    Config.Current.Save();
    switched = PolicyConfig.SetDefaultCapture(cableMic.ID);
    if (!switched) { Config.Current.OriginalMicId = null; Config.Current.Save(); }
}

// --- Pulsar-para-hablar automático de Dota 2
bool pttDown = false;
int pendingSound = -1;
DateTime pendingSince = DateTime.MinValue;
DateTime pendingAt = DateTime.MinValue;
DateTime lastActive = DateTime.MinValue;

// Aviso a la rueda cuando cambian los nombres (lo asigna el hilo de la interfaz)
Action<List<string>>? onNamesChanged = null;

// Si el panel de control cambió los sectores, recargar sonidos y nombres
void SyncSlots()
{
    try
    {
        if (!File.Exists(slotsPath)) return;
        if (File.GetLastWriteTimeUtc(slotsPath) == slotsStamp) return;
    }
    catch { return; }

    if (!LoadSlotFiles()) return;

    pendingSound = -1;
    try
    {
        engine.StopAll();
        RebuildSounds();
        engine.SetSounds(sounds);
        onNamesChanged?.Invoke(names);
    }
    catch (Exception ex) { Log("No pude recargar los sectores: " + ex); }
}

int PttVk()
{
    var k = (Config.Current.PttKey ?? "G").Trim().ToUpperInvariant();
    return k.Length == 1 && char.IsLetterOrDigit(k[0]) ? k[0] : 0x47;
}

bool DotaInForeground() => Native.ForegroundTitle().Contains("Dota 2", StringComparison.OrdinalIgnoreCase);

void PttPress()
{
    if (pttDown) return;
    Native.SendKey(PttVk(), true);
    pttDown = true;
}

void ReleasePtt()
{
    if (!pttDown) return;
    Native.SendKey(PttVk(), false);
    pttDown = false;
}

void UpdatePtt()
{
    var now = DateTime.UtcNow;

    if (pendingSound >= 0)
    {
        lastActive = now;
        if (!pttDown)
        {
            bool modsUp = !Native.Down(0x11) && !Native.Down(0x10) && !Native.Down(0x12);
            if (modsUp || (now - pendingSince).TotalMilliseconds > 800)
            {
                PttPress();
                pendingAt = now.AddMilliseconds(150);
            }
        }
        else if (now >= pendingAt)
        {
            engine.Play(pendingSound);
            pendingSound = -1;
        }
    }
    else if (engine.IsPlaying)
    {
        lastActive = now;
    }

    if (pttDown && pendingSound < 0 && (now - lastActive).TotalMilliseconds > 350)
        ReleasePtt();
}

AppDomain.CurrentDomain.ProcessExit += (_, _) => { ReleasePtt(); Restore(); };
AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log("Error no controlado: " + e.ExceptionObject); ReleasePtt(); Restore(); };
Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => { ReleasePtt(); Restore(); };

void Play(int n)
{
    SyncSlots();
    if (n < 0 || n >= sounds.Count || slotFiles[n] == null) return; // sector vacío: no suena
    engine.StopAll();

    if (Config.Current.PttEnabled && DotaInForeground())
    {
        pendingSound = n;
        pendingSince = DateTime.UtcNow;
        lastActive = DateTime.UtcNow;
    }
    else
    {
        pendingSound = -1;
        engine.Play(n);
    }
}

void StopAll()
{
    pendingSound = -1;
    engine.StopAll();
}

// --- Icono en la bandeja
var trayIcon = new System.Windows.Forms.NotifyIcon
{
    Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
    Visible = false,
    Text = "Hamul - Rueda de Sonidos"
};

const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

bool IsAutostart()
{
    try
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return k?.GetValue("Hamul") != null;
    }
    catch { return false; }
}

void SetAutostart(bool on)
{
    try
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (k == null) return;
        if (on) k.SetValue("Hamul", $"\"{Environment.ProcessPath}\" --startup");
        else k.DeleteValue("Hamul", false);
    }
    catch (Exception ex) { Log("Inicio con Windows: " + ex.Message); }
}

void ExitApp()
{
    Config.Current.Save();
    trayIcon.Visible = false;
    ReleasePtt();
    Restore();
    engine.Stop();
    trayIcon.Dispose();
    System.Windows.Forms.Application.Exit();
    Environment.Exit(0);
}

void RestartApp()
{
    Config.Current.Save();
    trayIcon.Visible = false;
    ReleasePtt();
    Restore();
    engine.Stop();
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
    Environment.Exit(0);
}

// Se asigna cuando arranca el hilo de interfaz WPF. El menú de la bandeja
// y el panel lo usan para abrir el login sin bloquear la aplicación.
Action? showLogin = null;

int settingsOpen = 0;
void OpenSettings()
{
    if (Interlocked.Exchange(ref settingsOpen, 1) != 0) return;

    var thread = new Thread(() =>
    {
        try
        {
            var win = new DashboardWindow();
            win.LoginRequested += (_, _) => showLogin?.Invoke();
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            Log("Dashboard: " + ex);
            System.Windows.MessageBox.Show("No pude abrir el panel:\n" + ex.Message, "Hamul");
        }
        finally
        {
            Interlocked.Exchange(ref settingsOpen, 0);
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
}

var contextMenu = new System.Windows.Forms.ContextMenuStrip();
contextMenu.Items.Add("Ajustes (sonidos y dispositivos)", null, (s, e) => OpenSettings());

var autoItem = new System.Windows.Forms.ToolStripMenuItem("Iniciar con Windows") { CheckOnClick = true, Checked = IsAutostart() };
autoItem.CheckedChanged += (s, e) => SetAutostart(autoItem.Checked);
contextMenu.Items.Add(autoItem);

contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
contextMenu.Items.Add("Salir (restaurar micrófono)", null, (s, e) => ExitApp());

trayIcon.ContextMenuStrip = contextMenu;
trayIcon.DoubleClick += (s, e) => OpenSettings();
trayIcon.Visible = true;

// --- Rueda overlay
var wpfReady = new ManualResetEventSlim();

var uiThread = new Thread(() =>
{
    var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
    app.DispatcherUnhandledException += (_, e) => { Log("WPF: " + e.Exception); e.Handled = true; };

    LoginWindow? loginWindow = null;

    // Abre el login SIN bloquear (Show, no ShowDialog). Si ya está abierto,
    // simplemente lo trae al frente. Se usa para reautenticar (401 o Account).
    void OpenLogin()
    {
        // La ventana de login solo debe abrirse si NO hay sesión iniciada.
        if (!string.IsNullOrWhiteSpace(Config.Current.AuthToken))
            return;

        if (loginWindow != null)
        {
            loginWindow.WindowState = System.Windows.WindowState.Normal;
            loginWindow.Activate();
            return;
        }

        var login = new LoginWindow
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
            Topmost = false,
            ShowInTaskbar = true
        };

        login.Closed += (_, _) => loginWindow = null;
        login.LoginSucceeded += (_, _) =>
        {
            loginWindow = null;
            OpenSettings();
        };

        loginWindow = login;
        login.Show();      // No bloquea: la app sigue funcionando en modo invitado
        login.Activate();
    }

    // El menú de la bandeja y el panel viven en otros hilos; marshal al hilo WPF.
    showLogin = () =>
    {
        if (app.Dispatcher.CheckAccess())
            OpenLogin();
        else
            app.Dispatcher.BeginInvoke(new Action(OpenLogin));
    };

    // 401: la sesión expiró. ApiClient ya limpió el token; pedimos login
    // de forma no bloqueante (sin MessageBox y sin ShowDialog).
    ApiClient.AuthenticationRequired += (_, _) =>
    {
        if (app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
            return;

        app.Dispatcher.BeginInvoke(new Action(OpenLogin));
    };

    var wheel = new WheelWindow(names);
    // La rueda vive en ESTE hilo (uiThread); los callbacks llegan desde otros hilos,
    // así que siempre hacemos marshalling al dispatcher de la rueda.
    onNamesChanged = n =>
    {
        var disp = wheel.Dispatcher;
        if (disp == null || wheel == null) return; // ventana cerrada o dispatcher muerto
        try { disp.BeginInvoke(() => wheel.SetNames(n)); } catch { } // la rueda pudo cerrarse mientras tanto
    };

    double scale = Native.GetDpiForSystem() / 96.0;
    bool open = false;
    int cx = 0, cy = 0;
    var was = new bool[10];

    // Sector bajo el mouse (relativo al punto donde se abrió la rueda). -1 si está en el centro o el sector está vacío.
    int Selection(int dx, int dy)
    {
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 40 * scale) return -1;
        double ang = Math.Atan2(dy, dx) + Math.PI / 2;
        if (ang < 0) ang += 2 * Math.PI;
        int idx = (int)(ang / (2 * Math.PI / SlotCount)) % SlotCount;
        return slotFiles[idx] != null ? idx : -1;
    }

    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12) };
    timer.Tick += (_, _) =>
    {
        UpdatePtt();

        bool mods = Native.Down(0x11) && Native.Down(0x10);
        bool wheelDown = (mods && Native.Down(0x20)) || Native.Down(0x05);

        if (wheelDown && !open)
        {
            SyncSlots(); // por si cambiaste los sectores en el panel

            if (slotFiles.Any(f => f != null))
            {
                Native.GetCursorPos(out var p);
                cx = p.X; cy = p.Y;
                wheel.SetSelected(-1);
                wheel.OpenAt(cx / scale, cy / scale);
                open = true;
            }
        }
        else if (open)
        {
            Native.GetCursorPos(out var p);
            int sel = Selection(p.X - cx, p.Y - cy);
            if (wheelDown) wheel.SetSelected(sel);
            else
            {
                wheel.Hide();
                open = false;
                if (sel >= 0) Play(sel);
            }
        }

        if (open) return;

        // Ctrl+Shift+F1..F8 = sectores 1..8
        for (int i = 0; i < SlotCount; i++)
        {
            bool d = mods && Native.Down(0x70 + i);
            if (d && !was[i]) Play(i);
            was[i] = d;
        }
        bool stop = mods && Native.Down(0x7B); // Ctrl+Shift+F12 = detener
        if (stop && !was[9]) StopAll();
        was[9] = stop;
    };
    timer.Start();
    wpfReady.Set();
    app.Run();
});

uiThread.SetApartmentState(ApartmentState.STA);
uiThread.IsBackground = true;
uiThread.Start();

wpfReady.Wait();

// El login inicial ya se completó correctamente: abrir la ventana principal.
OpenSettings();

System.Windows.Forms.Application.Run();

GC.KeepAlive(mutex);