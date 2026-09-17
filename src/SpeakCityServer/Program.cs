using SpeakCity;
// SpeakCityServer - browser-free host for the SPEAKCITY interface.
// Serves the packaged UI plus the /api/* routes over http://127.0.0.1:<port>
// so the interface can run in any external browser (Edge app mode) even when
// spawning embedded browser processes is blocked on the machine.
// The API contract is byte-compatible with the desktop app: AppRouter answers
// every /api/* route; static files come from ui\.
// Configure AI: the UI's "Configure AI" button opens a local settings page in
// the browser; saving writes the same DPAPI-protected store the desktop app
// uses (%LOCALAPPDATA%\SpeakCity\api-settings.bin). Environment variables
// SPEAKCITY_BASE_URL / SPEAKCITY_MODEL / SPEAKCITY_API_KEY override the store.
// Binds to loopback only. No conversations or keys are logged.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

int port = 8899;
int portIndex = Array.IndexOf(args, "--port");
if (portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out int parsed)) port = parsed;
bool launch = !args.Contains("--no-launch");

string? EnvOrEmpty(string name) => Environment.GetEnvironmentVariable(name) ?? "";
SpeakCity.ApiConfig EnvironmentConfig() => new()
{
    BaseUrl = EnvOrEmpty("SPEAKCITY_BASE_URL"),
    Model = EnvOrEmpty("SPEAKCITY_MODEL"),
    ApiKey = EnvOrEmpty("SPEAKCITY_API_KEY")
};
SpeakCity.ApiConfig EffectiveConfig()
{
    var env = EnvironmentConfig();
    if (SpeakCity.ApiConfigStore.IsConfigured(env)) return env;
    return SpeakCity.ApiConfigStore.Load();
}

string baseDir = AppContext.BaseDirectory;
string uiDir = Path.Combine(baseDir, "ui");
if (!File.Exists(Path.Combine(uiDir, "index.html")))
{
    Console.Error.WriteLine("UI folder is missing next to SpeakCityServer.exe (expected: ui\\index.html).");
    return 2;
}
string contentPath = Path.Combine(baseDir, "content", "scenarios.json");
if (!File.Exists(contentPath))
{
    Console.Error.WriteLine("content\\scenarios.json is missing next to SpeakCityServer.exe.");
    return 2;
}

using var worker = new SpeechWorkerClient();
var api = new SpeakCity.ApiClient(EffectiveConfig);
using var router = new AppRouter(worker, () => Task.FromResult(false), EffectiveConfig, api.CompleteJsonAsync, contentPath);

using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
try { listener.Start(); }
catch (HttpListenerException)
{
    // Another SpeakCityServer probably owns this port already: just show the app.
    Console.WriteLine($"Port {port} is already served - opening the app window.");
    if (launch)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c start \"\" msedge --app=http://127.0.0.1:{port}/index.html --window-size=1340,900")
        { CreateNoWindow = true, UseShellExecute = false };
        _ = System.Diagnostics.Process.Start(psi);
    }
    return 0;
}
Console.WriteLine($"SPEAKCITY server: http://127.0.0.1:{port}/  (close this window or press Ctrl+C to stop)");
Console.WriteLine("Keep this window open while practicing.");

_ = Task.Run(async () =>
{
    try
    {
        await worker.RequestAsync("ping");
        Console.WriteLine("Speech engine: ready.");
    }
    catch
    {
        Console.WriteLine("Speech engine: not available right now (typing still works).");
    }
});

if (launch)
{
    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
        $"/c start \"\" msedge --app=http://127.0.0.1:{port}/index.html --window-size=1340,900")
    { CreateNoWindow = true, UseShellExecute = false };
    _ = System.Diagnostics.Process.Start(psi);
}

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// "Configure AI" handshake: the UI waits on POST /api/configure while the
// learner fills the local settings page, exactly like the native dialog did.
TaskCompletionSource<bool>? configureGate = null;

