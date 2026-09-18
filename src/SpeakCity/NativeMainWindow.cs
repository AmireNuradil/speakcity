using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpeakCity;

/// <summary>
/// Native WPF window. No WebView2, Edge or msedgewebview2.exe.
/// It calls <see cref="AppRouter"/> directly, so the backend, speech worker,
/// API client and DPAPI settings are reused unchanged. The WebView2
/// MainWindow is deliberately left in place for now, for comparison.
/// </summary>
public sealed class NativeMainWindow : Window
{
    private readonly CancellationTokenSource _shutdown = new();
    private ApiConfig _config = ApiConfigStore.Load();
    private AppRouter? _router;
    private SpeechWorkerClient? _speech;
    private ApiClient? _api;
    private bool _closing;
    private bool _busy;

    private readonly ListBox _scenarios = new();
    private readonly ListBox _chat = new();
    private readonly TextBox _input = new();
    private readonly TextBlock _status = new();
    private readonly Button _startButton = new();
    private readonly Button _sendButton = new();
    private readonly Button _finishButton = new();
    private readonly Button _configureButton = new();
    private readonly ComboBox _level = new();
    private readonly ComboBox _language = new();
    private readonly System.Windows.Controls.Primitives.ToggleButton _micButton = new();
    private readonly Button _replayButton = new();
    private readonly CheckBox _speakReplies = new();
    private readonly WavRecorder _recorder = new();
    private readonly MediaPlayer _player = new();
    private string? _lastLucyLine;

    private string? _sessionId;
    private string? _scenarioId;
    private int _turnCount;
    private int _maxTurns = 8;
    public NativeMainWindow()
    {
        AppStartup.Note("startup", "native-window", "no browser component required");
        Title = "SPEAKCITY AI — native window";
        Width = 1120;
        Height = 820;
        MinWidth = 820;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;
        Foreground = Brushes.Black;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        UseLayoutRounding = true;

        var root = new Grid { Margin = new Thickness(18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var heading = new TextBlock
        {
            Text = "SPEAKCITY AI — native interface (no browser component)",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        };
        Grid.SetColumnSpan(heading, 2);
        root.Children.Add(heading);

        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(new TextBlock { Text = "Choose a place", Margin = new Thickness(0, 0, 0, 6), FontWeight = FontWeights.SemiBold });
        _scenarios.Height = 420;
        _scenarios.SelectionChanged += (_, _) => UpdateButtons();
        left.Children.Add(_scenarios);
        Grid.SetRow(left, 1);
        root.Children.Add(left);

        _chat.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(_chat, 1);
        Grid.SetColumn(_chat, 1);
        root.Children.Add(_chat);

        BuildControls(root);

        Loaded += async (_, _) => await InitializeAsync();
        Closing += (_, _) =>
        {
            _closing = true;
            _shutdown.Cancel();
            _router?.Dispose();
            _recorder.Dispose();
            _player.Close();
        };
        UpdateButtons();
    }

    /// <summary>
    /// One button, two halves: press to record, press again to stop and transcribe.
    /// The transcript goes into the editable box; nothing is sent automatically, so
    /// the learner stays in control of what leaves the machine.
    /// </summary>
    private async Task ToggleMicrophoneAsync()
    {
        if (_recorder.IsRecording)
        {
            _micButton.IsChecked = false;
            _micButton.Content = "Speak";
            if (!_recorder.Stop(out string? wavPath, out string stopError) || wavPath is null)
            {
                SetStatus(stopError);
                return;
            }
            await TranscribeAsync(wavPath);
            return;
        }
        if (_recorder.Start(out string startError))
        {
            _micButton.IsChecked = true;
            _micButton.Content = "Stop";
            SetStatus("Recording. Speak now, then press Stop to transcribe.");
        }
        else
        {
            _micButton.IsChecked = false;
            SetStatus(startError);
        }
    }

    private async Task TranscribeAsync(string wavPath)
    {
        _busy = true; UpdateButtons();
        try
        {
            byte[] audio = await File.ReadAllBytesAsync(wavPath);
            // /api/stt takes raw WAV bytes, so the router is called directly.
            var response = _router is null ? null : await _router.HandleAsync("POST", "/api/stt", audio, _shutdown.Token);
            var payload = ReadJson(response);
            if (response is null || response.Status != 200 || payload is null)
            {
                SetStatus("Transcription failed: " + FailureText(response, payload));
                return;
            }
            string text = payload["text"]?.GetValue<string>()?.Trim() ?? "";
            if (text.Length == 0)
            {
                SetStatus("No speech was recognised. Try again, closer to the microphone.");
                return;
            }
            _input.Text = text;
            _input.CaretIndex = text.Length;
            SetStatus("Transcribed. Edit it if needed, then press Send.");
            _input.Focus();
        }
        catch (OperationCanceledException) { SetStatus("Transcription canceled."); }
        catch (Exception) { SetStatus("Transcription failed. Check the microphone and try again."); }
        finally
        {
            _busy = false; UpdateButtons();
            try { File.Delete(wavPath); } catch (IOException) { }
        }
    }

    /// <summary>Plays a line through the bundled Kokoro voice. Typing must still work.</summary>
    private async Task SpeakAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _router is null) return;
        if (text.Length > 1200) text = text[..1200];
        try
        {
            var body = new JsonObject { ["text"] = text, ["voice"] = "american", ["speed"] = 0.95 };
            var response = await CallAsync("POST", "/api/tts", body);
            if (response is null || response.Status != 200 || response.Body.Length <= 44) return;
            string path = Path.Combine(Path.GetTempPath(), $"speakcity-tts-{Guid.NewGuid():N}.wav");
            await File.WriteAllBytesAsync(path, response.Body);
            _player.Open(new Uri(path));
            _player.Play();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // A missing or busy voice must never block the conversation.
            SetStatus("Lucy's voice could not play. Typing still works.");
        }
    }

