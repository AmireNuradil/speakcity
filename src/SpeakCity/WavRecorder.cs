using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SpeakCity;

/// <summary>
/// Minimal microphone recorder built on the Windows MCI waveaudio device
/// (winmm.dll). It introduces no new package dependency and produces exactly
/// the format the bundled worker expects: 16 kHz, mono, 16-bit PCM WAV.
/// MCI is a Windows component, not a browser component.
/// </summary>
public sealed class WavRecorder : IDisposable
{
    private const string Alias = "speakcitycap";
    // An uncapped take keeps buffering in memory for as long as the microphone is left on;
    // sixty seconds of this format stays inside the router's body limit.
    private const int MaxRecordingMilliseconds = 60_000;
    private readonly object _gate = new();
    private bool _open;
    private bool _recording;
    private string? _pendingPath;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferLength, IntPtr hwnd);

    private static string Message(int code)
    {
        var text = new StringBuilder(256);
        if (mciGetErrorString(code, text, text.Capacity)) return text.ToString();
        return $"MCI error {code}.";
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern bool mciGetErrorString(int code, StringBuilder buffer, int bufferLength);

    private static void Send(string command)
    {
        int code = mciSendString(command, null, 0, IntPtr.Zero);
        if (code != 0) throw new InvalidOperationException(Message(code));
    }

    private static string Query(string command)
    {
        var buffer = new StringBuilder(256);
        int code = mciSendString(command, buffer, buffer.Capacity, IntPtr.Zero);
        if (code != 0) throw new InvalidOperationException(Message(code));
        return buffer.ToString();
    }

    public bool IsRecording { get { lock (_gate) return _recording; } }

    /// <summary>Opens the default capture device and starts recording.</summary>
    public bool Start(out string error)
    {
        lock (_gate)
        {
            error = "";
            try
            {
                Close();
                // MCI only answers this when the machine has a waveaudio input device at all.
                // Without one every later command fails with an opaque parameter error, so say
                // the true thing instead of blaming the microphone's settings.
                if (!TrySend("sysinfo audio"))
                {
                    error = "No microphone was found on this computer, so practising by voice is " +
                        "not possible here. Typing still works.";
                    return false;
                }
                string path = Path.Combine(Path.GetTempPath(), $"speakcity-{Guid.NewGuid():N}.wav");
                Send($"open new type waveaudio alias {Alias}");
                _open = true;
                _pendingPath = path;
                bool milliseconds = TrySend($"set {Alias} time format ms");
                // 16 kHz mono 16-bit is what the bundled worker prefers, but a driver may refuse a
                // format change and recording at its default still transcribes, so neither the
                // format nor its derived values may be fatal here.
                TrySend($"set {Alias} bitspersample 16 channels 1 samplespersec 16000");
                TrySend($"set {Alias} bytespersec 32000 alignment 2");
                // A driver that dislikes the length argument must still record, uncapped, rather
                // than be reported to the learner as a broken microphone.
                if (!milliseconds || !TrySend($"record {Alias} length {MaxRecordingMilliseconds}"))
                    Send($"record {Alias}");
                _recording = true;
                return true;
            }
            catch (Exception exc)
            {
                error = Describe(exc);
                Close();
                return false;
            }
        }
    }

    private static bool TrySend(string command)
    {
        // Best effort: some drivers dislike an unsupported command, and a failure here must not
        // abort a recording that would otherwise work.
        try { Send(command); return true; } catch (InvalidOperationException) { return false; }
    }

    /// <summary>Stops recording, saves the WAV and returns its path.</summary>
    public bool Stop(out string? path, out string error)
    {
        lock (_gate)
        {
            path = null;
            error = "";
            if (!_open) { error = "The microphone was not started."; return false; }
            try
            {
                if (_recording) Send($"stop {Alias}");
                _recording = false;
                if (_pendingPath is null) { error = "No recording target was prepared."; Close(); return false; }
                Send($"save {Alias} \"{_pendingPath}\"");
                string saved = _pendingPath;
                Close();
                if (!File.Exists(saved) || new FileInfo(saved).Length <= 44)
                {
                    error = "Nothing was recorded. Check the microphone in Windows sound settings and try again.";
                    return false;
                }
                path = saved;
                return true;
            }
            catch (Exception exc)
            {
                error = Describe(exc);
                Close();
                return false;
            }
        }
    }

    private static string Describe(Exception exc)
    {
        // MCI messages are fixed Windows strings: no learner text can reach them.
        string text = exc.Message.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length == 0
            ? "The microphone could not be used. Check Windows microphone privacy settings."
            : $"The microphone could not be used: {text}";
    }

    private void Close()
    {
        if (_open)
        {
            try { Send($"close {Alias}"); } catch (InvalidOperationException) { }
        }
        _open = false;
        _recording = false;
        _pendingPath = null;
    }

    public void Dispose()
    {
        lock (_gate) Close();
    }
}
