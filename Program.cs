using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using HamulProto;
using HamulProto.Models;
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


bool manualLoginSucceeded = false;
bool hasSavedToken = !string.IsNullOrEmpty(Config.Current.AuthToken);
bool startupWithSavedToken = args.Contains("--startup", StringComparer.OrdinalIgnoreCase) && hasSavedToken;
ApiClient.SetToken(Config.Current.AuthToken);

if (hasSavedToken)
{
    var meResult = ApiClient.GetAsync<MeResponse>("/api/auth/me").GetAwaiter().GetResult();
    if (meResult.IsSuccess && meResult.Data is { } me)
    {
        Config.Current.UserId = me.UserId;
        Config.Current.Nickname = me.Nickname;
        Config.Current.Save();
    }
    else
    {
        hasSavedToken = false;
    }
}

if (!hasSavedToken)
{
    bool? loginResult = null;
    Exception? loginError = null;

    // El hilo principal de una app de consola es MTA: ShowDialog() exige STA.
    // Ejecutamos el login en un hilo STA dedicado y esperamos a que se cierre.
    var loginThread = new Thread(() =>
    {
        try
        {
            var login = new HamulProto.LoginWindow
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                Topmost = true,
                ShowInTaskbar = true
            };
            loginResult = login.ShowDialog();
        }
        catch (Exception ex)
        {
            loginError = ex;
        }
    });
    loginThread.SetApartmentState(ApartmentState.STA);
    loginThread.IsBackground = true;
    loginThread.Start();
    loginThread.Join(); // Bloquea el hilo principal hasta que se cierre el login

    if (loginError != null)
    {
        System.Windows.MessageBox.Show("Error al abrir el login:\n" + loginError.Message, "Hamul");
        return;
    }

    if (loginResult != true)
    {
        return; // El usuario cerró el login sin autenticarse
    }

    manualLoginSucceeded = true;
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

// --- Sonidos
var soundsDir = Path.Combine(Config.Dir, "sounds");
Directory.CreateDirectory(soundsDir);

var migratedFlag = Path.Combine(Config.Dir, ".sounds-migrated");
if (!File.Exists(migratedFlag))
{
    var oldDir = Path.Combine(AppContext.BaseDirectory, "sounds");
    if (Directory.Exists(oldDir))
    {
        foreach (var f in Directory.GetFiles(oldDir))
        {
            try { File.Copy(f, Path.Combine(soundsDir, Path.GetFileName(f))); } catch { }
        }
    }
    try { File.WriteAllText(migratedFlag, "1"); } catch { }
}

// --- Sectores de la rueda (8 fijos). Se guardan en slots.json desde el panel de control.
const int SlotCount = 8;
string slotsPath = Path.Combine(Config.Dir, "slots.json");
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
        // Primera vez: usar los primeros 8 sonidos por orden alfabético y guardarlos
        var first = Directory.GetFiles(soundsDir)
            .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(SlotCount)
            .ToList();
        for (int i = 0; i < first.Count; i++) next[i] = first[i];

        try
        {
            File.WriteAllText(slotsPath, JsonSerializer.Serialize(next));
            slotsStamp = File.GetLastWriteTimeUtc(slotsPath);
        }
        catch { }
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

int settingsOpen = 0;
void OpenSettings()
{
    if (Interlocked.Exchange(ref settingsOpen, 1) != 0) return;

    var thread = new Thread(() =>
    {
        try
        {
            var win = new DashboardWindow();
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

    int loginPromptOpen = 0;
    ApiClient.AuthenticationRequired += (_, _) =>
    {
        if (Interlocked.Exchange(ref loginPromptOpen, 1) != 0 ||
            app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
            return;

        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var login = new LoginWindow
                {
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    ShowInTaskbar = true
                };
                if (login.ShowDialog() != true)
                {
                    System.Windows.MessageBox.Show(
                        "La sesión requiere iniciar sesión nuevamente. Hamul seguirá abierto.",
                        "Hamul",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
            }
            finally
            {
                Interlocked.Exchange(ref loginPromptOpen, 0);
            }
        }));
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

if (manualLoginSucceeded && !startupWithSavedToken)
    OpenSettings();

System.Windows.Forms.Application.Run();

GC.KeepAlive(mutex);