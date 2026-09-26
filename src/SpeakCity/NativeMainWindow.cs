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
    private readonly StackPanel _chat = new();
    private readonly ScrollViewer _chatScroll = new();
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
    private string? _spokenFile;
    private string? _lastLucyLine;

    private readonly TextBlock _chatHint = new();
    private Border _missionCard = new();
    private readonly TextBlock _missionTitle = new();
    private readonly TextBlock _missionText = new();
    private readonly WrapPanel _hintPills = new();

    private string? _sessionId;
    private string? _scenarioId;
    private int _turnCount;
    // Voice warm-up: the first STT/TTS hashes 465 MB and loads Whisper/Kokoro.
    // Starting that in the background with visible status keeps the first
    // conversation turn from looking frozen. Results are stored for /api/bootstrap.
    private Task<JsonObject>? _voicePing;
    private volatile bool _voiceReady;

    private int _maxTurns = 8;
    public NativeMainWindow()
    {
        AppStartup.Note("startup", "native-window", "no browser component required");
        // Every spoken line is written to a temporary WAV; drop it when playback ends so a long
        // session cannot pile up files in the user's temp folder.
        _player.MediaEnded += (_, _) => RetireSpokenFile();
        _player.MediaFailed += (_, _) => RetireSpokenFile();
        Title = "SPEAKCITY AI — practise spoken English";
        Width = 1220;
        Height = 880;
        MinWidth = 940;
        MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = NativeTheme.Brush("Bg");
        Foreground = NativeTheme.Brush("Ink");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Resources.MergedDictionaries.Add(NativeTheme.Dictionary);

        var shell = new DockPanel { LastChildFill = true };

        // Header: brand mark, product name and the one action a new learner needs.
        var header = new Border
        {
            Background = NativeTheme.Brush("White"),
            BorderBrush = NativeTheme.Brush("Line"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 15, 24, 15)
        };
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brandRow = new StackPanel { Orientation = Orientation.Horizontal };
        brandRow.Children.Add(new Border
        {
            Background = NativeTheme.Brush("Teal"),
            CornerRadius = new CornerRadius(11),
            Width = 40,
            Height = 40,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Child = Emoji("🎤", 20, "White")
        });
        var brandText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var wordmark = new StackPanel { Orientation = Orientation.Horizontal };
        wordmark.Children.Add(new TextBlock { Text = "SPEAKCITY", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = NativeTheme.Brush("Ink") });
        wordmark.Children.Add(new TextBlock { Text = "AI", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = NativeTheme.Brush("Teal"), Margin = new Thickness(6, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top });
        brandText.Children.Add(wordmark);
        brandText.Children.Add(new TextBlock { Text = "A little practice. A bigger world.", FontSize = 11, Foreground = NativeTheme.Brush("Muted") });
        brandRow.Children.Add(brandText);
        headerGrid.Children.Add(brandRow);

        _configureButton.Content = "Configure AI";
        _configureButton.Style = NativeTheme.Style("OutlineButton");
        _configureButton.VerticalAlignment = VerticalAlignment.Center;
        _configureButton.Click += (_, _) => Configure();
        Grid.SetColumn(_configureButton, 2);
        headerGrid.Children.Add(_configureButton);
        header.Child = headerGrid;
        DockPanel.SetDock(header, Dock.Top);
        shell.Children.Add(header);

        // Status bar: the same status text as before, dressed up at the bottom.
        var statusBar = new Border
        {
            Background = NativeTheme.Brush("White"),
            BorderBrush = NativeTheme.Brush("Line"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 11, 24, 11)
        };
        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.TextWrapping = TextWrapping.Wrap;
        _status.FontSize = 12.5;
        _status.Foreground = NativeTheme.Brush("Muted");
        _status.VerticalAlignment = VerticalAlignment.Center;
        statusGrid.Children.Add(_status);
        var privacy = new TextBlock
        {
            Text = "Speech stays on this PC",
            FontSize = 11,
            Foreground = NativeTheme.Brush("Teal"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0)
        };
        Grid.SetColumn(privacy, 1);
        statusGrid.Children.Add(privacy);
        statusBar.Child = statusGrid;
        DockPanel.SetDock(statusBar, Dock.Bottom);
        shell.Children.Add(statusBar);

        // Body: places on the left, the conversation and its controls on the right.
        var body = new Grid { Margin = new Thickness(20) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(352) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var left = new Grid { Margin = new Thickness(0, 0, 18, 0) };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var placesLabel = new TextBlock { Text = "WHERE DO YOU WANT TO PRACTISE?", Style = NativeTheme.Style("Label"), Margin = new Thickness(2, 0, 0, 9) };
        left.Children.Add(placesLabel);

        _scenarios.Style = NativeTheme.Style("ScenarioList");
        _scenarios.ItemTemplate = ScenarioTemplate();
        _scenarios.SelectionChanged += (_, _) => { UpdateMission(); UpdateButtons(); };
        _scenarios.Margin = new Thickness(0, 0, 0, 14);
        Grid.SetRow(_scenarios, 1);
        left.Children.Add(_scenarios);

        _missionCard = BuildMissionCard();
        Grid.SetRow(_missionCard, 2);
        left.Children.Add(_missionCard);

        var choices = BuildChoices();
        Grid.SetRow(choices, 3);
        left.Children.Add(choices);

        _startButton.Content = "Start conversation";
        _startButton.Style = NativeTheme.Style("PrimaryButton");
        _startButton.Margin = new Thickness(0, 2, 0, 0);
        _startButton.Click += async (_, _) => await StartAsync();
        Grid.SetRow(_startButton, 4);
        left.Children.Add(_startButton);

        body.Children.Add(left);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        var chatCard = new Border { Style = NativeTheme.Style("Card"), Padding = new Thickness(18, 14, 12, 8) };
        var chatGrid = new Grid();
        _chatHint.Text = "Press Start conversation. Lucy will greet you, and you answer out loud or by typing.";
        _chatHint.FontSize = 13;
        _chatHint.TextWrapping = TextWrapping.Wrap;
        _chatHint.Foreground = NativeTheme.Brush("Muted");
        _chatHint.Margin = new Thickness(6, 10, 26, 0);
        _chatHint.VerticalAlignment = VerticalAlignment.Top;
        chatGrid.Children.Add(_chatHint);
        _chatScroll.Style = NativeTheme.Style("ScrollHost");
        _chatScroll.Padding = new Thickness(0, 0, 6, 0);
        _chatScroll.Content = _chat;
        chatGrid.Children.Add(_chatScroll);
        chatCard.Child = chatGrid;
        right.Children.Add(chatCard);

        BuildControls(right);
        shell.Children.Add(body);
        Content = shell;

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
    /// Emoji as text: no image file, no codec, no resource to lose in packaging.
    /// Segoe UI Emoji ships with Windows; a missing glyph degrades to a box,
    /// never to a crash.
    /// </summary>
    private static TextBlock Emoji(string glyph, double size, string color) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe UI Emoji"),
        FontSize = size,
        Foreground = NativeTheme.Brush(color),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

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
            _micButton.Content = "🎤  Speak";
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
            _micButton.Content = "⏹  Stop";
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
            // Checked before reading: an accidental long take must not be pulled into memory
            // just so the router can refuse it.
            if (new FileInfo(wavPath).Length > AppRouter.MaxBodyBytes)
            {
                SetStatus("That recording was too long. Press Speak again and keep the answer under about a minute.");
                return;
            }
            byte[] audio = await File.ReadAllBytesAsync(wavPath);
            // /api/stt takes raw WAV bytes, so the router is called directly.
            var response = _router is null ? null : await _router.HandleAsync("POST", "/api/stt", audio, _shutdown.Token);
            var payload = ReadJson(response);
            if (response is null || response.Status != 200 || payload is null)
            {
                SetStatus("Transcription failed: " + FailureText(response, payload));
                return;
            }
            string text = payload["text"] is JsonValue textValue && textValue.TryGetValue(out string? heard) ? heard?.Trim() ?? "" : "";
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
            if (response is null || response.Status != 200 || response.Body.Length <= 44)
            {
                // Surface the actual stage ("The bundled speech component is missing…",
                // "The local speech component took too long…") instead of failing silently.
                SetStatus("Lucy's voice could not play: " + FailureText(response, ReadJson(response)) + " Typing still works.");
                return;
            }
            string path = Path.Combine(Path.GetTempPath(), $"speakcity-tts-{Guid.NewGuid():N}.wav");
            await File.WriteAllBytesAsync(path, response.Body);
            RetireSpokenFile();
            _spokenFile = path;
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

    private void RetireSpokenFile()
    {
        if (_spokenFile is null) return;
        try { File.Delete(_spokenFile); }
        // Still held by the player: keep the path so the next spoken line tries again.
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        _spokenFile = null;
    }

    /// <summary>
    /// One background ping per window: warms the worker process and model loading
    /// before the first turn, and records whether voice can work at all. It never
    /// changes whether the learner can type.
    /// </summary>
    private async void StartWarmupAsync()
    {
        if (_router is null || _voicePing is not null) return;
        _voicePing = Task.Run(async () =>
        {
            var response = await CallAsync("GET", "/api/bootstrap");
            return ReadJson(response) ?? new JsonObject();
        });
        try
        {
            JsonObject bootstrap = await _voicePing;
            _voiceReady = bootstrap["tts_installed"]?.GetValue<bool>() == true && bootstrap["stt_installed"]?.GetValue<bool>() == true;
            if (_voiceReady)
                SetStatus(ApiConfigStore.IsConfigured(_config)
                    ? "Voice ready. Choose a place and press Start conversation."
                    : "Voice ready, but AI is not configured. Press Configure AI, test the connection, save, then Start.");
            else
            {
                SetStatus("Voice is unavailable: the bundled speech models did not load. Reinstall SPEAKCITY. Typing still works.");
                AppStartup.Note("speech-worker", "models-not-ready");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            _voiceReady = false;
            SetStatus("Voice is unavailable: the speech component could not start. Reinstall SPEAKCITY. Typing still works.");
        }
        UpdateButtons();
    }

    /// <summary>
    /// One scenario record from content/scenarios.json. The list used to hold
    /// plain "id — title" strings and the mission hint never reached the window;
    /// this record gives the card renderer an emoji, a role, the mission and the
    /// pipe-separated starter phrases, while SelectedScenarioId reads the exact id
    /// instead of reparsing the displayed text.
    /// </summary>
    private sealed record ScenarioItem(string Id, string Title, string Role, string Mission, string Hint)
    {
        public string Glyph => Id switch
        {
            "airport" => "✈️",
            "hotel" => "🏨",
            "cafe" => "☕",
            "shop" => "🛍️",
            "hospital" => "🏥",
            "directions" => "🗺️",
            "school" => "🏫",
            "interview" => "💼",
            _ => "💬"
        };

        public string Details => Role.Length == 0 ? "English speaking practice" : Role;
    }

    /// <summary>
    /// The visual for one scenario card: emoji medallion, scenario title and role.
    /// Separated from the data so a bad glyph can only affect the picture.
    /// </summary>
    private static DataTemplate ScenarioTemplate()
    {
        const string xaml = """
<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Grid Margin="1">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="Auto"/>
      <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>
    <Border Background="#FFE8F4EF" CornerRadius="9" Width="38" Height="38"
            VerticalAlignment="Top" Margin="0,0,11,0">
      <TextBlock Text="{Binding Glyph}" FontFamily="Segoe UI Emoji" FontSize="19"
                 HorizontalAlignment="Center" VerticalAlignment="Center"/>
    </Border>
    <StackPanel Grid.Column="1">
      <TextBlock Text="{Binding Title}" FontSize="15" FontWeight="SemiBold"
                 Foreground="#FF162F3C" TextWrapping="Wrap"/>
      <TextBlock Text="{Binding Details}" FontSize="11.5" Foreground="#FF5A727A"
                 TextWrapping="Wrap"/>
    </StackPanel>
  </Grid>
</DataTemplate>
""";
        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    /// <summary>
    /// The "Your mission" card below the scenario list: mission text plus one
    /// pill per pipe-separated starter phrase. Empty while the catalog loads.
    /// </summary>
    private Border BuildMissionCard()
    {
        _missionTitle.Style = NativeTheme.Style("Label");
        _missionTitle.Margin = new Thickness(0, 0, 0, 6);
        _missionText.Style = NativeTheme.Style("Body");
        _missionText.Margin = new Thickness(0, 0, 0, 10);
        _hintPills.Orientation = Orientation.Horizontal;
        _hintPills.Margin = new Thickness(0, 0, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(_missionTitle);
        stack.Children.Add(_missionText);
        stack.Children.Add(_hintPills);
        var card = new Border { Style = NativeTheme.Style("Card"), Margin = new Thickness(0, 0, 0, 14) };
        card.Child = stack;
        return card;
    }

    /// <summary>
    /// Level and feedback language pickers for the left column. Default WPF look,
    /// recoloured and padded to sit on the same cards as everything else.
    /// </summary>
    private StackPanel BuildChoices()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        foreach (var (label, box, items, selected) in new (string, ComboBox, string[], int)[]
        {
            ("LEVEL", _level, new[] { "A1", "A2" }, 1),
            ("FEEDBACK", _language, new[] { "en", "kk", "ru" }, 0)
        })
        {
            panel.Children.Add(new TextBlock { Text = label, Style = NativeTheme.Style("Label"), Margin = new Thickness(2, 0, 0, 5) });
            box.Items.Clear();
            foreach (string item in items) box.Items.Add(item);
            box.SelectedIndex = selected;
            box.MinWidth = 200;
            box.Padding = new Thickness(10, 7, 10, 7);
            box.FontSize = 13;
            box.Margin = new Thickness(0, 0, 0, 12);
            panel.Children.Add(box);
        }
        return panel;
    }

    /// <summary>
    /// Refreshes the mission card after the selection or the catalog changes.
    /// Reading the record instead of the displayed text keeps the exact id.
    /// </summary>
    private void UpdateMission()
    {
        var item = _scenarios.SelectedItem as ScenarioItem;
        _missionTitle.Text = item is null ? "MISSION" : item.Title.ToUpperInvariant();
        _missionText.Text = item switch
        {
            null => "The eight places are still loading.",
            { Mission.Length: > 0 } => item.Mission,
            _ => "Practise real conversation in English."
        };
        _hintPills.Children.Clear();
        if (item is not null && item.Hint.Length > 0)
        {
            foreach (string phrase in item.Hint.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string copy = phrase;
                var pill = new Border { Style = NativeTheme.Style("Pill"), Cursor = System.Windows.Input.Cursors.Hand };
                pill.Child = new TextBlock { Text = "\u201C" + copy + "\u201D", FontSize = 12, Foreground = NativeTheme.Brush("Teal") };
                pill.MouseLeftButtonUp += (_, _) => { _input.Text = copy; _input.Focus(); };
                pill.ToolTip = "Use this phrase as your answer";
                _hintPills.Children.Add(pill);
            }
        }
    }

    /// <summary>Clears the empty-state hint as soon as the first line arrives.</summary>
    private void UpdateChatHint() => _chatHint.Visibility = _chat.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void BuildControls(Grid right)
    {
        // Row 1 of the right column: voice controls.
        var voice = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 13, 0, 10) };
        _micButton.Content = "🎤  Speak";
        _micButton.Style = NativeTheme.Style("MicButton");
        _micButton.MinWidth = 152;
        _micButton.Margin = new Thickness(0, 0, 10, 0);
        _micButton.Click += async (_, _) => await ToggleMicrophoneAsync();
        voice.Children.Add(_micButton);

        _replayButton.Content = "↻  Replay Lucy";
        _replayButton.Style = NativeTheme.Style("OutlineButton");
        _replayButton.Margin = new Thickness(0, 0, 12, 0);
        _replayButton.Click += async (_, _) => await SpeakAsync(_lastLucyLine);
        voice.Children.Add(_replayButton);

        _speakReplies.Content = "Read Lucy aloud";
        _speakReplies.IsChecked = true;
        _speakReplies.Foreground = NativeTheme.Brush("Muted");
        _speakReplies.VerticalContentAlignment = VerticalAlignment.Center;
        voice.Children.Add(_speakReplies);
        Grid.SetRow(voice, 1);
        right.Children.Add(voice);

        // Row 2 of the right column: the learner's answer and its two actions.
        var send = new Grid { Margin = new Thickness(2, 0, 0, 0) };
        send.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        send.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        send.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _input.MinHeight = 42;
        _input.Margin = new Thickness(0, 0, 10, 0);
        _input.Style = NativeTheme.Style("InputBox");
        _input.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; await SendAsync(); } };
        send.Children.Add(_input);
        _sendButton.Content = "Send";
        _sendButton.Style = NativeTheme.Style("PrimaryButton");
        _sendButton.Margin = new Thickness(0, 0, 10, 0);
        _sendButton.Click += async (_, _) => await SendAsync();
        Grid.SetColumn(_sendButton, 1);
        send.Children.Add(_sendButton);
        _finishButton.Content = "Finish & feedback";
        _finishButton.Style = NativeTheme.Style("OutlineButton");
        _finishButton.Click += async (_, _) => await FinishAsync();
        Grid.SetColumn(_finishButton, 2);
        send.Children.Add(_finishButton);
        Grid.SetRow(send, 2);
        right.Children.Add(send);
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
                var node = pair.Value;
                string title = node?["title"]?["en"]?.GetValue<string>() ?? pair.Key;
                string role = node?["role"]?["en"]?.GetValue<string>() ?? "";
                string mission = node?["mission"]?["en"]?.GetValue<string>() ?? "";
                string hint = node?["hint"]?.GetValue<string>() ?? "";
                _scenarios.Items.Add(new ScenarioItem(pair.Key, title, role, mission, hint));
            }
            if (_scenarios.Items.Count > 0) _scenarios.SelectedIndex = 0;
            UpdateMission();

            _speech = new SpeechWorkerClient();
            _api = new ApiClient(() => _config);
            _router = new AppRouter(_speech, ConfigureTaskAsync, () => _config, _api.CompleteJsonAsync, catalogPath);
            if (_speech.WorkerPath is { Length: > 0 } && !File.Exists(_speech.WorkerPath))
            {
                SetStatus("Voice is unavailable: the bundled speech component is missing. Reinstall SPEAKCITY. Typing still works.");
                AppStartup.Note("speech-worker", "missing", _speech.ResolvedFrom);
            }
            else
            {
                StartWarmupAsync();
                SetStatus(ApiConfigStore.IsConfigured(_config)
                    ? "Voice engine starting… Choose a place and press Start conversation."
                    : "Voice engine starting… Press Configure AI, test the connection, save, then press Start conversation.");
            }
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
        // Items are ScenarioItem records now, so the exact catalog id is carried
        // by the item itself instead of being reparsed out of the displayed text.
        // Anything else falls back to the raw id for the old catalog shape.
        if (_scenarios.SelectedItem is ScenarioItem item) return item.Id;
        return _scenarios.SelectedItem as string ?? "";
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
            _chat.Children.Clear();
            UpdateChatHint();
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
            AddNote("— Feedback —", heading: true);
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
                    AddNote($"•  {original}   →   {corrected}");
                    if (explanation.Length > 0) AddNote(explanation);
                    corrections++;
                }
            }
            if (corrections == 0) AddNote("No corrections were found. Well done.");
            if (payload["vocabulary"] is JsonArray words)
            {
                AddNote($"— Vocabulary ({words.Count} words for {_scenarioId}) —", heading: true);
                foreach (var word in words)
                {
                    if (word is not JsonObject entry) continue;
                    string label = entry["word"]?.GetValue<string>() ?? "";
                    string meaning = entry["meaning"]?[language]?.GetValue<string>()
                        ?? entry["meaning"]?["en"]?.GetValue<string>() ?? "";
                    bool encountered = entry["encountered"]?.GetValue<bool>() ?? false;
                    AddNote($"• {label} — {meaning}{(encountered ? "  (used)" : "")}");
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
        AddBubble(who, text, who == "You" ? "you" : "lucy");
        ScrollChatToEnd();
    }

    private void ScrollChatToEnd() => _chatScroll.ScrollToEnd();

    /// <summary>Feedback and vocabulary lines: white cards, no name caption.</summary>
    private void AddNote(string text, bool heading = false) => AddBubble("", text, heading ? "heading" : "note");

    /// <summary>
    /// One bubble, built in C#. Lucy speaks on the left in mint, the learner on the
    /// right in teal, feedback is a white card with a teal spine for headings.
    /// Built here rather than in a XAML template: per-item styling in a DataTemplate
    /// needs converters or triggers, and this project loads its styles through
    /// XamlReader, which cannot resolve types from its own assembly.
    /// </summary>
    private void AddBubble(string who, string text, string kind)
    {
        var stack = new StackPanel();
        if (kind is "lucy" or "you")
        {
            stack.Children.Add(new TextBlock
            {
                Text = who,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = NativeTheme.Brush(kind == "you" ? "OnTeal" : "Teal"),
                Margin = new Thickness(0, 0, 0, 4)
            });
        }
        stack.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 21,
            FontWeight = kind == "heading" ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = NativeTheme.Brush(kind == "you" ? "White" : "Ink")
        });
        _chat.Children.Add(new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 14, 10),
            MaxWidth = 680,
            HorizontalAlignment = kind == "you" ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Margin = new Thickness(kind == "you" ? 60 : 0, 0, kind == "you" ? 0 : 60, 10),
            Background = NativeTheme.Brush(kind switch { "you" => "Teal", "lucy" => "Mint", _ => "White" }),
            BorderBrush = NativeTheme.Brush(kind == "note" ? "Line" : "Teal"),
            BorderThickness = kind switch
            {
                "heading" => new Thickness(4, 1, 1, 1),
                "note" => new Thickness(1),
                _ => new Thickness(0)
            }
        });
        UpdateChatHint();
        ScrollChatToEnd();
    }

    private void SetStatus(string text)
    {
        if (!_closing) _status.Text = text;
    }
}
