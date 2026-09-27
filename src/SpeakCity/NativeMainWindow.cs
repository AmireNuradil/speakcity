using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace SpeakCity;

/// <summary>
/// Native WPF window. No WebView2, Edge or msedgewebview2.exe.
/// It calls <see cref="AppRouter"/> directly, so the backend, speech worker,
/// API client and DPAPI settings are reused unchanged. The WebView2
/// MainWindow is deliberately left in place for comparison (--webview).
/// Like the web pages it mirrors, the window has two views: the city (map with
/// eight pins, Lucy, set-up) and the practice screen (scene portrait and chat).
/// </summary>
public sealed class NativeMainWindow : Window
{
    private const double MapWidth = 1000;
    private const double MapHeight = 558;
    private const string Thinking = "Lucy is thinking…";
    private const string LucyAlt = "Lucy, your illustrated AI conversation partner";
    private const string CityAlt = "Illustrated city for choosing any of eight scenarios. The Directions scene has its own exact schematic.";
    private const string MicIdleCaption = "Press, answer out loud, press again. You can edit the words before sending.";

    // Pin centres in percent of the city picture, from ui/app.css (.pin-*), in the web's tab order.
    private static readonly (string Id, double X, double Y)[] PinLayout =
    [
        ("airport", 23, 36), ("hotel", 57, 34), ("school", 85, 34), ("hospital", 15, 67),
        ("cafe", 50, 66), ("shop", 79, 62), ("directions", 28, 91), ("interview", 85, 93)
    ];

    // ui/i18n.js noErrors + noErrorsNote: an empty list is not proof of perfect English.
    private static readonly Dictionary<string, (string Headline, string Note)> NoCorrections = new()
    {
        ["en"] = ("No clear grammar corrections were returned.",
            "This does not mean every sentence was perfect. AI can miss mistakes; ask a teacher if something seems unclear."),
        ["kk"] = ("ЖИ нақты грамматикалық түзету ұсынбады.",
            "Бұл барлық сөйлем мінсіз дегенді білдірмейді. ЖИ қатені байқамауы мүмкін; түсініксіз болса, мұғалімнен сұраңыз."),
        ["ru"] = ("ИИ не предложил явных грамматических исправлений.",
            "Это не означает, что все предложения идеальны. ИИ может пропустить ошибки; если что-то неясно, спросите учителя.")
    };

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<string, IReadOnlyList<(string Role, string Content)>, int, CancellationToken, Task<JsonObject>>? _completeOverride;
    private readonly Func<byte[], Task<bool>>? _playOverride;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ApiConfig _config;
    private AppRouter? _router;
    private SpeechWorkerClient? _speech;
    private ApiClient? _api;
    private bool _closing;
    private bool _busy;

    private readonly List<ScenarioItem> _places = new();
    private readonly List<Button> _pins = new();
    private ScenarioItem? _selected;
    private readonly Grid _homeView = new();
    private readonly Grid _practiceView = new();
    private bool _inPractice;
    private readonly BitmapSource? _lucyPicture = NativeAssets.TryLoad(NativeAssets.LucyPicture);
    private BitmapSource? _cityPicture;
    private BitmapSource? _portraitPicture;
    private readonly Border _portrait = new();
    private readonly TextBlock _portraitRole = new();
    private readonly Border _onboarding = new();

    private readonly StackPanel _chat = new();
    private readonly ScrollViewer _chatScroll = new();
    private readonly TextBlock _chatHint = new();
    private readonly TextBlock _chatRole = new();
    private readonly TextBlock _turnChip = new();
    private readonly TextBox _input = new();
    private readonly TextBlock _status = new();
    private readonly Button _startButton = new();
    private readonly Button _restartButton = new();
    private readonly Button _backButton = new();
    private readonly Button _sendButton = new();
    private readonly Button _finishButton = new();
    private readonly Button _configureButton = new();
    private readonly ComboBox _level = new();
    private readonly ComboBox _language = new();
    private readonly ToggleButton _micButton = new();
    private readonly ScaleTransform _micPulse = new(1, 1);
    private readonly TextBlock _micLabel = new();
    private readonly TextBlock _micCaption = new();
    private readonly Button _replayButton = new();
    private readonly CheckBox _speakReplies = new();
    private readonly WavRecorder _recorder = new();
    private FrameworkElement? _thinking;

    private readonly TextBlock _missionTitle = new();
    private readonly TextBlock _missionRole = new();
    private readonly TextBlock _missionText = new();
    private readonly TextBlock _practiceMissionText = new();
    private readonly WrapPanel _hintPills = new();

