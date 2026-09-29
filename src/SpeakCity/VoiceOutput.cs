using System.Runtime.InteropServices;
using System.Text;

namespace SpeakCity;

/// <summary>
/// An audio output that plays written PCM back to back with no gap between writes, so the only
/// silence between two of Lucy's sentences is the pause the caller writes on purpose.
/// </summary>
internal interface IVoiceOutput : IDisposable
{
    /// <summary>False when no output device can play this format; the caller then falls back.</summary>
    bool Open(WavFormat format);
    /// <summary>Queues audio behind whatever is still playing.</summary>
    void Write(byte[] pcm);
    /// <summary>True while any written audio has not finished playing.</summary>
    bool Playing { get; }
    /// <summary>Stops at once and drops everything queued.</summary>
    void Reset();
}

internal readonly record struct WavFormat(int SampleRate, int Channels, int BitsPerSample)
{
    public int BlockAlign => Channels * BitsPerSample / 8;
    public int BytesPerSecond => SampleRate * BlockAlign;
    public TimeSpan Duration(int bytes) => TimeSpan.FromSeconds(bytes / (double)BytesPerSecond);
    public byte[] Silence(TimeSpan length) => new byte[(int)Math.Round(length.TotalSeconds * SampleRate) * BlockAlign];
}

internal static class SentencePacing
{
    /// <summary>
    /// Where the next sentence goes on a continuous output, given when the audio written so far ends.
    /// If it is still playing, the full pause is written after it. If the output has already gone
    /// silent (the sentence was not ready in time), that silence counts towards the pause, so waiting
    /// never adds a pause on top of itself.
    /// </summary>
    public static (TimeSpan Silence, TimeSpan Start, TimeSpan AlreadySilent) Next(TimeSpan now, TimeSpan audioEnd, bool first, TimeSpan pause)
    {
        if (first) return (TimeSpan.Zero, now, TimeSpan.Zero);
        TimeSpan silent = now > audioEnd ? now - audioEnd : TimeSpan.Zero;
        TimeSpan gap = silent >= pause ? TimeSpan.Zero : pause - silent;
        return (gap, (now > audioEnd ? now : audioEnd) + gap, silent);
    }
}

internal static class WavAudio
{
    /// <summary>Reads 16-bit PCM out of a RIFF/WAVE file by walking its chunks, not by assuming a 44-byte header.</summary>
    public static bool TryRead(byte[] wav, out WavFormat format, out byte[] pcm)
    {
        format = default;
        pcm = [];
        if (wav.Length < 12 || Tag(wav, 0) != "RIFF" || Tag(wav, 8) != "WAVE") return false;
        bool haveFormat = false;
        int offset = 12;
        while (offset + 8 <= wav.Length)
        {
            string id = Tag(wav, offset);
            int body = offset + 8;
            int size = BitConverter.ToInt32(wav, offset + 4);
            if (size < 0 || size > wav.Length - body) size = wav.Length - body;
            if (id == "fmt " && size >= 16)
            {
                int tag = BitConverter.ToInt16(wav, body);
                int channels = BitConverter.ToInt16(wav, body + 2);
                int rate = BitConverter.ToInt32(wav, body + 4);
                int bits = BitConverter.ToInt16(wav, body + 14);
                if (tag != 1 || channels is < 1 or > 2 || rate is < 8000 or > 96000 || bits != 16) return false;
                format = new WavFormat(rate, channels, bits);
                haveFormat = true;
            }
            else if (id == "data" && haveFormat)
            {
                int length = size - size % format.BlockAlign;
                pcm = wav.AsSpan(body, length).ToArray();
                return length > 0;
            }
            offset = body + size + (size & 1);
        }
        return false;
    }

    private static string Tag(byte[] data, int at) => Encoding.ASCII.GetString(data, at, 4);
}

