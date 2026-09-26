using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SpeakCity;

/// <summary>
/// Microphone capture through the winmm waveIn API.
/// </summary>
/// <remarks>
/// MCI's "waveaudio" alias cannot be given a format on modern Windows: every <c>set</c> command
/// answers "command not supported by the driver", and a recording taken without one comes back as
/// 8-bit 11 kHz, which the bundled speech worker cannot transcribe. waveIn is the same system
/// library, adds no package dependency, and produces real 16 kHz mono 16-bit PCM.
/// </remarks>
public sealed class WavRecorder : IDisposable
{
    private const int TargetRate = 16000;
    private const int BitsPerSample = 16;
    private const int BufferBytes = 4096;
    private const int BufferCount = 8;
    /// <summary>Sixty seconds of the target format stays inside the router's body limit.</summary>
    private const long MaxPcmBytes = TargetRate * 2 * 60;
    /// <summary>Below this peak there is no speech to rescue, only room hiss.</summary>
    private const int QuietFloor = 1200;

    /// <summary>Best first the worker's own format, then formats typical webcams do support.</summary>
    private static readonly (uint Rate, ushort Channels)[] Attempts =
    {
        (16000, 1), (44100, 1), (48000, 1), (48000, 2),
    };

    private readonly object _gate = new();
    private readonly MemoryStream _pcm = new();
    private IntPtr _handle;
    private AutoResetEvent? _done;
    private Thread? _pump;
    private WAVEHDR[]? _headers;
    private GCHandle[] _pins = [];
    private uint _captureRate;
    private ushort _captureChannels;
    private bool _recording;
    private string? _pendingPath;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackEvent = 0x00050000;
    private const uint HeaderDone = 0x00000001;
    private static readonly int HeaderSize = Marshal.SizeOf<WAVEHDR>();

    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr handle, uint deviceId, ref WAVEFORMATEX format,
        IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr handle, ref WAVEHDR header, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr handle, ref WAVEHDR header, int size);

    /// <summary>
    /// Peak amplitude of the last take measured before any gain was applied, 0-32767. The window
    /// uses it to tell "the learner was too quiet" apart from "nothing was recognised".
    /// </summary>
    public int LastPeakAmplitude { get; private set; }

    /// <summary>True when the last take held too little signal for the recogniser to work with.</summary>
    public bool LastTakeWasQuiet { get { lock (_gate) return LastPeakAmplitude < QuietFloor; } }

    public bool IsRecording { get { lock (_gate) return _recording; } }

    /// <summary>Opens the default capture device and starts recording into memory.</summary>
    public bool Start(out string error)
    {
        lock (_gate)
        {
            error = "";
            if (waveInGetNumDevs() == 0)
            {
                error = "No microphone was found on this computer, so practising by voice is not " +
                    "possible here. Typing still works.";
                return false;
            }
            Abandon();
            lock (_pcm) _pcm.SetLength(0);
            try
            {
                OpenDevice();
                _pendingPath = Path.Combine(Path.GetTempPath(), $"speakcity-{Guid.NewGuid():N}.wav");
                _recording = true;
                return true;
            }
            catch (Exception exc)
            {
                error = Describe(exc);
                Abandon();
                return false;
            }
        }
    }

    private void OpenDevice()
    {
        int lastCode = 0;
        foreach ((uint rate, ushort channels) in Attempts)
        {
            var format = new WAVEFORMATEX
            {
                wFormatTag = 1,
                nChannels = channels,
                nSamplesPerSec = rate,
                wBitsPerSample = BitsPerSample,
                nBlockAlign = (ushort)(channels * BitsPerSample / 8),
                nAvgBytesPerSec = rate * channels * BitsPerSample / 8,
            };
            var signal = new AutoResetEvent(false);
            int code = waveInOpen(out IntPtr handle, WaveMapper, ref format,
                signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent);
            lastCode = code;
            if (code != 0) { signal.Dispose(); continue; }
            _done = signal;
            _handle = handle;
            _captureRate = rate;
            _captureChannels = channels;
            PrepareBuffers();
            int started = waveInStart(_handle);
            if (started != 0) throw new InvalidOperationException(FailureText(started));
            return;
        }
        throw new InvalidOperationException(FailureText(lastCode == 0 ? 1024 : lastCode));
    }

    private void PrepareBuffers()
    {
        AutoResetEvent signal = _done!;
        _headers = new WAVEHDR[BufferCount];
        _pins = new GCHandle[BufferCount];
        for (int index = 0; index < BufferCount; index++)
        {
            byte[] buffer = new byte[BufferBytes];
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            _pins[index] = pin;
            _headers[index] = new WAVEHDR { lpData = pin.AddrOfPinnedObject(), dwBufferLength = BufferBytes };
            if (waveInPrepareHeader(_handle, ref _headers[index], HeaderSize) != 0)
                throw new InvalidOperationException("The microphone could not be prepared for recording.");
            if (waveInAddBuffer(_handle, ref _headers[index], HeaderSize) != 0)
                throw new InvalidOperationException("The microphone could not be given a recording buffer.");
        }
        _pump = new Thread(() => Drain(signal)) { IsBackground = true, Name = "speakcity-capture" };
        _pump.Start();
    }

    /// <summary>Copies every finished buffer and puts it back on the queue until the device closes.</summary>
    private void Drain(WaitHandle signal)
    {
        try
        {
            while (_headers is WAVEHDR[] headers)
            {
                try
                {
                    if (!signal.WaitOne(250)) continue;
                }
                catch (ObjectDisposedException) { return; }
                for (int index = 0; index < headers.Length; index++)
                {
                    ref WAVEHDR header = ref headers[index];
                    if ((header.dwFlags & HeaderDone) == 0 || header.dwBytesRecorded == 0) continue;
                    byte[] chunk = new byte[header.dwBytesRecorded];
                    Marshal.Copy(header.lpData, chunk, 0, chunk.Length);
                    lock (_pcm)
                    {
                        // A forgotten microphone must not grow without bound.
                        if (_pcm.Length >= MaxPcmBytes) { waveInStop(_handle); return; }
                        _pcm.Write(chunk, 0, chunk.Length);
                    }
                    header.dwFlags &= ~HeaderDone;
                    if (waveInAddBuffer(_handle, ref header, HeaderSize) != 0) return;
                }
            }
        }
        catch (Exception) { /* Stop reports anything the capture thread could not finish */ }
    }

    /// <summary>Stops recording, writes the WAV and returns its path.</summary>
    public bool Stop(out string? path, out string error)
    {
        lock (_gate)
        {
            path = null;
            error = "";
            if (_handle == IntPtr.Zero) { error = "The microphone was not started."; return false; }
            _recording = false;
            waveInStop(_handle);
            waveInReset(_handle);
            JoinPump();
            string? target = _pendingPath;
            byte[] pcm;
            lock (_pcm) pcm = _pcm.ToArray();
            Abandon();
            if (target is null) { error = "No recording target was prepared."; return false; }
            // 0.1 s of the target format: less than this is a click, not an answer.
            if (pcm.Length <= TargetRate / 5 * 2)
            {
                error = "Nothing was recorded. Check that this microphone is selected in Windows " +
                    "sound settings and try again.";
                return false;
            }
            byte[] mono = ToMono16Bit(pcm);
            int peak = PeakAmplitude(mono);
            LastPeakAmplitude = peak;
            File.WriteAllBytes(target, BuildWav(Normalize(mono, peak)));
            path = target;
            return true;
        }
    }

    private static int PeakAmplitude(byte[] pcm)
    {
        int peak = 0;
        for (int index = 0; index + 1 < pcm.Length; index += 2)
        {
            int sample = Math.Abs(BitConverter.ToInt16(pcm, index));
            if (sample > peak) peak = sample;
        }
        return peak;
    }

    /// <summary>
    /// Learners keep their voice low around other people, and the microphone records exactly that,
    /// which is the level the local recogniser misses. Lifting a quiet take towards a spoken level is
    /// what makes it transcribe; the gain and the floor are capped so an empty room cannot be
    /// amplified into a voice that was never said.
    /// </summary>
    internal static byte[] Normalize(byte[] pcm, int peak)
    {
        const int AlreadyLoudEnough = 24000;
        const int TargetPeak = 16000;
        const double MaximumGain = 10;
        if (peak < QuietFloor || peak > AlreadyLoudEnough) return pcm;
        double gain = Math.Min(TargetPeak / (double)peak, MaximumGain);
        var scaled = new byte[pcm.Length];
        for (int index = 0; index + 1 < pcm.Length; index += 2)
        {
            int value = (int)(BitConverter.ToInt16(pcm, index) * gain);
            BitConverter.GetBytes((short)Math.Clamp(value, -32768, 32767)).CopyTo(scaled, index);
        }
        return scaled;
    }

    private void JoinPump()
    {
        Thread? pump = _pump;
        _pump = null;
        try { _done?.Close(); } catch { }
        if (pump is not null && pump != Thread.CurrentThread) pump.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>Downmixes and decimates so the worker always receives 16 kHz mono 16-bit.</summary>
    private byte[] ToMono16Bit(byte[] pcm)
    {
        int channels = _captureChannels;
        int sourceRate = (int)_captureRate;
        if (channels == 1 && sourceRate == TargetRate) return pcm;
        int frames = pcm.Length / (2 * channels);
        var mono = new short[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            int total = 0;
            for (int channel = 0; channel < channels; channel++)
                total += BitConverter.ToInt16(pcm, (frame * channels + channel) * 2);
            mono[frame] = (short)(total / channels);
        }
        if (sourceRate == TargetRate) return MemoryMarshal.AsBytes(mono.AsSpan()).ToArray();
        double step = sourceRate / (double)TargetRate;
        int targetFrames = (int)(frames / step);
        var resampled = new short[Math.Max(targetFrames, 0)];
        for (int index = 0; index < resampled.Length; index++)
        {
            double position = index * step;
            int left = Math.Min((int)position, frames - 1);
            int right = Math.Min(left + 1, frames - 1);
            double fraction = position - left;
            resampled[index] = (short)(mono[left] * (1 - fraction) + mono[right] * fraction);
        }
        return MemoryMarshal.AsBytes(resampled.AsSpan()).ToArray();
    }

    private static byte[] BuildWav(byte[] pcm)
    {
        using var stream = new MemoryStream(44 + pcm.Length);
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write((uint)(36 + pcm.Length));
            writer.Write("WAVEfmt "u8);
            writer.Write((uint)16);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((uint)TargetRate);
            writer.Write((uint)(TargetRate * 2));
            writer.Write((ushort)2);
            writer.Write((ushort)16);
            writer.Write("data"u8);
            writer.Write((uint)pcm.Length);
            writer.Write(pcm);
        }
        return stream.ToArray();
    }

    private static string FailureText(int code) => code switch
    {
        1024 => "This microphone cannot record in a format the app needs.",
        1025 => "The microphone is still busy with an earlier recording.",
        4 => "No microphone driver is available on this computer.",
        8 => "No microphone was found on this computer.",
        10 => "The microphone is being used by another app.",
        11 => "The microphone is disabled. Enable it in Windows sound settings.",
        _ => $"The microphone could not be started (code {code}).",
    };

    private static string Describe(Exception exc)
    {
        string text = exc.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length == 0
            ? "The microphone could not be used. Check Windows microphone privacy settings."
            : text;
    }

    /// <summary>Releases the device and every buffer. The caller must hold the gate.</summary>
    private void Abandon()
    {
        _headers = null;
        if (_handle != IntPtr.Zero)
        {
            waveInReset(_handle);
            waveInClose(_handle);
            _handle = IntPtr.Zero;
        }
        foreach (GCHandle pin in _pins) if (pin.IsAllocated) pin.Free();
        _pins = [];
        _done?.Dispose();
        _done = null;
        _pendingPath = null;
        _recording = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            JoinPump();
            Abandon();
            _pcm.Dispose();
        }
    }
}