    private void BuildControls(Grid root)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        _level.Items.Add("A1");
        _level.Items.Add("A2");
        _level.SelectedIndex = 1;
        _level.Width = 72;
        _level.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(new TextBlock { Text = "Level", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(_level);

        _language.Items.Add("en");
        _language.Items.Add("kk");
        _language.Items.Add("ru");
        _language.SelectedIndex = 0;
        _language.Width = 72;
        _language.Margin = new Thickness(0, 0, 14, 0);
        row.Children.Add(new TextBlock { Text = "Feedback", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(_language);

        _startButton.Content = "Start conversation";
        _startButton.Padding = new Thickness(16, 5, 16, 5);
        _startButton.Margin = new Thickness(0, 0, 10, 0);
        _startButton.Click += async (_, _) => await StartAsync();
        row.Children.Add(_startButton);

        _configureButton.Content = "Configure AI";
        _configureButton.Padding = new Thickness(16, 5, 16, 5);
        _configureButton.Click += (_, _) => Configure();
        row.Children.Add(_configureButton);

        _micButton.Content = "Speak";
        _micButton.Padding = new Thickness(16, 5, 16, 5);
        _micButton.Margin = new Thickness(10, 0, 10, 0);
        _micButton.Click += async (_, _) => await ToggleMicrophoneAsync();
        row.Children.Add(_micButton);

        _replayButton.Content = "Replay Lucy";
        _replayButton.Padding = new Thickness(16, 5, 16, 5);
        _replayButton.Margin = new Thickness(0, 0, 10, 0);
        _replayButton.Click += async (_, _) => await SpeakAsync(_lastLucyLine);
        row.Children.Add(_replayButton);

        _speakReplies.Content = "Read Lucy aloud";
        _speakReplies.IsChecked = true;
        _speakReplies.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_speakReplies);

        Grid.SetRow(row, 2);
        Grid.SetColumn(row, 1);
        root.Children.Add(row);

        var send = new Grid();
        send.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        send.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        send.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _input.MinHeight = 30;
        _input.Margin = new Thickness(0, 0, 10, 0);
        _input.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await SendAsync(); } };
        send.Children.Add(_input);
        _sendButton.Content = "Send";
        _sendButton.Padding = new Thickness(16, 5, 16, 5);
        _sendButton.Margin = new Thickness(0, 0, 10, 0);
        _sendButton.Click += async (_, _) => await SendAsync();
        Grid.SetColumn(_sendButton, 1);
        send.Children.Add(_sendButton);
        _finishButton.Content = "Finish & feedback";
        _finishButton.Padding = new Thickness(16, 5, 16, 5);
        _finishButton.Click += async (_, _) => await FinishAsync();
        Grid.SetColumn(_finishButton, 2);
        send.Children.Add(_finishButton);
        Grid.SetRow(send, 2);
        root.Children.Add(send);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(_status, 3);
        Grid.SetColumnSpan(_status, 2);
        root.Children.Add(_status);
    }

private async Task InitializeAsync()
    {
        try
        {
            string catalogPath = Path.Combine(AppContext.BaseDirectory, "content", "scenarios.json");
            if (!File.Exists(catalogPath))
            {
                SetStatus("The scenario package is missing from the installed folder. Reinstall SPEAKCITY using the official installer.");
                return;
            }
            var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))?.AsObject();
            if (catalog is null || catalog.Count != 8)
            {
                SetStatus("The scenario package is invalid. Reinstall SPEAKCITY.");
                return;
            }
            _scenarios.Items.Clear();
            foreach (var pair in catalog)
            {
                string title = pair.Value?["title"]?["en"]?.GetValue<string>() ?? pair.Key;
                _scenarios.Items.Add($"{pair.Key} — {title}");
            }
            if (_scenarios.Items.Count > 0) _scenarios.SelectedIndex = 0;