const string ConfigurePage = """"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>SPEAKCITY AI - Configure AI</title>
<style>
  body { font-family: "Segoe UI", Arial, sans-serif; background: #10141c; color: #e8edf4; margin: 0; }
  .card { max-width: 560px; margin: 60px auto; background: #1a2130; border-radius: 14px; padding: 28px 30px; }
  h1 { font-size: 22px; margin: 0 0 6px; }
  p { color: #9fb0c3; font-size: 14px; margin: 0 0 18px; }
  label { display: block; font-size: 13px; color: #9fb0c3; margin: 14px 0 6px; }
  input { width: 100%; box-sizing: border-box; padding: 10px 12px; border-radius: 8px; border: 1px solid #33415c;
          background: #0d1119; color: #e8edf4; font-size: 14px; }
  button { margin-top: 22px; width: 100%; padding: 12px; border: 0; border-radius: 8px; font-size: 15px;
           background: #2f6feb; color: white; cursor: pointer; }
  button:hover { background: #275fd0; }
  .note { font-size: 12px; color: #74879c; margin-top: 14px; }
  .ok { color: #4cc38a; font-weight: 600; }
</style>
</head>
<body>
<div class="card">
  <h1>Configure AI</h1>
  <p>Paste the settings of your AI provider (OpenAI-compatible API). They are stored encrypted on this PC only.</p>
  <form method="post" action="/configure-save">
    <label for="baseUrl">API base URL (must start with https://)</label>
    <input id="baseUrl" name="baseUrl" placeholder="https://api.openai.com/v1" required>
    <label for="model">Model ID (exact name)</label>
    <input id="model" name="model" placeholder="gpt-4o-mini" required>
    <label for="apiKey">API key</label>
    <input id="apiKey" name="apiKey" type="password" placeholder="sk-..." required>
    <button type="submit">Save</button>
  </form>
  <div class="note">The key is encrypted for your Windows user (DPAPI) and is used only to talk to your provider.
  After saving, close this tab and return to the SPEAKCITY window.</div>
</div>
</body>
</html>
"""";

const string SavedPage = """"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>SPEAKCITY AI - saved</title>
<style>
  body { font-family: "Segoe UI", Arial, sans-serif; background: #10141c; color: #e8edf4;
         display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }
  .card { text-align: center; }
  .ok { font-size: 24px; color: #4cc38a; font-weight: 600; }
  p { color: #9fb0c3; }
</style>
</head>
<body>
<div class="card">
  <div class="ok">Settings saved вњ“</div>
  <p>You can close this tab and return to the SPEAKCITY window.</p>
</div>
</body>
</html>
"""";

static Dictionary<string, string> ParseForm(string body)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        int eq = pair.IndexOf('=');
        if (eq <= 0) continue;
        string key = Uri.UnescapeDataString(pair[..eq].Replace("+", " "));
        string value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace("+", " "));
        result[key] = value;
    }
    return result;
}

string ContentTypeOf(string path)
{
    var ext = Path.GetExtension(path).ToLowerInvariant();
    return ext switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".webp" => "image/webp",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream"
    };
}

async Task WriteJson(HttpListenerResponse response, int status, JsonObject value, CancellationToken ct)
{
    response.StatusCode = status;
    response.ContentType = "application/json; charset=utf-8";
    byte[] bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
    response.ContentLength64 = bytes.Length;
    await response.OutputStream.WriteAsync(bytes, ct);
    response.OutputStream.Close();
}

