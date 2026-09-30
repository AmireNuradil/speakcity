using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SpeakCity;

/// <summary>
/// The window's screens besides the city and the conversation: Feedback, Settings and its sections. Every
/// section is one entry in <see cref="Sections"/>; the hub, navigation and back links follow from it,
/// so a new section (voice, conversation, history…) needs only its entry and its page builder.
/// </summary>
public sealed partial class NativeMainWindow
{
    private static readonly IReadOnlyDictionary<string, string> LanguageNames = new Dictionary<string, string>
    {
        ["en"] = "English", ["kk"] = "Қазақша", ["ru"] = "Русский"
    };

    private static readonly IReadOnlyDictionary<string, (string Name, string Description)> LevelNames = new Dictionary<string, (string, string)>
    {
        ["A1"] = ("Beginner", "Short everyday phrases and simple choices."),
        ["A2"] = ("Elementary", "Clear everyday English about familiar things."),
        ["B1"] = ("Intermediate", "Natural everyday English with some common phrasal verbs."),
        ["B2"] = ("Upper-intermediate", "Varied vocabulary and the odd idiom, at a normal pace."),
        ["C1"] = ("Advanced", "Fluent, idiomatic English that invites longer answers."),
        ["C2"] = ("Proficient", "Natural, nuanced English, as a real person in this role would speak.")
    };

    private static readonly IReadOnlyDictionary<string, (string Name, string Description)> DepthNames = new Dictionary<string, (string, string)>
    {
        ["focused"] = ("Focused", "Up to 3 clear grammar mistakes. Nothing about style or punctuation."),
        ["thorough"] = ("Thorough", "Up to 5 mistakes, including clearly wrong word choices.")
    };

    private sealed record SettingsSection(string Id, string Glyph, string Title, Func<string> Summary, Func<FrameworkElement> Build);

    private readonly Border _pageHost = new();
    private IReadOnlyList<SettingsSection>? _sections;
    private string _view = "";

    private IReadOnlyList<SettingsSection> Sections => _sections ??=
    [
        new("level", "🎯", "English level", () => $"{_prefs.Level} · {LevelNames[_prefs.Level].Name}. Lucy speaks to match it.", BuildLevelPage),
        new("feedback", "📝", "Feedback", FeedbackSettingsSummary, BuildFeedbackChoices),
        new("ai", "🤖", "AI configuration", AiSummary, BuildAiPage)
    ];

    /// <summary>
    /// Shows "home", "practice", "feedback", "settings" or "settings/{section}". Pages are rebuilt on every visit,
    /// so they always show current values, and leaving one unloads it (the AI page stops a running test).
    /// </summary>
    private void Navigate(string view)
    {
        var section = view.StartsWith("settings/", StringComparison.Ordinal)
            ? Sections.FirstOrDefault(item => item.Id == view["settings/".Length..]) : null;
        if (view.StartsWith("settings/", StringComparison.Ordinal) && section is null) view = "settings";
        if (_view == "practice" && view != "practice") DropRecording();
        _view = view;
        _inPractice = view == "practice";
        _homeView.Visibility = view == "home" ? Visibility.Visible : Visibility.Collapsed;
        _practiceView.Visibility = _inPractice ? Visibility.Visible : Visibility.Collapsed;
        _pageHost.Child = view switch
        {
            "settings" => BuildSettingsHub(),
            "feedback" => Page("Feedback", "Your grammar corrections from finished conversations and your personal vocabulary. Kept only on this PC.", BuildFeedbackPage()),
            _ => section is null ? null : SectionPage(section)
        };
        _pageHost.Visibility = _pageHost.Child is null ? Visibility.Collapsed : Visibility.Visible;
        _homeNav.Background = NativeTheme.Brush(view is "home" or "practice" ? "Mint" : "White");
        _feedbackNav.Background = NativeTheme.Brush(view == "feedback" ? "Mint" : "White");
        _settingsNav.Background = NativeTheme.Brush(view.StartsWith("settings", StringComparison.Ordinal) ? "Mint" : "White");
        if (view == "home") UpdatePracticeSummary();
        UpdateButtons();
    }

