using System.IO;
using System.Text.Json;

namespace HamulProto;

public class SoundEntry
{
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
}

public class Config
{
    private static readonly object Sync = new();
    private static Config? _current;
    private string? _micId;
    private string? _monitorId;
    private string? _originalMicId;
    private bool _switchDefaultMic = true;
    private List<SoundEntry> _sounds = new();
    private bool _pttEnabled = true;
    private string _pttKey = "G";
    private string? _authToken;
    private string? _nickname;
    private long _userId;
    private string? _lastEmail;
    private string _theme = "Dark";
    private string _language = "es";
    private int _soundVolume = 100;

    public static Config Current
    {
        get
        {
            lock (Sync)
                return _current ??= LoadFromFile();
        }
    }

    // --- Dispositivos de Audio ---
    public string? MicId { get { lock (Sync) return _micId; } set { lock (Sync) _micId = value; } }
    public string? MonitorId { get { lock (Sync) return _monitorId; } set { lock (Sync) _monitorId = value; } }
    public string? OriginalMicId { get { lock (Sync) return _originalMicId; } set { lock (Sync) _originalMicId = value; } }
    public bool SwitchDefaultMic { get { lock (Sync) return _switchDefaultMic; } set { lock (Sync) _switchDefaultMic = value; } }
    public List<SoundEntry> Sounds { get { lock (Sync) return _sounds; } set { lock (Sync) _sounds = value ?? new(); } }

    // --- Pulsar-para-hablar (Dota 2) ---
    public bool PttEnabled { get { lock (Sync) return _pttEnabled; } set { lock (Sync) _pttEnabled = value; } }
    public string PttKey { get { lock (Sync) return _pttKey; } set { lock (Sync) _pttKey = value; } }

    // --- Sesión de Usuario (NUEVO) ---
    public string? AuthToken { get { lock (Sync) return _authToken; } set { lock (Sync) _authToken = value; } }
    public string? Nickname { get { lock (Sync) return _nickname; } set { lock (Sync) _nickname = value; } }
    public long UserId { get { lock (Sync) return _userId; } set { lock (Sync) _userId = value; } }
    public string? LastEmail { get { lock (Sync) return _lastEmail; } set { lock (Sync) _lastEmail = value; } }

    // --- Apariencia e Idioma (NUEVO) ---
    public string Theme { get { lock (Sync) return _theme; } set { lock (Sync) _theme = value; } }
    public string Language { get { lock (Sync) return _language; } set { lock (Sync) _language = value; } }
    public int SoundVolume
    {
        get { lock (Sync) return _soundVolume; }
        set { lock (Sync) _soundVolume = Math.Clamp(value, 0, 100); }
    }

    // --- Rutas ---
    public static string Dir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HamulProto");

    static string FilePath => System.IO.Path.Combine(Dir, "config.json");

    private static Config LoadFromFile()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch { }
        return new Config();
    }

    public void Save()
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Dir);
            var tempPath = FilePath + ".tmp";
            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tempPath, FilePath, overwrite: true);
            }
            catch
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                throw;
            }
        }
    }
}