while (!cts.IsCancellationRequested)
{
    HttpListenerContext ctx;
    try { ctx = await listener.GetContextAsync(); }
    catch (Exception) when (cts.IsCancellationRequested) { break; }
    _ = Task.Run(async () =>
    {
        try
        {
            var request = ctx.Request;
            var response = ctx.Response;
            response.Headers["Cache-Control"] = "no-store";
            string path = request.Url?.AbsolutePath ?? "/";
            string method = request.HttpMethod.ToUpperInvariant();

            if (path.StartsWith("/api/", StringComparison.Ordinal))
            {
                if (path == "/api/configure" && method == "POST")
                {
                    // Open the local settings page and hold the request like a modal dialog.
                    var psi = new System.Diagnostics.ProcessStartInfo($"http://127.0.0.1:{port}/configure")
                    { UseShellExecute = true };
                    _ = System.Diagnostics.Process.Start(psi);
                    configureGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    bool finished = await Task.WhenAny(configureGate.Task, Task.Delay(TimeSpan.FromMinutes(10), cts.Token)) == configureGate.Task;
                    bool configured = finished && configureGate.Task.Result;
                    await WriteJson(response, 200, new JsonObject { ["configured"] = configured }, cts.Token);
                    return;
                }

                byte[] bodyBytes = [];
                if (request.HasEntityBody)
                {
                    using var ms = new MemoryStream();
                    await request.InputStream.CopyToAsync(ms, cts.Token);
                    bodyBytes = ms.ToArray();
                }
                var result = await router.HandleAsync(method, path, bodyBytes, cts.Token);
                response.StatusCode = result.Status;
                response.ContentType = result.ContentType;
                response.ContentLength64 = result.Body.Length;
                await response.OutputStream.WriteAsync(result.Body, cts.Token);
                response.OutputStream.Close();
                return;
            }

            if (path == "/configure" && method == "GET")
            {
                response.StatusCode = 200;
                response.ContentType = "text/html; charset=utf-8";
                byte[] page = Encoding.UTF8.GetBytes(ConfigurePage);
                response.ContentLength64 = page.Length;
                await response.OutputStream.WriteAsync(page, cts.Token);
                response.OutputStream.Close();
                return;
            }

            if (path == "/configure-save" && method == "POST")
            {
                using var ms = new MemoryStream();
                await request.InputStream.CopyToAsync(ms, cts.Token);
                var form = ParseForm(Encoding.UTF8.GetString(ms.ToArray()));
                var config = new SpeakCity.ApiConfig
                {
                    BaseUrl = form.GetValueOrDefault("baseUrl", "").Trim(),
                    Model = form.GetValueOrDefault("model", "").Trim(),
                    ApiKey = form.GetValueOrDefault("apiKey", "").Trim()
                };
                try
                {
                    SpeakCity.ApiConfigStore.Save(config);
                    configureGate?.TrySetResult(true);
                    response.StatusCode = 200;
                    response.ContentType = "text/html; charset=utf-8";
                    byte[] page = Encoding.UTF8.GetBytes(SavedPage);
                    response.ContentLength64 = page.Length;
                    await response.OutputStream.WriteAsync(page, cts.Token);
                    response.OutputStream.Close();
                }
                catch (Exception exc)
                {
                    response.StatusCode = 400;
                    response.ContentType = "text/plain; charset=utf-8";
                    byte[] err = Encoding.UTF8.GetBytes("Could not save: " + exc.Message);
                    response.ContentLength64 = err.Length;
                    await response.OutputStream.WriteAsync(err, cts.Token);
                    response.OutputStream.Close();
                }
                return;
            }

            string rel = path == "/" ? "index.html" : path.TrimStart('/');
            string file = Path.GetFullPath(Path.Combine(uiDir, rel));
            if (!file.StartsWith(uiDir, StringComparison.Ordinal) || !File.Exists(file))
            {
                response.StatusCode = 404;
                response.ContentType = "text/plain; charset=utf-8";
                var nf = Encoding.UTF8.GetBytes("Not found");
                response.ContentLength64 = nf.Length;
                await response.OutputStream.WriteAsync(nf, cts.Token);
                response.OutputStream.Close();
            }
            else
            {
                response.StatusCode = 200;
                response.ContentType = ContentTypeOf(file);
                var bytes = await File.ReadAllBytesAsync(file, cts.Token);
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, cts.Token);
                response.OutputStream.Close();
            }
        }
        catch (Exception) { try { ctx.Response.Abort(); } catch { } }
    });
}
return 0;