    // ---- Page frame -----------------------------------------------------------

    private static FrameworkElement Page(string title, string intro, FrameworkElement content, Action? back = null, string backLabel = "")
    {
        var stack = new StackPanel { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 18, 18) };
        if (back is not null)
        {
            var link = new Button { Content = "←  " + backLabel, Style = NativeTheme.Style("LinkButton"), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-5, 0, 0, 6), Tag = "back" };
            link.Click += (_, _) => back();
            stack.Children.Add(link);
        }
        stack.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.Bold, Foreground = NativeTheme.Brush("Ink") });
        stack.Children.Add(new TextBlock { Text = intro, Style = NativeTheme.Style("Body"), FontSize = 13.5, Foreground = NativeTheme.Brush("Muted"), Margin = new Thickness(0, 4, 0, 18) });
        stack.Children.Add(content);
        return new ScrollViewer { Style = NativeTheme.Style("ScrollHost"), Content = stack };
    }

    private FrameworkElement SectionPage(SettingsSection section)
    {
        string intro = section.Id switch
        {
            "level" => "Choose the level you want to practise at. Lucy adapts her English to it from your next conversation.",
            "feedback" => "How your end-of-conversation review is given. Your corrections and saved words are on the Feedback page.",
            "ai" => "The AI provider Lucy talks through. Only the text of the conversation is sent there; your voice stays on this PC.",
            _ => ""
        };
        return Page(section.Title, intro, section.Build(), () => Navigate("settings"), "Settings");
    }

    private static Border Card(UIElement content, Thickness? margin = null) =>
        new() { Style = NativeTheme.Style("Card"), Padding = new Thickness(20, 16, 20, 18), Margin = margin ?? new Thickness(0, 0, 0, 14), Child = content };

    private static TextBlock CardTitle(string text) =>
        new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = NativeTheme.Brush("Ink"), Margin = new Thickness(0, 0, 0, 4) };

    private static TextBlock Muted(string text, double size = 12.5) =>
        new() { Text = text, FontSize = size, Foreground = NativeTheme.Brush("Muted"), TextWrapping = TextWrapping.Wrap };

    /// <summary>A choice card; the selected one is mint with a teal border and a check mark.</summary>
    private static Button Choice(string tag, string badge, string title, string description, bool selected, Action choose)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 14, 0),
            Background = NativeTheme.Brush(selected ? "Teal" : "Mint"), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = badge, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = NativeTheme.Brush(selected ? "White" : "Teal"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = NativeTheme.Brush("Ink") });
        text.Children.Add(Muted(description));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (selected)
        {
            var check = new TextBlock { Text = "✓", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = NativeTheme.Brush("Teal"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }
        var button = new Button
        {
            Style = NativeTheme.Style("CardButton"), Content = grid, Tag = tag, Margin = new Thickness(0, 0, 0, 10),
            Background = NativeTheme.Brush(selected ? "Mint" : "White"),
            BorderBrush = NativeTheme.Brush(selected ? "Teal" : "Line"),
            BorderThickness = new Thickness(selected ? 2 : 1)
        };
        AutomationProperties.SetName(button, $"{title}{(selected ? ", selected" : "")}");
        AutomationProperties.SetHelpText(button, description);
        button.Click += (_, _) => choose();
        return button;
    }

    private void SavePreferences(string done)
    {
        try
        {
            _prefs.Save(_prefsPath);
            ShowToast(done);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notify("The choice applies now but could not be saved for next time: the settings folder is not writable.");
        }
    }

    // ---- Settings hub -----------------------------------------------------------

    private FrameworkElement BuildSettingsHub()
    {
        var list = new StackPanel();
        foreach (var section in Sections)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new Border
            {
                Width = 46, Height = 46, CornerRadius = new CornerRadius(13), Background = NativeTheme.Brush("Mint"), Margin = new Thickness(0, 0, 16, 0),
                Child = Emoji(section.Glyph, 21, "Teal")
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = section.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = NativeTheme.Brush("Ink") });
            text.Children.Add(Muted(section.Summary(), 12.5));
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var arrow = new TextBlock { Text = "›", FontSize = 26, Foreground = NativeTheme.Brush("Teal"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 2, 2) };
            Grid.SetColumn(arrow, 2);
            grid.Children.Add(arrow);
            var open = new Button { Style = NativeTheme.Style("CardButton"), Content = grid, Tag = "open:" + section.Id, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(18, 16, 18, 16) };
            AutomationProperties.SetName(open, section.Title);
            string id = section.Id;
            open.Click += (_, _) => Navigate("settings/" + id);
            list.Children.Add(open);
        }
        return Page("Settings", "Everything you can change in SPEAKCITY, in one place.", list);
    }

    // ---- English level ------------------------------------------------------------

    private FrameworkElement BuildLevelPage()
    {
        var list = new StackPanel();
        foreach (string level in AppPreferences.Levels)
        {
            var (name, description) = LevelNames[level];
            string chosen = level;
            list.Children.Add(Choice(level, level, name, description, _prefs.Level == level, () =>
            {
                if (_prefs.Level == chosen) return;
                _prefs.Level = chosen;
                SavePreferences($"Level set to {chosen}. It applies from your next conversation.");
                Navigate("settings/level");
            }));
        }
        if (!string.IsNullOrEmpty(_sessionId))
            list.Children.Add(Muted("The conversation that is open now keeps the level it started with.", 12));
        return list;
    }

    // ---- Feedback ------------------------------------------------------------------

    private string FeedbackSettingsSummary() =>
        $"{DepthNames[_prefs.FeedbackDepth].Name} review, explanations in {LanguageNames[_prefs.FeedbackLanguage]}.";

    private FrameworkElement BuildFeedbackPage()
    {
        var page = new StackPanel();
        page.Children.Add(BuildCorrectionsCard());
        page.Children.Add(BuildWordsCard());
        page.Children.Add(BuildVocabularyCard());
        var settings = new Button { Content = "Explanation language and review type are in Settings  →", Style = NativeTheme.Style("LinkButton"), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-5, 0, 0, 10), Tag = "open-settings" };
        settings.Click += (_, _) => Navigate("settings/feedback");
        page.Children.Add(settings);
        if (_history.Entries.Count > 0)
        {
            var clear = new Button { Content = "Clear feedback history", Style = NativeTheme.Style("OutlineButton"), HorizontalAlignment = HorizontalAlignment.Left, Tag = "clear-history" };
            clear.Click += (_, _) =>
            {
                if (MessageBox.Show(this, "Delete every saved review from this PC? My vocabulary and your settings stay as they are.",
                        "Clear feedback history", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                try { _history.Clear(); ShowToast("Feedback history cleared."); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Notify("The feedback history could not be cleared: its folder is not writable."); }
                Navigate("feedback");
            };
            page.Children.Add(clear);
        }
        return page;
    }

    private Border BuildFeedbackChoices()
    {
        var stack = new StackPanel();
        stack.Children.Add(CardTitle("How you get feedback"));
        stack.Children.Add(Muted("Changes apply to your next review.", 12));
        stack.Children.Add(new TextBlock { Text = "EXPLANATIONS IN", Style = NativeTheme.Style("Label"), Margin = new Thickness(0, 14, 0, 7) });
        var languages = new WrapPanel();
        foreach (string language in AppPreferences.Languages)
        {
            bool selected = _prefs.FeedbackLanguage == language;
            var option = new Button
            {
                Content = (selected ? "✓  " : "") + LanguageNames[language], Tag = "language:" + language,
                Style = NativeTheme.Style(selected ? "PrimaryButton" : "OutlineButton"), Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(16, 8, 16, 8)
            };
            AutomationProperties.SetName(option, $"Explanations in {LanguageNames[language]}{(selected ? ", selected" : "")}");
            string chosen = language;
            option.Click += (_, _) =>
            {
                if (_prefs.FeedbackLanguage == chosen) return;
                _prefs.FeedbackLanguage = chosen;
                SavePreferences($"Explanations will be in {LanguageNames[chosen]}.");
                Navigate("settings/feedback");
            };
            languages.Children.Add(option);
        }
        stack.Children.Add(languages);
        stack.Children.Add(new TextBlock { Text = "REVIEW", Style = NativeTheme.Style("Label"), Margin = new Thickness(0, 12, 0, 7) });
        foreach (string depth in AppPreferences.Depths)
        {
            var (name, description) = DepthNames[depth];
            string chosen = depth;
            stack.Children.Add(Choice("depth:" + depth, name[..1], name, description, _prefs.FeedbackDepth == depth, () =>
            {
                if (_prefs.FeedbackDepth == chosen) return;
                _prefs.FeedbackDepth = chosen;
                SavePreferences($"{name} review from your next conversation.");
                Navigate("settings/feedback");
            }));
        }
        return Card(stack);
    }

    /// <summary>Every saved review, newest first, with its corrections in the chosen language.</summary>
    private Border BuildCorrectionsCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(CardTitle("Your corrections"));
        if (_history.Entries.Count == 0)
        {
            stack.Children.Add(Muted("Finish a conversation with “Finish & feedback” and its review appears here."));
            return Card(stack);
        }
        string language = _prefs.FeedbackLanguage;
        foreach (var entry in _history.Entries)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"{Text(entry["title"]) ?? Text(entry["scenario"])} · {Text(entry["level"])} · {When(entry)}",
                FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = NativeTheme.Brush("Teal"), Margin = new Thickness(0, 12, 0, 5)
            });
            var corrections = entry["corrections"] as JsonArray ?? [];
            if (corrections.Count == 0)
            {
                var (headline, note) = NoCorrections.TryGetValue(language, out var words) ? words : NoCorrections["en"];
                stack.Children.Add(Muted($"{headline} {note}", 12.5));
                continue;
            }
            foreach (var item in corrections)
            {
                if (item is not JsonObject correction) continue;
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                row.Children.Add(new TextBlock
                {
                    Text = $"{Text(correction["original"])}   →   {Text(correction["corrected"])}",
                    FontSize = 14, Foreground = NativeTheme.Brush("Ink"), TextWrapping = TextWrapping.Wrap
                });
                string explanation = Text(correction["explanation"]?[language]) ?? Text(correction["explanation"]?["en"]) ?? "";
                if (explanation.Length > 0) row.Children.Add(Muted(explanation, 12.5));
                stack.Children.Add(new Border { BorderBrush = NativeTheme.Brush("Teal"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2), Child = row });
            }
        }
        return Card(stack);
    }

    /// <summary>The words of every practised place: the ones still to try and the ones the learner said, each with Listen and Save.</summary>
    private Border BuildWordsCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(CardTitle("Words from your conversations"));
        var words = new Dictionary<string, (JsonObject Word, bool Used, SortedSet<string> Places, string Scenario, string Title)>(StringComparer.OrdinalIgnoreCase);
        string language = _prefs.FeedbackLanguage;
        foreach (var entry in _history.Entries)
        {
            string scenario = Text(entry["scenario"]) ?? "";
            string place = Text(entry["title"]) ?? scenario;
            foreach (var item in entry["words"] as JsonArray ?? [])
            {
                if (item is not JsonObject word || Text(word["word"]) is not { Length: > 0 } label) continue;
                bool used = word["used_by_learner"] is JsonValue flag && flag.TryGetValue(out bool said) && said;
                if (!words.TryGetValue(label, out var known)) known = (word, false, new SortedSet<string>(StringComparer.Ordinal), scenario, place);
                known.Places.Add(place);
                words[label] = (known.Word, known.Used || used, known.Places, known.Scenario, known.Title);
            }
        }
        if (words.Count == 0)
        {
            stack.Children.Add(Muted("Each place has its own useful words. They appear here after your first review."));
            return Card(stack);
        }
        foreach (var (heading, used) in new[] { ("WORDS TO TRY NEXT TIME", false), ("WORDS YOU USED", true) })
        {
            var group = words.Where(pair => pair.Value.Used == used).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList();
            stack.Children.Add(new TextBlock { Text = $"{heading} ({group.Count})", Style = NativeTheme.Style("Label"), Margin = new Thickness(0, 12, 0, 6) });
            if (group.Count == 0)
            {
                stack.Children.Add(Muted(used ? "None yet: try to use a few of the words above in your answers." : "You have used every word so far.", 12));
                continue;
            }
            var chips = new WrapPanel();
            foreach (var (label, info) in group)
            {
                var chip = new StackPanel();
                chip.Children.Add(new TextBlock { Text = (used ? "✓ " : "") + label, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = NativeTheme.Brush(used ? "Teal" : "Ink") });
                string meaning = Meaning(info.Word, language);
                if (meaning.Length > 0) chip.Children.Add(Muted(meaning, 11.5));
                chip.Children.Add(Muted(string.Join(", ", info.Places), 10.5));
                chip.Children.Add(WordActions(info.Word, info.Scenario, info.Title, () => Navigate("feedback")));
                chips.Children.Add(new Border
                {
                    Background = NativeTheme.Brush(used ? "Mint" : "Bg"), CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 7, 12, 6), Margin = new Thickness(0, 0, 8, 8), MaxWidth = 260, Child = chip
                });
            }
            stack.Children.Add(chips);
        }
        return Card(stack);
    }

    /// <summary>My vocabulary: the saved words with meaning, example and place, to listen to again or remove.</summary>
    private Border BuildVocabularyCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(CardTitle($"My vocabulary ({_vocabulary.Entries.Count})"));
        if (_vocabulary.Entries.Count == 0)
        {
            stack.Children.Add(Muted("Press “+ Save” on a word, here or in the review after a conversation, to keep it here for later."));
            return Card(stack);
        }
        string language = _prefs.FeedbackLanguage;
        foreach (var entry in _vocabulary.Entries)
        {
            string label = Text(entry["word"]) ?? "";
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = label, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = NativeTheme.Brush("Ink") });
            string meaning = Meaning(entry, language);
            if (meaning.Length > 0) text.Children.Add(Muted(meaning, 12.5));
            if (Text(entry["example"]) is { Length: > 0 } example)
                text.Children.Add(new TextBlock { Text = "“" + example + "”", FontSize = 12.5, FontStyle = FontStyles.Italic, Foreground = NativeTheme.Brush("Ink"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            text.Children.Add(Muted(Text(entry["title"]) ?? "", 10.5));
            grid.Children.Add(text);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var listen = new Button { Content = "🔊 Listen", Style = NativeTheme.Style("LinkButton"), Tag = "listen:" + label };
            AutomationProperties.SetName(listen, $"Listen to {label}");
            listen.Click += (_, _) => Speak(label);
            actions.Children.Add(listen);
            var remove = new Button { Content = "Remove", Style = NativeTheme.Style("LinkButton"), Tag = "remove:" + label, Margin = new Thickness(6, 0, 0, 0) };
            AutomationProperties.SetName(remove, $"Remove {label} from My vocabulary");
            remove.Click += (_, _) =>
            {
                try { _vocabulary.Remove(label); ShowToast($"“{label}” removed from My vocabulary."); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Notify("The word could not be removed: the settings folder is not writable."); }
                Navigate("feedback");
            };
            actions.Children.Add(remove);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            stack.Children.Add(new Border { BorderBrush = NativeTheme.Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8), Child = grid });
        }
        return Card(stack);
    }

    /// <summary>Listen (Lucy's voice) and Save (to My vocabulary) for one word.</summary>
    private FrameworkElement WordActions(JsonObject word, string scenario, string title, Action? refresh)
    {
        string label = Text(word["word"]) ?? "";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-5, 3, 0, 0) };
        var listen = new Button { Content = "🔊 Listen", Style = NativeTheme.Style("LinkButton"), Tag = "listen:" + label };
        AutomationProperties.SetName(listen, $"Listen to {label}");
        listen.Click += (_, _) => Speak(label);
        row.Children.Add(listen);
        bool saved = _vocabulary.Contains(label);
        var save = new Button { Content = saved ? "✓ Saved" : "+ Save", Style = NativeTheme.Style("LinkButton"), Tag = "save:" + label, IsEnabled = !saved, Margin = new Thickness(6, 0, 0, 0) };
        AutomationProperties.SetName(save, saved ? $"{label}, saved in My vocabulary" : $"Save {label} to My vocabulary");
        save.Click += (_, _) =>
        {
            try { _vocabulary.Add(word, scenario, title, DateTime.UtcNow); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Notify("The word could not be saved: the settings folder is not writable.");
                return;
            }
            ShowToast($"“{label}” saved to My vocabulary.");
            save.Content = "✓ Saved";
            save.IsEnabled = false;
            refresh?.Invoke();
        };
        row.Children.Add(save);
        return row;
    }

    /// <summary>One of the place's words in the end-of-conversation review, with Listen and Save.</summary>
    private void AddWordNote(JsonObject word, string scenario, string title, string language)
    {
        string label = Text(word["word"]) ?? "";
        if (label.Length == 0) return;
        bool used = word["used_by_learner"] is JsonValue flag && flag.TryGetValue(out bool said) && said;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"•  {label} — {Meaning(word, language)}{(used ? "   (you used it)" : "")}",
            TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21, Foreground = NativeTheme.Brush("Ink")
        });
        stack.Children.Add(WordActions(word, scenario, title, refresh: null));
        _chat.Children.Add(new Border
        {
            Child = stack, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 8, 14, 6), MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 60, 8),
            Background = NativeTheme.Brush("White"), BorderBrush = NativeTheme.Brush("Line"), BorderThickness = new Thickness(1)
        });
        UpdateChatHint();
        ScrollChatToEnd();
    }

    private static string Meaning(JsonObject word, string language) =>
        word["meaning"] is JsonObject meaning ? Text(meaning[language]) ?? Text(meaning["en"]) ?? "" : "";

    private static string When(JsonObject entry) =>
        DateTime.TryParse(Text(entry["finished_utc"]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var finished)
            ? finished.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)
            : "";

    // ---- AI configuration -----------------------------------------------------------

    private string AiSummary()
    {
        if (!ApiConfigStore.IsConfigured(_config)) return "Not configured yet. Needed before you can talk with Lucy.";
        try { return $"{ApiConfigStore.GetValidatedBaseUri(_config.BaseUrl).IdnHost} · {_config.Model}"; }
        catch (ArgumentException) { return "Configured."; }
    }

    /// <summary>The same form, checks and encrypted storage as before, as a page instead of a dialog.</summary>
    private FrameworkElement BuildAiPage()
    {
        var panel = new ApiSettingsPanel(_config, showCancel: false) { MinHeight = 560 };
        panel.SavedSettings += (_, _) =>
        {
            _config = ApiConfigStore.Load();
            UpdateOnboarding();
            Notify(ApiConfigStore.IsConfigured(_config)
                ? "AI settings saved. Choose a place and press Start conversation."
                : "The AI settings were saved but are not complete yet.");
            Navigate("settings");
        };
        return Card(panel);
    }
}
