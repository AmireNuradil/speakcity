using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpeakCity;

public sealed class SpeechWorkerClient : IDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private Process? _process;
    private bool _disposed;
    public string WorkerPath { get; }
    public SpeechWorkerClient(string? path = null) => WorkerPath = path ?? Path.Combine(AppContext.BaseDirectory, "speech", "speakcity-speech-worker.exe");

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is { HasExited: false }) return;
        if (!File.Exists(WorkerPath)) throw new InvalidOperationException("The bundled speech component is missing. Repair or reinstall SPEAKCITY.");
        var info = new ProcessStartInfo(WorkerPath)
        {
            WorkingDirectory = Path.GetDirectoryName(WorkerPath)!, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("--threads");
        info.ArgumentList.Add(Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString());
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        info.Environment["PYTHONUTF8"] = "1";
        _process = Process.Start(info) ?? throw new InvalidOperationException("The local speech component could not start.");
        // Drain diagnostics without persisting or displaying learner text/audio.
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
    }

    public async Task<JsonObject> RequestAsync(string operation, JsonObject? fields = null, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(120));
        await _serial.WaitAsync(limit.Token).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            var id = Guid.NewGuid().ToString("N");
            var request = fields?.DeepClone().AsObject() ?? new JsonObject();
            request["id"] = id; request["op"] = operation;
            string line = request.ToJsonString();
            if (Encoding.UTF8.GetByteCount(line) > 4 * 1024 * 1024)
                throw new ArgumentException("The speech request is too large. Use a shorter answer.");
            await _process!.StandardInput.WriteLineAsync(line.AsMemory(), limit.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(limit.Token).ConfigureAwait(false);
            string? response = await _process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false);
            if (response is null || response.Length > 8 * 1024 * 1024)
                throw new InvalidOperationException("The local speech component stopped unexpectedly. Please try again.");
            var parsed = JsonNode.Parse(response)?.AsObject();
            if (parsed is null || parsed["id"]?.GetValue<string>() != id)
                throw new InvalidOperationException("The speech component returned an invalid response.");
            if (parsed["ok"]?.GetValue<bool>() != true)
            {
                string code = parsed["error"]?["code"]?.GetValue<string>() ?? "speech_error";
                string friendly = code.Contains("model", StringComparison.OrdinalIgnoreCase)
                    ? "A bundled speech model is missing or invalid. Repair or reinstall the application."
                    : "The speech request could not be processed. Try a shorter recording or use typing.";
                throw new InvalidOperationException(friendly);
            }
            return parsed["result"]?.DeepClone().AsObject() ?? new JsonObject();
        }
        catch (OperationCanceledException)
        {
            StopProcess();
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("The local speech component took too long. Try again or use typing.");
        }
        catch (JsonException)
        {
            StopProcess(); throw new InvalidOperationException("The speech component returned invalid data.");
        }
        catch (IOException)
        {
            StopProcess(); throw new InvalidOperationException("The local speech component disconnected. Try again.");
        }
        finally { _serial.Release(); }
    }

    private void StopProcess()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose(); _process = null;
    }
    public void Dispose() { _disposed = true; StopProcess(); }
}