/// <summary>
/// Windows waveOut: every write becomes one device buffer and the device plays queued buffers
/// seamlessly. Headers and samples live in unmanaged memory because the driver keeps pointers to
/// both until it hands a buffer back (the same lesson as the recorder's pinned headers, F-01).
/// </summary>
internal sealed class WaveOutVoice : IVoiceOutput
{
    private const int WaveMapper = -1;
    private const int CallbackNull = 0;
    private const int HeaderDone = 0x1;
    private static readonly int HeaderSize = Marshal.SizeOf<WAVEHDR>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WAVEHDR>(nameof(WAVEHDR.dwFlags));
    private readonly object _gate = new();
    private readonly List<(IntPtr Header, IntPtr Data)> _queued = new();
    private IntPtr _handle;
    private WavFormat _format;

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

    [DllImport("winmm.dll")] private static extern int waveOutGetNumDevs();
    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr handle, int device, ref WAVEFORMATEX format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr handle);

    public bool Open(WavFormat format)
    {
        lock (_gate)
        {
            if (_handle != IntPtr.Zero && format == _format) return true;
            CloseDevice();
            try
            {
                if (waveOutGetNumDevs() == 0) return false;
                var wave = new WAVEFORMATEX
                {
                    wFormatTag = 1,
                    nChannels = (ushort)format.Channels,
                    nSamplesPerSec = (uint)format.SampleRate,
                    nAvgBytesPerSec = (uint)format.BytesPerSecond,
                    nBlockAlign = (ushort)format.BlockAlign,
                    wBitsPerSample = (ushort)format.BitsPerSample
                };
                if (waveOutOpen(out IntPtr handle, WaveMapper, ref wave, IntPtr.Zero, IntPtr.Zero, CallbackNull) != 0) return false;
                _handle = handle;
                _format = format;
                return true;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }

    public void Write(byte[] pcm)
    {
        if (pcm.Length == 0) return;
        lock (_gate)
        {
            if (_handle == IntPtr.Zero) throw new InvalidOperationException("The voice output is not open.");
            Reap();
            IntPtr data = Marshal.AllocHGlobal(pcm.Length);
            IntPtr header = Marshal.AllocHGlobal(HeaderSize);
            Marshal.Copy(pcm, 0, data, pcm.Length);
            Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = (uint)pcm.Length }, header, false);
            if (waveOutPrepareHeader(_handle, header, HeaderSize) != 0)
            {
                Free(header, data);
                throw new InvalidOperationException("The voice output refused a buffer.");
            }
            if (waveOutWrite(_handle, header, HeaderSize) != 0)
            {
                waveOutUnprepareHeader(_handle, header, HeaderSize);
                Free(header, data);
                throw new InvalidOperationException("The voice output refused a buffer.");
            }
            _queued.Add((header, data));
        }
    }

    public bool Playing
    {
        get
        {
            lock (_gate)
            {
                Reap();
                return _queued.Count > 0;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            // waveOutReset marks every pending buffer done before it returns.
            if (_handle != IntPtr.Zero) waveOutReset(_handle);
            Reap();
        }
    }

    public void Dispose()
    {
        lock (_gate) CloseDevice();
    }

    private void Reap()
    {
        for (int index = _queued.Count - 1; index >= 0; index--)
        {
            var (header, data) = _queued[index];
            if ((Marshal.ReadInt32(header, FlagsOffset) & HeaderDone) == 0) continue;
            waveOutUnprepareHeader(_handle, header, HeaderSize);
            Free(header, data);
            _queued.RemoveAt(index);
        }
    }

    private void CloseDevice()
    {
        if (_handle == IntPtr.Zero) return;
        waveOutReset(_handle);
        Reap();
        // A buffer the driver still reports as busy after a reset is leaked, never freed under it.
        _queued.Clear();
        waveOutClose(_handle);
        _handle = IntPtr.Zero;
    }

    private static void Free(IntPtr header, IntPtr data)
    {
        Marshal.FreeHGlobal(header);
        Marshal.FreeHGlobal(data);
    }
}
