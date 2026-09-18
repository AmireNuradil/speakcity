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
                string path = Path.Combine(Path.GetTempPath(), $"speakcity-{Guid.NewGuid():N}.wav");
                Send($"open new type waveaudio alias {Alias}");
                _open = true;
                _pendingPath = path;
                Skip();
                Send($"set {Alias} bitspersample 16 channels 1 samplespersec 16000 bytespersec 32000 alignment 2");
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

    private static void Skip()
    {
        // Best effort: some drivers dislike an unsupported command, and a failure
        // here must not abort a recording that would otherwise work.
        try { Send($"set {Alias} time format ms"); } catch (InvalidOperationException) { }
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