    private readonly Border _toast = new();
    private readonly TextBlock _toastText = new();
    private readonly TranslateTransform _toastShift = new(0, 20);
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };

    private readonly MediaPlayer _player = new();
    private readonly List<string> _spokenFiles = new();
    private readonly Dictionary<string, byte[]> _voiceCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _voiceCacheOrder = new();
    private TaskCompletionSource<bool>? _playing;
    private int _speechGeneration;
    private int _voicedSentences;
    private int _playbackFailures;
    private TimeSpan? _firstVoiceDelay;
    private Task? _currentSpeech;
    // Voice warm-up: the bootstrap ping only verifies the ~465 MB of model files; Kokoro
    // and Whisper load on first use. The warm-up makes that first use happen in the
    // background, so neither Lucy's greeting nor the first recording pays for it.
    private Task<JsonObject>? _voicePing;
    private Task? _warmup;
    private volatile bool _voiceReady;
    private string? _lastLucyLine;

    private string? _sessionId;
    private string? _scenarioId;
    private int _turnCount;
    private int _maxTurns = 8;
    private bool _feedbackShown;

    public NativeMainWindow() : this(null, null) { }

    /// <param name="config">Smoke-test hook: settings to use instead of the saved DPAPI file.</param>
    /// <param name="complete">Smoke-test hook: a scripted AI completion; the app itself never passes one.</param>
    /// <param name="play">Smoke-test hook: stands in for the speakers on a build agent that has none.</param>
    internal NativeMainWindow(ApiConfig? config,
        Func<string, IReadOnlyList<(string Role, string Content)>, int, CancellationToken, Task<JsonObject>>? complete,
        Func<byte[], Task<bool>>? play = null)
    {
        _config = config ?? ApiConfigStore.Load();
        _completeOverride = complete;
        _playOverride = play;
        AppStartup.Note("startup", "native-window", "no browser component required");
        // Every spoken sentence is a temporary WAV; they are deleted as soon as the player lets go.
        _player.MediaEnded += (_, _) => { _playing?.TrySetResult(true); RetireSpokenFiles(); };
        _player.MediaFailed += (_, e) =>
        {
            _playbackFailures++;
            AppStartup.Note("voice", "playback-failed", e.ErrorException?.GetType().Name);
            _playing?.TrySetResult(false);
            RetireSpokenFiles();
        };
        // The recorder stops itself at the speech worker's limit; finish the take as if Stop was pressed.
        _recorder.LimitReached += () => Dispatcher.BeginInvoke(async () =>
        {
            if (!_recorder.IsRecording || _closing) return;
            ShowToast("Recording stopped at the 30-second limit. Review your words before continuing.");
            await ToggleMicrophoneAsync();
        });
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
        shell.Children.Add(BuildHeader());
        shell.Children.Add(BuildStatusBar());
        var body = new Grid { Margin = new Thickness(20) };
        BuildHomeView();
        BuildPracticeView();
        body.Children.Add(_homeView);
        body.Children.Add(_practiceView);
        shell.Children.Add(body);

        // The root carries the background so a rendered snapshot matches the window.
        var root = new Grid { Background = NativeTheme.Brush("Bg") };
        root.Children.Add(shell);
        root.Children.Add(BuildToast());
        Content = root;
        ShowView(practice: false);

        Loaded += async (_, _) => await InitializeAsync();
        Closing += (_, _) =>
        {
            _closing = true;
            _speechGeneration++;
            _playing?.TrySetResult(false);
            _toastTimer.Stop();
            _shutdown.Cancel();
            _router?.Dispose();
            _recorder.Dispose();
            _player.Close();
            RetireSpokenFiles();
        };
        UpdateButtons();
    }

    // ---- Layout -------------------------------------------------------------

    private Border BuildHeader()
    {
        var header = new Border
        {
            Background = NativeTheme.Brush("White"),
            BorderBrush = NativeTheme.Brush("Line"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 15, 24, 15)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
        grid.Children.Add(brandRow);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _backButton.Content = "←  Back to city";
        _backButton.Style = NativeTheme.Style("OutlineButton");
        _backButton.Margin = new Thickness(0, 0, 10, 0);
        _backButton.Click += (_, _) => BackToCity();
        actions.Children.Add(_backButton);
        _configureButton.Content = "Configure AI";
        _configureButton.Style = NativeTheme.Style("OutlineButton");
        _configureButton.Click += (_, _) => Configure();
        actions.Children.Add(_configureButton);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        header.Child = grid;
        DockPanel.SetDock(header, Dock.Top);
        return header;
    }

    private Border BuildStatusBar()
    {
        var statusBar = new Border
        {
            Background = NativeTheme.Brush("White"),
            BorderBrush = NativeTheme.Brush("Line"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 11, 24, 11)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.TextWrapping = TextWrapping.Wrap;
        _status.FontSize = 12.5;
        _status.Foreground = NativeTheme.Brush("Muted");
        _status.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_status);
        var privacy = new TextBlock
        {
            Text = "Speech stays on this PC",
            FontSize = 11,
            Foreground = NativeTheme.Brush("Teal"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0)
        };
        Grid.SetColumn(privacy, 1);
        grid.Children.Add(privacy);
        statusBar.Child = grid;
        DockPanel.SetDock(statusBar, Dock.Bottom);
        return statusBar;
    }

    /// <summary>The city: Lucy and the set-up on the left, the map of eight places on the right.</summary>
    private void BuildHomeView()
    {
        _homeView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(352) });
        _homeView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        left.Children.Add(BuildLucyCard());
        left.Children.Add(BuildMissionCard());
        left.Children.Add(BuildChoices());
        _startButton.Content = "Start conversation";
        _startButton.Style = NativeTheme.Style("PrimaryButton");
        _startButton.Margin = new Thickness(0, 2, 0, 4);
        _startButton.Click += async (_, _) => await StartAsync();
        left.Children.Add(_startButton);
        _homeView.Children.Add(new ScrollViewer { Style = NativeTheme.Style("ScrollHost"), Content = left, Margin = new Thickness(0, 0, 8, 0) });

        var right = new Grid();
        for (int row = 0; row < 5; row++) right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new TextBlock { Text = "WHERE DO YOU WANT TO PRACTISE?", Style = NativeTheme.Style("Label"), Margin = new Thickness(2, 0, 0, 9) };
        right.Children.Add(heading);
        BuildOnboarding();
        Grid.SetRow(_onboarding, 1);
        right.Children.Add(_onboarding);
        var map = BuildMap();
        Grid.SetRow(map, 2);
        right.Children.Add(map);
        var note = new TextBlock
        {
            Text = "📍  Eight places. Eight real conversations. Choose any location.",
            FontSize = 12,
            Foreground = NativeTheme.Brush("Muted"),
            Margin = new Thickness(4, 10, 0, 0)
        };
        Grid.SetRow(note, 3);
        right.Children.Add(note);
        var how = BuildHowItWorks();
        Grid.SetRow(how, 4);
        right.Children.Add(how);
        Grid.SetColumn(right, 1);
        _homeView.Children.Add(right);

        // The map takes the width it is given, but never more height than is left after the
        // heading, note and steps, so those sit directly under it at any window size.
        void FitMap()
        {
            static double Outer(FrameworkElement element) =>
                element.IsVisible ? element.ActualHeight + element.Margin.Top + element.Margin.Bottom : 0;
            double spare = right.ActualHeight - Outer(heading) - Outer(_onboarding) - Outer(note) - Outer(how);
            double byWidth = right.ActualWidth * MapHeight / MapWidth;
            if (right.ActualWidth > 0) map.Height = Math.Max(150, Math.Min(byWidth, spare));
        }
        right.SizeChanged += (_, _) => FitMap();
        _onboarding.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(FitMap, DispatcherPriority.Loaded);
    }

    private Border BuildLucyCard()
    {
        var photo = new Border { Height = 200, CornerRadius = new CornerRadius(14, 14, 0, 0), Background = Hex("#FFDAF0F0") };
        AutomationProperties.SetName(photo, LucyAlt);
        NativeAssets.PaintCover(photo, _lucyPicture, 0.5, 0.12);
        var copy = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };
        copy.Children.Add(new TextBlock { Text = "YOUR CONVERSATION PARTNER", Style = NativeTheme.Style("Label"), FontSize = 10.5 });
        copy.Children.Add(new TextBlock { Text = "Lucy", FontSize = 24, FontWeight = FontWeights.Bold, Foreground = NativeTheme.Brush("Ink"), Margin = new Thickness(0, 2, 0, 4) });
        copy.Children.Add(new TextBlock { Text = "“You don’t need perfect English. Let’s practise together.”", Style = NativeTheme.Style("Body"), Foreground = NativeTheme.Brush("Muted") });
        var stack = new StackPanel();
        stack.Children.Add(photo);
        stack.Children.Add(copy);
        return new Border { Style = NativeTheme.Style("Card"), Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 14), Child = stack };
    }

    /// <summary>The chosen place: title, Lucy's role there and the mission, refreshed on every pin.</summary>
    private Border BuildMissionCard()
    {
        _missionTitle.Style = NativeTheme.Style("Label");
        _missionTitle.Margin = new Thickness(0, 0, 0, 3);
        _missionRole.FontSize = 11.5;
        _missionRole.Foreground = NativeTheme.Brush("Teal");
        _missionRole.TextWrapping = TextWrapping.Wrap;
        _missionRole.Margin = new Thickness(0, 0, 0, 7);
        _missionText.Style = NativeTheme.Style("Body");
        var stack = new StackPanel();
        stack.Children.Add(_missionTitle);
        stack.Children.Add(_missionRole);
        stack.Children.Add(_missionText);
        return new Border { Style = NativeTheme.Style("Card"), Margin = new Thickness(0, 0, 0, 14), Child = stack };
    }

    /// <summary>
    /// Level and feedback language pickers for the left column. Default WPF look,
    /// recoloured and padded to sit on the same cards as everything else.
    /// </summary>
    private StackPanel BuildChoices()
    {
        var panel = new StackPanel();
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

    /// <summary>Shown until AI is configured: the web's onboarding card with its teal spine.</summary>
    private void BuildOnboarding()
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "ALL 8 PLACES ARE OPEN", Style = NativeTheme.Style("Label"), FontSize = 10.5 });
        stack.Children.Add(new TextBlock { Text = "Your city is ready. Connect your AI.", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = NativeTheme.Brush("Ink"), Margin = new Thickness(0, 3, 0, 4) });
        stack.Children.Add(new TextBlock { Text = "1. Open Configure AI. 2. Save your provider settings in Windows. 3. Choose any of the eight places and start talking.", Style = NativeTheme.Style("Body"), Foreground = NativeTheme.Brush("Muted") });
        var configure = new Button { Content = "Configure AI", Style = NativeTheme.Style("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(16, 8, 16, 8) };
        configure.Click += (_, _) => Configure();
        stack.Children.Add(configure);
        _onboarding.Background = NativeTheme.Brush("White");
        _onboarding.BorderBrush = NativeTheme.Brush("Teal");
        _onboarding.BorderThickness = new Thickness(4, 0, 0, 0);
        _onboarding.CornerRadius = new CornerRadius(12);
        _onboarding.Padding = new Thickness(18, 14, 18, 14);
        _onboarding.Margin = new Thickness(0, 0, 0, 12);
        _onboarding.Child = stack;
    }

    /// <summary>
    /// The city picture with one button per place, laid out on a fixed 1000-wide
    /// surface and scaled by a Viewbox, so pins stay on their buildings at any size.
    /// </summary>
    private FrameworkElement BuildMap()
    {
        var surface = new Canvas { Width = MapWidth, Height = MapHeight };
        foreach (var (id, x, y) in PinLayout)
        {
            var pin = new Button { Style = NativeTheme.Style("Pin"), Tag = id, Visibility = Visibility.Collapsed };
            double centreX = MapWidth * x / 100, centreY = MapHeight * y / 100;
            // Centred on its point like the web's translate(-50%,-50%), whatever its size.
            pin.SizeChanged += (_, e) =>
            {
                Canvas.SetLeft(pin, centreX - e.NewSize.Width / 2);
                Canvas.SetTop(pin, centreY - e.NewSize.Height / 2);
            };
            pin.Click += (_, _) =>
            {
                SelectPlace(id);
                if (_startButton.IsEnabled) _startButton.Focus();
            };
            _pins.Add(pin);
            surface.Children.Add(pin);
        }
        var map = new Border
        {
            Width = MapWidth,
            Height = MapHeight,
            CornerRadius = new CornerRadius(19),
            BorderBrush = Hex("#FFACCCC0"),
            BorderThickness = new Thickness(1),
            Background = Hex("#FFBFE1D2"),
            Child = surface
        };
        _cityPicture = NativeAssets.TryLoad(NativeAssets.CityPicture);
        NativeAssets.PaintCover(map, _cityPicture, 0.5, 0.5);
        AutomationProperties.SetName(map, CityAlt);
        return new Viewbox { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = map };
    }

    private Border BuildHowItWorks()
    {
        var grid = new Grid();
        string[][] steps =
        [
            ["Choose your setting", "Explore all eight places in your city."],
            ["Have a real conversation", "Configure AI once. Lucy responds to what you actually say."],
            ["Learn from your words", "Review grammar and save useful phrases afterward."]
        ];
        for (int index = 0; index < steps.Length; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var row = new Grid { Margin = new Thickness(0, 0, 18, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Border
            {
                Width = 27,
                Height = 27,
                CornerRadius = new CornerRadius(13.5),
                BorderBrush = Hex("#FFBFD8CF"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock { Text = $"0{index + 1}", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = NativeTheme.Brush("Teal"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = steps[index][0], FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = NativeTheme.Brush("Ink") });
            text.Children.Add(new TextBlock { Text = steps[index][1], FontSize = 11.5, Foreground = NativeTheme.Brush("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            Grid.SetColumn(row, index);
            grid.Children.Add(row);
        }
        return new Border
        {
            BorderBrush = NativeTheme.Brush("Line"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(2, 14, 0, 0),
            Margin = new Thickness(0, 14, 0, 0),
            Child = grid
        };
    }

    /// <summary>The practice screen: scene portrait and mission on the left, the conversation on the right.</summary>
    private void BuildPracticeView()
    {
        _practiceView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(318) });
        _practiceView.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _portrait.Height = 360;
        _portrait.CornerRadius = new CornerRadius(16, 16, 0, 0);
        _portrait.Background = Hex("#FFDAF0F0");
        var caption = new StackPanel { Margin = new Thickness(20, 13, 20, 15) };
        caption.Children.Add(new TextBlock { Text = "Lucy", FontSize = 23, FontWeight = FontWeights.Bold, Foreground = NativeTheme.Brush("Ink") });
        _portraitRole.FontSize = 12;
        _portraitRole.Foreground = NativeTheme.Brush("Muted");
        _portraitRole.TextWrapping = TextWrapping.Wrap;
        caption.Children.Add(_portraitRole);
        var portraitStack = new StackPanel();
        portraitStack.Children.Add(_portrait);
        portraitStack.Children.Add(caption);
        var portraitCard = new Border
        {
            CornerRadius = new CornerRadius(17),
            BorderBrush = NativeTheme.Brush("Line"),
            BorderThickness = new Thickness(1),
            Background = NativeTheme.Brush("White"),
            Margin = new Thickness(0, 0, 0, 14),
            Child = portraitStack
        };

        var missionStack = new StackPanel();
        missionStack.Children.Add(new TextBlock { Text = "YOUR MISSION", Style = NativeTheme.Style("Label"), Margin = new Thickness(0, 0, 0, 6) });
        _practiceMissionText.Style = NativeTheme.Style("Body");
        _practiceMissionText.Margin = new Thickness(0, 0, 0, 10);
        missionStack.Children.Add(_practiceMissionText);
        _hintPills.Orientation = Orientation.Horizontal;
        missionStack.Children.Add(_hintPills);
        var missionCard = new Border { Style = NativeTheme.Style("Card"), Child = missionStack };

        var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        left.Children.Add(portraitCard);
        left.Children.Add(missionCard);
        _practiceView.Children.Add(new ScrollViewer { Style = NativeTheme.Style("ScrollHost"), Content = left, Margin = new Thickness(0, 0, 12, 0) });

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(right, 1);
        _practiceView.Children.Add(right);

        var chatGrid = new Grid();
        chatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chatGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        chatGrid.Children.Add(BuildChatHeader());
        _chatHint.Text = "Lucy will greet you. Answer out loud or by typing.";
        _chatHint.FontSize = 13;
        _chatHint.TextWrapping = TextWrapping.Wrap;
        _chatHint.Foreground = NativeTheme.Brush("Muted");
        _chatHint.Margin = new Thickness(6, 10, 26, 0);
        _chatHint.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(_chatHint, 1);
        chatGrid.Children.Add(_chatHint);
        _chatScroll.Style = NativeTheme.Style("ScrollHost");
        _chatScroll.Padding = new Thickness(0, 4, 6, 0);
        _chatScroll.Content = _chat;
        Grid.SetRow(_chatScroll, 1);
        chatGrid.Children.Add(_chatScroll);
        right.Children.Add(new Border { Style = NativeTheme.Style("Card"), Padding = new Thickness(18, 12, 12, 8), Child = chatGrid });

        BuildControls(right);
    }

    private Border BuildChatHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var avatar = new Ellipse
        {
            Width = 42,
            Height = 42,
            Margin = new Thickness(0, 0, 12, 0),
            Fill = NativeAssets.Cover(_lucyPicture, 0.5, 0.16, 42, 42) ?? NativeTheme.Brush("Mint"),
            Stroke = NativeTheme.Brush("Line"),
            StrokeThickness = 1
        };
        AutomationProperties.SetName(avatar, LucyAlt);
        grid.Children.Add(avatar);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "Lucy", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = NativeTheme.Brush("Ink") });
        _chatRole.FontSize = 12;
        _chatRole.Foreground = NativeTheme.Brush("Muted");
        _chatRole.TextTrimming = TextTrimming.CharacterEllipsis;
        names.Children.Add(_chatRole);
        Grid.SetColumn(names, 1);
        grid.Children.Add(names);
        _turnChip.FontSize = 12;
        _turnChip.FontWeight = FontWeights.SemiBold;
        _turnChip.Foreground = NativeTheme.Brush("Teal");
        var chip = new Border { Style = NativeTheme.Style("Pill"), Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Child = _turnChip };
        Grid.SetColumn(chip, 2);
        grid.Children.Add(chip);
        _restartButton.Content = "↻  Practise again";
        _restartButton.Style = NativeTheme.Style("OutlineButton");
        _restartButton.Padding = new Thickness(12, 7, 12, 7);
        _restartButton.VerticalAlignment = VerticalAlignment.Center;
        _restartButton.Click += async (_, _) => await StartAsync();
        Grid.SetColumn(_restartButton, 3);
        grid.Children.Add(_restartButton);
        return new Border
        {
            BorderBrush = NativeTheme.Brush("Line"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 6, 11),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    private void BuildControls(Grid right)
    {
        // Row 1 of the right column: the round microphone, its label, replay and voice toggle.
        var voice = new Grid { Margin = new Thickness(2, 12, 0, 10) };
        voice.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        voice.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        voice.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        voice.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _micButton.Style = NativeTheme.Style("RoundMic");
        _micButton.RenderTransformOrigin = new Point(0.5, 0.5);
        _micButton.RenderTransform = _micPulse;
        _micButton.Click += async (_, _) => await ToggleMicrophoneAsync();
        voice.Children.Add(_micButton);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        _micLabel.FontSize = 13.5;
        _micLabel.FontWeight = FontWeights.SemiBold;
        _micLabel.Foreground = NativeTheme.Brush("Ink");
        _micCaption.FontSize = 11;
        _micCaption.Foreground = NativeTheme.Brush("Muted");
        _micCaption.TextWrapping = TextWrapping.Wrap;
        labels.Children.Add(_micLabel);
        labels.Children.Add(_micCaption);
        Grid.SetColumn(labels, 1);
        voice.Children.Add(labels);
        SetMicState(recording: false);

        _replayButton.Content = "↻  Replay Lucy";
        _replayButton.Style = NativeTheme.Style("OutlineButton");
        _replayButton.Margin = new Thickness(0, 0, 12, 0);
        _replayButton.VerticalAlignment = VerticalAlignment.Center;
        _replayButton.Click += (_, _) => Speak(_lastLucyLine);
        Grid.SetColumn(_replayButton, 2);
        voice.Children.Add(_replayButton);

        _speakReplies.Content = "Read Lucy aloud";
        _speakReplies.IsChecked = true;
        _speakReplies.Foreground = NativeTheme.Brush("Muted");
        _speakReplies.VerticalAlignment = VerticalAlignment.Center;
        _speakReplies.VerticalContentAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_speakReplies, 3);
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

    private UIElement BuildToast()
    {
        _toastText.Foreground = NativeTheme.Brush("White");
        _toastText.FontSize = 12.5;
        _toastText.TextWrapping = TextWrapping.Wrap;
        _toast.Child = _toastText;
        _toast.Background = NativeTheme.Brush("Ink");
        _toast.CornerRadius = new CornerRadius(12);
        _toast.Padding = new Thickness(20, 12, 20, 12);
        _toast.MaxWidth = 580;
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.VerticalAlignment = VerticalAlignment.Bottom;
        _toast.Margin = new Thickness(0, 0, 0, 62);
        _toast.Opacity = 0;
        _toast.IsHitTestVisible = false;
        _toast.RenderTransform = _toastShift;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        };
        return _toast;
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

    private static SolidColorBrush Hex(string argb)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb));
        brush.Freeze();
        return brush;
    }

    // ---- Places and views ----------------------------------------------------

    /// <summary>
    /// One scenario record from content/scenarios.json: an emoji, a role, the mission,
    /// the pipe-separated starter phrases and the picture the catalog names.
    /// </summary>
    private sealed record ScenarioItem(string Id, string Title, string Role, string Mission, string Hint, string Image)
    {
        public string Glyph => Id switch
        {
            "airport" => "✈",
            "hotel" => "🏨",
            "cafe" => "☕",
            "shop" => "🛍",
            "hospital" => "🏥",
            "directions" => "🗺",
            "school" => "🏫",
            "interview" => "💼",
            _ => "💬"
        };

        public string Details => Role.Length == 0 ? "English speaking practice" : Role;
    }

    private void SelectPlace(string id)
    {
        _selected = _places.FirstOrDefault(place => place.Id == id) ?? _selected;
        RefreshPins();
        UpdateMission();
        UpdateButtons();
    }

    /// <summary>Selected pin: teal (the café keeps its brick red) with a white ring; the rest small and light.</summary>
    private void RefreshPins()
    {
        foreach (var pin in _pins)
        {
            string id = (string)pin.Tag;
            var place = _places.FirstOrDefault(item => item.Id == id);
            pin.Visibility = place is null ? Visibility.Collapsed : Visibility.Visible;
            if (place is null) continue;
            bool active = _selected?.Id == id;
            double size = active ? 18 : 15;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = place.Glyph, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = size, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = place.Title, FontSize = size, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            pin.Content = content;
            pin.Foreground = active ? NativeTheme.Brush("White") : Hex("#FF4B6470");
            pin.Background = active ? (id == "cafe" ? Hex("#FF873F31") : NativeTheme.Brush("Teal")) : Hex("#ECF4F8F9");
            pin.BorderBrush = active ? Hex("#B0FFFFFF") : Hex("#99FFFFFF");
            pin.BorderThickness = new Thickness(active ? 3 : 1);
            pin.Padding = active ? new Thickness(13, 8, 14, 8) : new Thickness(10, 5, 11, 5);
            pin.ToolTip = place.Mission.Length > 0 ? place.Mission : null;
            Panel.SetZIndex(pin, active ? 1 : 0);
            AutomationProperties.SetName(pin, place.Title);
            AutomationProperties.SetHelpText(pin, place.Mission);
        }
    }

    private void UpdateMission()
    {
        var item = _selected;
        _missionTitle.Text = item is null ? "MISSION" : item.Title.ToUpperInvariant();
        _missionRole.Text = item is null ? "" : $"Lucy: {item.Details}";
        _missionText.Text = item switch
        {
            null => "The eight places are still loading.",
            { Mission.Length: > 0 } => item.Mission,
            _ => "Practise real conversation in English."
        };
    }

    /// <summary>Portrait, role, mission and starter phrases of the place being practised.</summary>
    private void ShowPlace(ScenarioItem place)
    {
        _portraitPicture = NativeAssets.TryLoad(NativeAssets.ForScenario(place.Image));
        NativeAssets.PaintCover(_portrait, _portraitPicture, 0.5, 0.24);
        AutomationProperties.SetName(_portrait, $"Lucy · {place.Details}");
        _portraitRole.Text = place.Details;
        _chatRole.Text = $"{place.Title} · {place.Details}";
        _practiceMissionText.Text = place.Mission.Length > 0 ? place.Mission : "Practise real conversation in English.";
        _hintPills.Children.Clear();
        foreach (string phrase in place.Hint.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string copy = phrase;
            var pill = new Border { Style = NativeTheme.Style("Pill"), Cursor = System.Windows.Input.Cursors.Hand };
            pill.Child = new TextBlock { Text = "\u201C" + copy + "\u201D", FontSize = 12, Foreground = NativeTheme.Brush("Teal"), TextWrapping = TextWrapping.Wrap };
            pill.MouseLeftButtonUp += (_, _) => { _input.Text = copy; _input.Focus(); };
            pill.ToolTip = "Use this phrase as your answer";
            _hintPills.Children.Add(pill);
        }
    }

    private void ShowView(bool practice)
    {
        _inPractice = practice;
        _homeView.Visibility = practice ? Visibility.Collapsed : Visibility.Visible;
        _practiceView.Visibility = practice ? Visibility.Visible : Visibility.Collapsed;
        _backButton.Visibility = practice ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    /// <summary>Back to the map. An unfinished conversation stays open and can be resumed.</summary>
    private void BackToCity()
    {
        if (_recorder.IsRecording)
        {
            if (_recorder.Stop(out string? path, out _) && path is not null)
                try { File.Delete(path); } catch (IOException) { }
            SetMicState(recording: false);
        }
        ShowView(practice: false);
        if (!string.IsNullOrEmpty(_sessionId) && _selected?.Id == _scenarioId)
            SetStatus("Your conversation is still open. Press Resume conversation to continue it.");
    }

    private void UpdateOnboarding() =>
        _onboarding.Visibility = ApiConfigStore.IsConfigured(_config) ? Visibility.Collapsed : Visibility.Visible;

    private void UpdateChatHint() => _chatHint.Visibility = _chat.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateTurnChip() => _turnChip.Text = $"Turn {_turnCount} / {_maxTurns}";

    // ---- Microphone -----------------------------------------------------------

    private void SetMicState(bool recording)
    {
        _micButton.IsChecked = recording;
        _micButton.Content = Emoji(recording ? "⏹" : "🎤", 20, "White");
        _micLabel.Text = recording ? "Stop recording" : "Speak";
        _micCaption.Text = recording ? "Recording… press again when your answer is finished." : MicIdleCaption;
        AutomationProperties.SetName(_micButton, _micLabel.Text);
        if (recording)
        {
            var pulse = new DoubleAnimation(1, 0.93, TimeSpan.FromMilliseconds(850))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            _micPulse.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            _micPulse.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
        else
        {
            _micPulse.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _micPulse.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
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
            SetMicState(recording: false);
            if (!_recorder.Stop(out string? wavPath, out string stopError) || wavPath is null)
            {
                Notify(stopError);
                return;
            }
            await TranscribeAsync(wavPath);
            return;
        }
        // Lucy must not talk into the learner's recording.
        StopVoice();
        if (_recorder.Start(out string startError))
        {
            SetMicState(recording: true);
            SetStatus("Recording. Speak now, then press Stop to transcribe.");
        }
        else
        {
            SetMicState(recording: false);
            Notify(startError);
        }
    }

    private async Task TranscribeAsync(string wavPath)
    {
        _busy = true; UpdateButtons();
        _micCaption.Text = "Turning your voice into words…";
        try
        {
            // Checked before reading: an accidental long take must not be pulled into memory
            // just so the router can refuse it.
            if (new FileInfo(wavPath).Length > AppRouter.MaxBodyBytes)
            {
                Notify("That recording was too long. Press Speak again and keep the answer under 30 seconds.");
                return;
            }
            byte[] audio = await File.ReadAllBytesAsync(wavPath);
            // /api/stt takes raw WAV bytes, so the router is called directly.
            var response = _router is null ? null : await _router.HandleAsync("POST", "/api/stt", audio, _shutdown.Token);
            var payload = ReadJson(response);
            if (response is null || response.Status != 200 || payload is null)
            {
                Notify("Transcription failed: " + FailureText(response, payload));
                return;
            }
            string text = payload["text"] is JsonValue textValue && textValue.TryGetValue(out string? heard) ? heard?.Trim() ?? "" : "";
            if (text.Length == 0)
            {
                // A learner who keeps their voice down deserves to be told that, not to be told
                // the microphone or the recognition is broken.
                Notify(_recorder.LastTakeWasQuiet
                    ? "The microphone picked up almost nothing. Speak a little louder, or type the answer."
                    : "No speech was recognised. Try again, closer to the microphone.");
                return;
            }
            _input.Text = text;
            _input.CaretIndex = text.Length;
            SetStatus("Transcribed. Edit it if needed, then press Send.");
        }
        catch (OperationCanceledException) { SetStatus("Transcription canceled."); }
        catch (Exception) { Notify("Transcription failed. Check the microphone and try again."); }
        finally
        {
            _busy = false; UpdateButtons();
            _micCaption.Text = MicIdleCaption;
            try { File.Delete(wavPath); } catch (IOException) { }
            FocusInput();
        }
    }

    // ---- Lucy's voice ------------------------------------------------------------

    private void Speak(string? text) => _currentSpeech = SpeakAsync(text);

    /// <summary>Silences the current line; its remaining sentences are dropped.</summary>
    private void StopVoice()
    {
        _speechGeneration++;
        _player.Stop();
        _playing?.TrySetResult(false);
    }

    /// <summary>
    /// Plays a line through the bundled Kokoro voice, sentence by sentence: the next
    /// sentence is synthesised while the current one plays, so Lucy starts talking
    /// after one sentence's synthesis instead of the whole reply's. A newer line
    /// supersedes this one between sentences. Worker requests are never cancelled:
    /// cancelling one restarts the speech process and reloads both models.
    /// Typing must still work whatever happens here.
    /// </summary>
    private async Task SpeakAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _router is null) return;
        if (text.Length > 1200) text = text[..1200];
        StopVoice();
        int generation = ++_speechGeneration;
        var chunks = SpeechChunks.Split(text);
        if (chunks.Count == 0) return;
        var started = Stopwatch.StartNew();
        try
        {
            var next = SynthesizeAsync(chunks[0]);
            for (int index = 0; index < chunks.Count; index++)
            {
                var (audio, failure) = await next;
                if (generation != _speechGeneration || _closing) return;
                if (audio is null)
                {
                    // Surface the actual stage ("The bundled speech component is missing…",
                    // "The local speech component took too long…") instead of failing silently.
                    Notify("Lucy's voice could not play: " + failure + " Typing still works.");
                    return;
                }
                if (index + 1 < chunks.Count) next = SynthesizeAsync(chunks[index + 1]);
                if (index == 0) _firstVoiceDelay = started.Elapsed;
                if (!await PlayAsync(audio, generation)) return;
            }
        }
        catch (Exception)
        {
            // A missing or busy voice must never block the conversation.
            Notify("Lucy's voice could not play. Typing still works.");
        }
    }

    /// <summary>Returns the WAV or the router's reason; never throws, so a superseded request is harmless.</summary>
    private async Task<(byte[]? Audio, string Failure)> SynthesizeAsync(string sentence)
    {
        if (_voiceCache.TryGetValue(sentence, out var cached)) return (cached, "");
        try
        {
            var body = new JsonObject { ["text"] = sentence, ["voice"] = "american", ["speed"] = 0.95 };
            var response = await CallAsync("POST", "/api/tts", body);
            if (response is null || response.Status != 200 || response.Body.Length <= 44)
                return (null, FailureText(response, ReadJson(response)));
            // Replay and "Hear again" reuse the audio instead of synthesising it again.
            _voiceCache[sentence] = response.Body;
            _voiceCacheOrder.Enqueue(sentence);
            while (_voiceCacheOrder.Count > 24) _voiceCache.Remove(_voiceCacheOrder.Dequeue());
            return (response.Body, "");
        }
        catch (OperationCanceledException) { return (null, "Canceled."); }
        catch (Exception) { return (null, "The voice could not be prepared."); }
    }

    /// <summary>Plays one WAV; false when it was superseded, failed, or the window is closing.</summary>
    private async Task<bool> PlayAsync(byte[] audio, int generation)
    {
        if (_playOverride is not null)
        {
            _voicedSentences++;
            bool heard = await _playOverride(audio);
            return heard && generation == _speechGeneration && !_closing;
        }
        string path = Path.Combine(Path.GetTempPath(), $"speakcity-tts-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(path, audio);
        if (generation != _speechGeneration || _closing)
        {
            try { File.Delete(path); } catch (IOException) { }
            return false;
        }
        RetireSpokenFiles();
        _spokenFiles.Add(path);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _playing = finished;
        _voicedSentences++;
        _player.Open(new Uri(path));
        _player.Play();
        // MediaEnded may never arrive on a PC without an audio output; the clip's own
        // length bounds the wait so the following sentences are not held up forever.
        var done = await Task.WhenAny(finished.Task, Task.Delay(WavLength(audio) + TimeSpan.FromSeconds(3)));
        if (ReferenceEquals(_playing, finished)) _playing = null;
        bool played = done == finished.Task ? finished.Task.Result : true;
        return played && generation == _speechGeneration && !_closing;
    }

    private static TimeSpan WavLength(byte[] wav)
    {
        if (wav.Length <= 44) return TimeSpan.Zero;
        int channels = Math.Max((int)BitConverter.ToInt16(wav, 22), 1);
        int rate = Math.Max(BitConverter.ToInt32(wav, 24), 1);
        int bits = Math.Max((int)BitConverter.ToInt16(wav, 34), 8);
        return TimeSpan.FromSeconds((wav.Length - 44) / (double)(rate * channels * (bits / 8)));
    }

    private void RetireSpokenFiles()
    {
        for (int index = _spokenFiles.Count - 1; index >= 0; index--)
        {
            // A file still held by the player stays listed and is tried again next time.
            try { File.Delete(_spokenFiles[index]); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            _spokenFiles.RemoveAt(index);
        }
    }

    /// <summary>
    /// One background start per window: the bootstrap ping records whether voice can
    /// work at all, then <see cref="WarmVoiceAsync"/> loads both models. It never
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
            {
                if (IsIdle) SetStatus("Preparing Lucy's voice…");
                _warmup = WarmVoiceAsync();
                await _warmup;
                if (IsIdle)
                    SetStatus(ApiConfigStore.IsConfigured(_config)
                        ? "Voice ready. Choose a place on the map and press Start conversation."
                        : "Voice ready, but AI is not configured. Press Configure AI, test the connection, save, then Start.");
            }
            else
            {
                Notify("Voice is unavailable: the bundled speech models did not load. Reinstall SPEAKCITY. Typing still works.");
                AppStartup.Note("speech-worker", "models-not-ready");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            _voiceReady = false;
            Notify("Voice is unavailable: the speech component could not start. Reinstall SPEAKCITY. Typing still works.");
        }
        UpdateButtons();
    }

    /// <summary>
    /// The bootstrap ping only checks the model files; Kokoro and Whisper load on
    /// first use. One tiny synthesis, then its transcription, moves both loads off
    /// Lucy's greeting and the learner's first recording. Failures stay quiet here:
    /// the first real use reports them in its own words.
    /// </summary>
    private async Task WarmVoiceAsync()
    {
        if (_router is null) return;
        var watch = Stopwatch.StartNew();
        try
        {
            byte[] request = System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["text"] = "Hello there.", ["voice"] = "american", ["speed"] = 0.95 }.ToJsonString());
            var spoken = await _router.HandleAsync("POST", "/api/tts", request, _shutdown.Token);
            if (spoken.Status != 200 || spoken.Body.Length <= 44) return;
            AppStartup.Note("speech-worker", "voice-warm", $"{watch.ElapsedMilliseconds} ms");
            watch.Restart();
            await _router.HandleAsync("POST", "/api/stt", spoken.Body, _shutdown.Token);
            AppStartup.Note("speech-worker", "transcription-warm", $"{watch.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    // ---- Start, turns and feedback ------------------------------------------------------

    private async Task InitializeAsync()
    {
        try
        {
            string catalogPath = Path.Combine(AppContext.BaseDirectory, "content", "scenarios.json");
            if (!File.Exists(catalogPath))
            {
                Notify("The scenario package is missing from the installed folder. Reinstall SPEAKCITY using the official installer.");
                return;
            }
            var catalog = JsonNode.Parse(await File.ReadAllTextAsync(catalogPath))?.AsObject();
            if (catalog is null || catalog.Count != 8)
            {
                Notify("The scenario package is invalid. Reinstall SPEAKCITY.");
                return;
            }
            _places.Clear();
            foreach (var pair in catalog)
            {
                var node = pair.Value;
                _places.Add(new ScenarioItem(pair.Key,
                    Text(node?["title"]?["en"]) ?? pair.Key,
                    Text(node?["role"]?["en"]) ?? "",
                    Text(node?["mission"]?["en"]) ?? "",
                    Text(node?["hint"]) ?? "",
                    Text(node?["image"]) ?? ""));
            }
            SelectPlace(_places.Any(place => place.Id == "airport") ? "airport" : _places[0].Id);

            _speech = new SpeechWorkerClient();
            _api = new ApiClient(() => _config);
            var complete = _completeOverride ?? _api.CompleteJsonAsync;
            _router = new AppRouter(_speech, ConfigureTaskAsync, () => _config, complete, catalogPath);
            if (_speech.WorkerPath is { Length: > 0 } && !File.Exists(_speech.WorkerPath))
            {
                Notify("Voice is unavailable: the bundled speech component is missing. Reinstall SPEAKCITY. Typing still works.");
                AppStartup.Note("speech-worker", "missing", _speech.ResolvedFrom);
            }
            else
            {
                StartWarmupAsync();
                SetStatus(ApiConfigStore.IsConfigured(_config)
                    ? "Voice engine starting… Choose a place on the map and press Start conversation."
                    : "Voice engine starting… Press Configure AI, test the connection, save, then press Start conversation.");
            }
        }
        catch (Exception)
        {
            Notify("The native window could not prepare itself. Reinstall SPEAKCITY. No browser component is required.");
        }
        finally
        {
            UpdateOnboarding();
            UpdateButtons();
            _ready.TrySetResult();
        }
    }

    private bool IsIdle => !_busy && string.IsNullOrEmpty(_sessionId);

    private void UpdateButtons()
    {
        bool hasScenario = _selected is not null;
        bool hasSession = !string.IsNullOrEmpty(_sessionId);
        bool resumable = hasSession && _selected?.Id == _scenarioId;
        _startButton.Content = resumable ? "Resume conversation" : "Start conversation";
        _startButton.IsEnabled = !_busy && hasScenario;
        _restartButton.Visibility = _feedbackShown && !hasSession ? Visibility.Visible : Visibility.Collapsed;
        _restartButton.IsEnabled = !_busy;
        _sendButton.IsEnabled = !_busy && hasSession;
        _finishButton.IsEnabled = !_busy && hasSession;
        _input.IsEnabled = !_busy && hasSession;
        _micButton.IsEnabled = !_busy && hasSession;
        _replayButton.IsEnabled = !_busy && _lastLucyLine is not null;
        _configureButton.IsEnabled = !_busy;
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
        Notify(ApiConfigStore.IsConfigured(_config)
            ? "AI settings saved. Choose a place and press Start conversation."
            : "AI is not configured yet. Open Configure AI again.");
        UpdateOnboarding();
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

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string FailureText(AppResponse? response, JsonObject? payload)
    {
        string detail = Text(payload?["detail"]) ?? "";
        if (detail.Length > 0) return detail;
        return response is null ? "No response." : $"HTTP {response.Status}.";
    }

    private async Task StartAsync()
    {
        if (_busy || _selected is null) return;
        var place = _selected;
        if (!string.IsNullOrEmpty(_sessionId) && place.Id == _scenarioId)
        {
            ShowView(practice: true);
            FocusInput();
            return;
        }
        _busy = true; UpdateButtons();
        try
        {
            string level = _level.SelectedItem as string ?? "A2";
            var body = new JsonObject { ["scenario"] = place.Id, ["level"] = level, ["language"] = "en" };
            var response = await CallAsync("POST", "/api/sessions", body);
            var payload = ReadJson(response);
            if (response is null || response.Status != 200 || payload is null)
            {
                Notify("Could not start: " + FailureText(response, payload));
                return;
            }
            // Starting another place abandons the open conversation; free it in the router.
            if (!string.IsNullOrEmpty(_sessionId)) _ = CallAsync("DELETE", $"/api/sessions/{_sessionId}");
            _sessionId = Text(payload["session_id"]);
            _scenarioId = place.Id;
            _turnCount = payload["turn_count"]?.GetValue<int>() ?? 0;
            _maxTurns = payload["max_turns"]?.GetValue<int>() ?? 8;
            _feedbackShown = false;
            _chat.Children.Clear();
            UpdateChatHint();
            ShowPlace(place);
            UpdateTurnChip();
            ShowView(practice: true);
            _lastLucyLine = Text(payload["reply"]) ?? "";
            AddMessage("Lucy", _lastLucyLine);
            if (_speakReplies.IsChecked == true) Speak(_lastLucyLine);
            SetStatus($"{place.Title}, level {level}. Turn {_turnCount}/{_maxTurns}. Answer out loud or type, then press Send.");
        }
        catch (OperationCanceledException) { SetStatus("Start canceled."); }
        catch (Exception) { Notify("Could not start the conversation. Check your API settings and try again."); }
        finally { _busy = false; UpdateButtons(); FocusInput(); }
    }

    private async Task SendAsync()
    {
        if (_busy) return;
        if (string.IsNullOrEmpty(_sessionId)) { SetStatus("Press Start conversation first."); return; }
        string text = _input.Text.Trim();
        if (text.Length == 0) return;
        _busy = true; UpdateButtons();
        var sent = AddMessage("You", text);
        bool delivered = false;
        try
        {
            _input.Clear();
            ShowThinking();
            var body = new JsonObject { ["text"] = text, ["request_id"] = Guid.NewGuid().ToString("N") };
            var response = await CallAsync("POST", $"/api/sessions/{_sessionId}/turn", body);
            var payload = ReadJson(response);
            HideThinking();
            if (response is null || response.Status != 200 || payload is null)
            {
                Notify("Send failed: " + FailureText(response, payload));
                return;
            }
            delivered = true;
            _turnCount = payload["turn_count"]?.GetValue<int>() ?? _turnCount + 1;
            UpdateTurnChip();
            _lastLucyLine = Text(payload["reply"]) ?? "";
            AddMessage("Lucy", _lastLucyLine);
            if (_speakReplies.IsChecked == true) Speak(_lastLucyLine);
            bool atLimit = payload["at_limit"]?.GetValue<bool>() ?? _turnCount >= _maxTurns;
            SetStatus(atLimit
                ? $"Turn limit reached ({_turnCount}/{_maxTurns}). Press Finish & feedback."
                : $"Turn {_turnCount}/{_maxTurns}.");
        }
        catch (OperationCanceledException) { SetStatus("Send canceled."); }
        catch (Exception) { Notify("Send failed. Check the connection and try again."); }
        finally
        {
            HideThinking();
            if (!delivered && !_closing)
            {
                // The turn never reached the conversation: take the bubble back and return the
                // words to the box, so Send can simply be pressed again.
                _chat.Children.Remove(sent);
                UpdateChatHint();
                if (_input.Text.Length == 0) _input.Text = text;
            }
            _busy = false; UpdateButtons(); FocusInput();
        }
    }

    private async Task FinishAsync()
    {
        if (_busy) return;
        if (string.IsNullOrEmpty(_sessionId)) { SetStatus("Press Start conversation first."); return; }
        _busy = true; UpdateButtons();
        try
        {
            string language = _language.SelectedItem as string ?? "en";
            ShowThinking();
            var body = new JsonObject { ["language"] = language };
            var response = await CallAsync("POST", $"/api/sessions/{_sessionId}/finish", body);
            var payload = ReadJson(response);
            HideThinking();
            if (response is null || response.Status != 200 || payload is null)
            {
                Notify("Feedback failed: " + FailureText(response, payload));
                return;
            }
            AddNote("— Feedback —", heading: true);
            int corrections = 0;
            if (payload["corrections"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is not JsonObject entry) continue;
                    string original = Text(entry["original"]) ?? "";
                    string corrected = Text(entry["corrected"]) ?? "";
                    string explanation = Text(entry["explanation"]?[language]) ?? Text(entry["explanation"]?["en"]) ?? "";
                    AddNote($"•  {original}   →   {corrected}");
                    if (explanation.Length > 0) AddNote(explanation);
                    corrections++;
                }
            }
            if (corrections == 0)
            {
                var (headline, note) = NoCorrections.TryGetValue(language, out var words) ? words : NoCorrections["en"];
                AddNote(headline);
                AddNote(note);
            }
            if (payload["vocabulary"] is JsonArray vocabulary)
            {
                AddNote($"— Vocabulary ({vocabulary.Count} words for {_selected?.Title ?? _scenarioId}) —", heading: true);
                foreach (var word in vocabulary)
                {
                    if (word is not JsonObject entry) continue;
                    string label = Text(entry["word"]) ?? "";
                    string meaning = Text(entry["meaning"]?[language]) ?? Text(entry["meaning"]?["en"]) ?? "";
                    bool encountered = entry["encountered"] is JsonValue used && used.TryGetValue(out bool seen) && seen;
                    AddNote($"• {label} — {meaning}{(encountered ? "  (used)" : "")}");
                }
            }
            ScrollChatToEnd();
            // The review is on screen; free the finished conversation in the router.
            string? finished = _sessionId;
            _sessionId = null;
            _feedbackShown = true;
            if (finished is not null) _ = CallAsync("DELETE", $"/api/sessions/{finished}");
            SetStatus("Conversation finished. Press Practise again, or go back to the city for another place.");
        }
        catch (OperationCanceledException) { SetStatus("Feedback canceled."); }
        catch (Exception) { Notify("Feedback failed. Try again."); }
        finally { HideThinking(); _busy = false; UpdateButtons(); }
    }

    // ---- Chat -------------------------------------------------------------------

    private FrameworkElement AddMessage(string who, string text)
    {
        var row = who == "You" ? YouRow(text) : LucyRow(MessageText(text), text);
        _chat.Children.Add(row);
        UpdateChatHint();
        ScrollChatToEnd();
        return row;
    }

    private static TextBlock MessageText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
        LineHeight = 22,
        Foreground = NativeTheme.Brush("Ink")
    };

    /// <summary>Lucy's line: round avatar, name, white bubble with the web's 0/13/13/13 corners, "Hear again".</summary>
    private FrameworkElement LucyRow(UIElement content, string? replayText)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 60, 12), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Ellipse
        {
            Width = 28,
            Height = 28,
            Margin = new Thickness(0, 20, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Fill = NativeAssets.Cover(_lucyPicture, 0.5, 0.16, 28, 28) ?? NativeTheme.Brush("Mint")
        });
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Lucy", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = NativeTheme.Brush("Teal"), Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(new Border
        {
            Child = content,
            Background = NativeTheme.Brush("White"),
            BorderBrush = NativeTheme.Brush("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0, 13, 13, 13),
            Padding = new Thickness(15, 11, 15, 11),
            HorizontalAlignment = HorizontalAlignment.Left
        });
        if (replayText is not null)
        {
            var replay = new Button { Content = "🔊  Hear again", Style = NativeTheme.Style("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            replay.Click += (_, _) => Speak(replayText);
            stack.Children.Add(replay);
        }
        Grid.SetColumn(stack, 1);
        row.Children.Add(stack);
        return row;
    }

    /// <summary>The learner's line: no avatar, pushed right, mint-green with the mirrored corners.</summary>
    private static FrameworkElement YouRow(string text)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(60, 0, 0, 12), MaxWidth = 680 };
        stack.Children.Add(new TextBlock { Text = "You", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = NativeTheme.Brush("Muted"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 2, 4) });
        stack.Children.Add(new Border
        {
            Child = MessageText(text),
            Background = Hex("#FFE7F3EE"),
            BorderBrush = Hex("#FFD8E8DF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13, 0, 13, 13),
            Padding = new Thickness(15, 11, 15, 11)
        });
        return stack;
    }

    /// <summary>"Lucy is thinking…" with three pulsing dots, in place of an empty wait.</summary>
    private void ShowThinking()
    {
        HideThinking();
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        for (int index = 0; index < 3; index++)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Fill = NativeTheme.Brush("Teal"), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(600))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(200 * index)
            });
            content.Children.Add(dot);
        }
        content.Children.Add(new TextBlock { Text = Thinking, FontSize = 12, Foreground = NativeTheme.Brush("Muted"), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        _thinking = LucyRow(content, replayText: null);
        _chat.Children.Add(_thinking);
        UpdateChatHint();
        ScrollChatToEnd();
    }

    private void HideThinking()
    {
        if (_thinking is null) return;
        _chat.Children.Remove(_thinking);
        _thinking = null;
        UpdateChatHint();
    }

    private void ScrollChatToEnd() => _chatScroll.ScrollToEnd();

    /// <summary>Feedback and vocabulary lines: white cards, a teal spine for headings.</summary>
    private void AddNote(string text, bool heading = false)
    {
        _chat.Children.Add(new Border
        {
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                LineHeight = 21,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = NativeTheme.Brush("Ink")
            },
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 14, 10),
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 60, 10),
            Background = NativeTheme.Brush("White"),
            BorderBrush = NativeTheme.Brush(heading ? "Teal" : "Line"),
            BorderThickness = heading ? new Thickness(4, 1, 1, 1) : new Thickness(1)
        });
        UpdateChatHint();
        ScrollChatToEnd();
    }

    // ---- Status and toast -----------------------------------------------------

    private void SetStatus(string text)
    {
        if (!_closing) _status.Text = text;
    }

    /// <summary>Status line plus a toast: for failures and results the learner must not miss.</summary>
    private void Notify(string text)
    {
        SetStatus(text);
        ShowToast(text);
    }

    internal void ShowToast(string text)
    {
        if (_closing || string.IsNullOrWhiteSpace(text)) return;
        _toastText.Text = text;
        _toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        _toastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(180)));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>After the buttons are re-enabled, so a disabled box never swallows the focus.</summary>
    private void FocusInput() =>
        Dispatcher.BeginInvoke(() => { if (_inPractice && _input.IsEnabled) _input.Focus(); }, DispatcherPriority.Input);

    // ---- Smoke-test surface (NativeSmoke only) ---------------------------------------

    internal Task Ready => _ready.Task;
    internal Task? VoiceWarmup => _warmup;
    internal Task? CurrentSpeech => _currentSpeech;
    internal int VoicedSentences => _voicedSentences;
    internal int PlaybackFailures => _playbackFailures;
    internal TimeSpan? FirstVoiceDelay => _firstVoiceDelay;
    internal IReadOnlyList<Button> Pins => _pins;
    internal string? SelectedPlaceId => _selected?.Id;
    internal bool InPractice => _inPractice;
    internal BitmapSource? MapPicture => _cityPicture;
    internal BitmapSource? LucyPicture => _lucyPicture;
    internal BitmapSource? PortraitPicture => _portraitPicture;
    internal bool PortraitPainted => _portrait.Background is ImageBrush { ImageSource: not null };
    internal bool FeedbackShown => _feedbackShown;
    internal int LucyAvatars => _chat.Children.OfType<Grid>()
        .Count(row => row.Children.OfType<Ellipse>().Any(avatar => avatar.Fill is ImageBrush { ImageSource: not null }));
    internal IEnumerable<string> ChatLines => _chat.Children.OfType<FrameworkElement>()
        .SelectMany(element => Descendants(element).OfType<TextBlock>()).Select(block => block.Text);
    internal Task StartForTestAsync() => StartAsync();
    internal Task SendForTestAsync(string text) { _input.Text = text; return SendAsync(); }
    internal Task FinishForTestAsync() => FinishAsync();
    internal void ShowRecordingForTest(bool recording) => SetMicState(recording);
    internal void ShowThinkingForTest(bool thinking) { if (thinking) ShowThinking(); else HideThinking(); }
    internal async Task<JsonObject?> PingVoiceForTestAsync() => _speech is null ? null : await _speech.RequestAsync("ping");

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (var deeper in Descendants(node)) yield return deeper;
        }
    }
}