            _speech = new SpeechWorkerClient();
            _api = new ApiClient(() => _config);
            _router = new AppRouter(_speech, ConfigureTaskAsync, () => _config, _api.CompleteJsonAsync, catalogPath);
            SetStatus(ApiConfigStore.IsConfigured(_config)
                ? "Ready. Choose a place and press Start conversation."
                : "Press Configure AI, test the connection, save, then press Start conversation.");
        }
        catch (Exception)
        {
            SetStatus("The native window could not prepare itself. Reinstall SPEAKCITY. No browser component is required.");
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool hasScenario = _scenarios.SelectedItem is not null;
        bool hasSession = !string.IsNullOrEmpty(_sessionId);
        _startButton.IsEnabled = !_busy && hasScenario;
        _sendButton.IsEnabled = !_busy && hasSession;
        _finishButton.IsEnabled = !_busy && hasSession;
        _input.IsEnabled = !_busy && hasSession;
        _micButton.IsEnabled = !_busy && hasSession;
        _replayButton.IsEnabled = !_busy && _lastLucyLine is not null;
        _configureButton.IsEnabled = !_busy;
    }

    private string SelectedScenarioId()
    {
        string item = _scenarios.SelectedItem as string ?? "";
        // Items are rendered as "<id> — <title>"; keep only the id part.
        int separator = item.IndexOf(" — ", StringComparison.Ordinal);
        return separator > 0 ? item[..separator] : item;
    }
    private Task<bool> ConfigureTaskAsync()
    {
        Configure();
        return Task.FromResult(ApiConfigStore.IsConfigured(_config));
    }

    private void Configure()
    {
        var dialog = new ApiSettingsWindow(_config) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Saved) _config = ApiConfigStore.Load();
        SetStatus(ApiConfigStore.IsConfigured(_config)
            ? "AI settings saved. Choose a place and press Start conversation."
            : "AI is not configured yet. Open Configure AI again.");
        UpdateButtons();
    }

    private async Task<AppResponse?> CallAsync(string method, string path, JsonObject? body = null)
    {
        if (_router is null) { SetStatus("The native window is not ready yet."); return null; }
        byte[] payload = body is null ? [] : System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
        return await _router.HandleAsync(method, path, payload, _shutdown.Token);
    }

    private static JsonObject? ReadJson(AppResponse? response)
    {
        if (response is null) return null;
        try { return JsonNode.Parse(response.Body)?.AsObject(); }
        catch { return null; }
    }

    private static string FailureText(AppResponse? response, JsonObject? payload)
    {
        string detail = payload?["detail"]?.GetValue<string>() ?? "";
        if (detail.Length > 0) return detail;
        return response is null ? "No response." : $"HTTP {response.Status}.";
    }

private async Task StartAsync()
    {
        if (_busy) return;
        _busy = true; UpdateButtons();
        try
        {
            _scenarioId = SelectedScenarioId();
            string level = _level.SelectedItem as string ?? "A2";
            var body = new JsonObject { ["scenario"] = _scenarioId, ["level"] = level, ["language"] = "en" };
            var response = await CallAsync("POST", "/api/sessions", body);
            var payload = ReadJson(response);
            if (response is null || response.Status != 200 || payload is null)
            {
                SetStatus("Could not start: " + FailureText(response, payload));
                return;
            }
            _sessionId = payload["session_id"]?.GetValue<string>();
            _turnCount = payload["turn_count"]?.GetValue<int>() ?? 0;
            _maxTurns = payload["max_turns"]?.GetValue<int>() ?? 8;
            _chat.Items.Clear();
            _lastLucyLine = payload["reply"]?.GetValue<string>() ?? "";
            AddMessage("Lucy", _lastLucyLine);
            if (_speakReplies.IsChecked == true) _ = SpeakAsync(_lastLucyLine);
            SetStatus($"{_scenarioId}, level {level}. Turn {_turnCount}/{_maxTurns}. Type your answer and press Send.");
            _input.Focus();
        }
        catch (OperationCanceledException) { SetStatus("Start canceled."); }
        catch (Exception) { SetStatus("Could not start the conversation. Check your API settings and try again."); }
        finally { _busy = false; UpdateButtons(); }
    }
