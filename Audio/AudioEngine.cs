using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HamulProto;

public class CachedSound
{
    public float[] Data { get; }

    CachedSound() { Data = Array.Empty<float>(); }

    public static CachedSound Empty => new CachedSound();

    public CachedSound(string path, int rate)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider sp = reader;
        if (sp.WaveFormat.SampleRate != rate) sp = new WdlResamplingSampleProvider(sp, rate);
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);

        var all = new List<float>();
        var buf = new float[rate * 2];
        int read;
        while ((read = sp.Read(buf, 0, buf.Length)) > 0)
            all.AddRange(buf.Take(read));
        Data = all.ToArray();
    }
}

public class CachedSoundProvider : ISampleProvider
{
    readonly CachedSound sound;
    int position;

    public bool Stopped { get; set; }
    public bool Finished => Stopped || position >= sound.Data.Length;
    public float Gain { get; set; } = 1f;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public CachedSoundProvider(CachedSound s) => sound = s;

    public int Read(float[] buffer, int offset, int count)
    {
        if (Stopped) return 0; // el mezclador lo elimina al recibir 0
        int available = sound.Data.Length - position;
        int toCopy = Math.Min(available, count);
        for (int i = 0; i < toCopy; i++)
            buffer[offset + i] = sound.Data[position + i] * Gain;
        position += toCopy;
        return toCopy;
    }
}

public class AudioEngine
{
    public const int Rate = 48000;
    static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

    MixingSampleProvider? cableMixer, monitorMixer;
    WasapiCapture? capture;
    WasapiOut? cableOut, monitorOut;
    readonly List<CachedSoundProvider> active = new();
    readonly object gate = new();
    volatile List<CachedSound> sounds = new();

    public bool Running => capture != null;
        public bool IsPlaying
    {
        get
        {
            lock (gate)
                return active.Any(a => !a.Finished);
        }
    }
 
    public void SetSounds(List<CachedSound> list)
    {
        StopAll();
        sounds = list;
    }

    public void Start(MMDevice mic, MMDevice cableIn, MMDevice monitor)
    {
        Stop();
        try
        {
            cableMixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
            monitorMixer = new MixingSampleProvider(MixFormat) { ReadFully = true };

            var cap = new WasapiCapture(mic);
            capture = cap;
            var buffer = new BufferedWaveProvider(cap.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(500)
            };
            cap.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

            ISampleProvider micSamples = buffer.ToSampleProvider();
            if (micSamples.WaveFormat.SampleRate != Rate)
                micSamples = new WdlResamplingSampleProvider(micSamples, Rate);
            if (micSamples.WaveFormat.Channels == 1)
                micSamples = new MonoToStereoSampleProvider(micSamples);
            cableMixer.AddMixerInput(micSamples);

            cableOut = new WasapiOut(cableIn, AudioClientShareMode.Shared, true, 50);
            cableOut.Init(cableMixer);
            monitorOut = new WasapiOut(monitor, AudioClientShareMode.Shared, true, 50);
            monitorOut.Init(monitorMixer);

            cap.StartRecording();
            cableOut.Play();
            monitorOut.Play();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        StopAll();
        Try(() => capture?.StopRecording());
        Try(() => cableOut?.Stop());
        Try(() => monitorOut?.Stop());
        Try(() => capture?.Dispose());
        Try(() => cableOut?.Dispose());
        Try(() => monitorOut?.Dispose());
        capture = null;
        cableOut = null;
        monitorOut = null;
        cableMixer = null;
        monitorMixer = null;
    }

    static void Try(Action a)
    {
        try { a(); } catch { }
    }

    public void Play(int n)
    {
        var list = sounds;
        var cm = cableMixer;
        var mm = monitorMixer;
        if (cm == null || mm == null || n < 0 || n >= list.Count) return;

        var forCable = new CachedSoundProvider(list[n]);
        var forMonitor = new CachedSoundProvider(list[n]);
        float gain = Config.Current.SoundVolume / 100f;
        forCable.Gain = gain;
        forMonitor.Gain = gain;
        lock (gate)
        {
            active.RemoveAll(a => a.Finished);
            active.Add(forCable);
            active.Add(forMonitor);
        }
        cm.AddMixerInput(forCable);
        mm.AddMixerInput(forMonitor);
    }

    public void StopAll()
    {
        lock (gate)
        {
            active.ForEach(a => a.Stopped = true);
            active.Clear();
        }
    }
}