private async Task SendAsync()
    {
        if (_busy) return;
        if (string.IsNullOrEmpty(_sessionId)) { SetStatus("Press Start conversation first."); return; }
        string text = _input.Text.Trim();
        if (text.Length == 0) return;
        _busy = true; UpdateButtons();
        try
        {
            AddMessage("You", text);
            _input.Clear();
            var body = new JsonObject { ["text"] = text, ["request_id"] = Guid.NewGuid().ToString("N") };
            var response = await CallAsync("POST", $"/api/sessions/{_sessionId}/turn", body);
            var payload = ReadJson(response);
            if (response is null || response.Status != 200 || payload is null)
            {
                SetStatus("Send failed: " + FailureText(response, payload));
                return;
            }
            _turnCount = payload["turn_count"]?.GetValue<int>() ?? _turnCount + 1;
            _lastLucyLine = payload["reply"]?.GetValue<string>() ?? "";
            AddMessage("Lucy", _lastLucyLine);
            if (_speakReplies.IsChecked == true) _ = SpeakAsync(_lastLucyLine);
            bool atLimit = payload["at_limit"]?.GetValue<bool>() ?? _turnCount >= _maxTurns;
            SetStatus(atLimit
                ? $"Turn limit reached ({_turnCount}/{_maxTurns}). Press Finish & feedback."
                : $"Turn {_turnCount}/{_maxTurns}.");
            _input.Focus();
        }
        catch (OperationCanceledException) { SetStatus("Send canceled."); }
        catch (Exception) { SetStatus("Send failed. Check the connection and try again."); }
        finally { _busy = false; UpdateButtons(); }
    }
private async Task FinishAsync()
    {
        if (_busy) return;
        if (string.IsNullOrEmpty(_sessionId)) { SetStatus("Press Start conversation first."); return; }
        _busy = true; UpdateButtons();
        try
        {
            string language = _language.SelectedItem as string ?? "en";
            var body = new JsonObject { ["language"] = language };
            var response = await CallAsync("POST", $"/api/sessions/{_sessionId}/finish", body);
            var payload = ReadJson(response);
            if (response is null || response.Status != 200 || payload is null)
            {
                SetStatus("Feedback failed: " + FailureText(response, payload));
                return;
            }
            _chat.Items.Add("");
            _chat.Items.Add("— Feedback —");
            int corrections = 0;
            if (payload["corrections"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is not JsonObject entry) continue;
                    string original = entry["original"]?.GetValue<string>() ?? "";
                    string corrected = entry["corrected"]?.GetValue<string>() ?? "";
                    string explanation = entry["explanation"]?[language]?.GetValue<string>()
                        ?? entry["explanation"]?["en"]?.GetValue<string>() ?? "";
                    _chat.Items.Add($"• {original}  →  {corrected}");
                    if (explanation.Length > 0) _chat.Items.Add($"    {explanation}");
                    corrections++;
                }
            }
            if (corrections == 0) _chat.Items.Add("No corrections were found. Well done.");
            if (payload["vocabulary"] is JsonArray words)
            {
                _chat.Items.Add($"— Vocabulary ({words.Count} words for {_scenarioId}) —");
                foreach (var word in words)
                {
                    if (word is not JsonObject entry) continue;
                    string label = entry["word"]?.GetValue<string>() ?? "";
                    string meaning = entry["meaning"]?[language]?.GetValue<string>()
                        ?? entry["meaning"]?["en"]?.GetValue<string>() ?? "";
                    bool encountered = entry["encountered"]?.GetValue<bool>() ?? false;
                    _chat.Items.Add($"• {label} — {meaning}{(encountered ? "  (used)" : "")}");
                }
            }
            ScrollChatToEnd();
            _sessionId = null;
            SetStatus("Conversation finished. Press Start conversation to practise again.");
        }
        catch (OperationCanceledException) { SetStatus("Feedback canceled."); }
        catch (Exception) { SetStatus("Feedback failed. Try again."); }
        finally { _busy = false; UpdateButtons(); }
    }
private void AddMessage(string who, string text)
    {
        _chat.Items.Add($"{who}: {text}");
        ScrollChatToEnd();
    }

    private void ScrollChatToEnd()
    {
        if (_chat.Items.Count > 0) _chat.ScrollIntoView(_chat.Items[^1]);
    }

    private void SetStatus(string text)
    {
        if (!_closing) _status.Text = text;
    }
}
