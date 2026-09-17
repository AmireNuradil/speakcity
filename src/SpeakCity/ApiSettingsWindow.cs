using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpeakCity;

/// <summary>A native, owner-supplied API configuration dialog; no browser or JavaScript is used.</summary>
public sealed class ApiSettingsWindow : Window
{
    private readonly TextBox _baseUrl;
    private readonly TextBox _model;
    private readonly PasswordBox _apiKey;
    private readonly TextBlock _targetHost;
    private readonly TextBlock _status;
    private readonly Button _saveButton;
    private readonly Button _testButton;
    private readonly ProgressBar _progress;
    private CancellationTokenSource? _testCancellation;
    private long _editVersion;
    private bool _hasSuccessfulTest;
    private bool _isTesting;
    private bool _isClosed;

    public bool Saved { get; private set; }
    public ApiConfig Configuration { get; private set; }

    public ApiSettingsWindow(ApiConfig current)
    {
        ArgumentNullException.ThrowIfNull(current);
        // Work on a copy. Cancel must not mutate the caller's live configuration.
        Configuration = new ApiConfig
        {
            BaseUrl = current.BaseUrl ?? "",
            Model = current.Model ?? "",
            ApiKey = current.ApiKey ?? ""
        };

        Title = "SpeakCity — API settings";
        Width = 620;
        Height = 700;
        MinWidth = 440;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        Background = SystemColors.WindowBrush;
        Foreground = SystemColors.WindowTextBrush;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        heading.Children.Add(new TextBlock
        {
            Text = "Connect your own AI provider",
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Configuration is required. SpeakCity does not supply or silently select a provider, model, or API key.",
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(heading);

        var fields = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = fields,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 10, 0),
            Focusable = false
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        _baseUrl = new TextBox
        {
            Text = Configuration.BaseUrl,
            MaxLength = ApiConfigStore.MaxBaseUrlLength,
            Padding = new Thickness(8),
            AcceptsReturn = false
        };
        AddField(fields, "HTTPS _Base URL", _baseUrl,
            "Use your provider's API base URL, including /v1 if required. SpeakCity appends /chat/completions once; it never adds another /v1. No credentials, query string, or fragment.");
        AutomationProperties.SetName(_baseUrl, "HTTPS API base URL");
        AutomationProperties.SetHelpText(_baseUrl, "Use the base URL provided by your chosen provider, not the chat completions endpoint.");

        _targetHost = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 14)
        };
        fields.Children.Add(_targetHost);
        AutomationProperties.SetName(_targetHost, "Request destination host");

        _model = new TextBox
        {
            Text = Configuration.Model,
            MaxLength = ApiConfigStore.MaxModelLength,
            Padding = new Thickness(8),
            AcceptsReturn = false
        };
        AddField(fields, "_Model ID", _model, "Enter the exact model ID enabled for your provider account. A display name may not be the API model ID.");
        AutomationProperties.SetName(_model, "API model ID");

        _apiKey = new PasswordBox
        {
            MaxLength = ApiConfigStore.MaxApiKeyLength,
            Padding = new Thickness(8),
            PasswordChar = '●'
        };
        _apiKey.Password = Configuration.ApiKey;
        AddField(fields, "API _key", _apiKey, "Masked here and saved as encrypted settings for this Windows user. It is never added to a URL or a chat message.");
        AutomationProperties.SetName(_apiKey, "API key, masked");

        var notice = new Border
        {
            Padding = new Thickness(12),
            Margin = new Thickness(0, 14, 0, 0),
            BorderBrush = SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock
            {
                Text = "Privacy and access: API features send transcript text to your chosen provider and require an internet connection. Choose a provider permitted for your learners' ages and follow its privacy terms. Do not share master API keys; use a restricted key where available. Test connection sends only a tiny synthetic request, never learner or conversation data, and may incur a small provider charge.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            }
        };
        fields.Children.Add(notice);

        var feedback = new StackPanel { Margin = new Thickness(0, 16, 0, 12) };
        _progress = new ProgressBar
        {
            Height = 3,
            IsIndeterminate = true,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 8)
        };
        feedback.Children.Add(_progress);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 38 };
        AutomationProperties.SetName(_status, "API settings status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        feedback.Children.Add(_status);
        Grid.SetRow(feedback, 2);
        root.Children.Add(feedback);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _testButton = new Button { Content = "Test _connection", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 6) };
        _saveButton = new Button { Content = "_Save", IsDefault = true, MinWidth = 86, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 6) };
        var cancel = new Button { Content = "_Cancel", IsCancel = true, MinWidth = 86, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 0, 6) };
        buttons.Children.Add(_testButton);
        buttons.Children.Add(_saveButton);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        _baseUrl.TextChanged += (_, _) => OnSettingsEdited();
        _model.TextChanged += (_, _) => OnSettingsEdited();
        _apiKey.PasswordChanged += (_, _) => OnSettingsEdited();
        _testButton.Click += TestConnectionClicked;
        _saveButton.Click += SaveClicked;
        cancel.Click += (_, _) => Close();
        Closed += (_, _) =>
        {
            _isClosed = true;
            _testCancellation?.Cancel();
            _apiKey.Clear();
            if (!Saved)
                Configuration.ApiKey = "";
        };
        Loaded += (_, _) => _baseUrl.Focus();
        UpdateTargetHost();
        SetStatus("Check the destination host before testing or saving. Testing does not save your settings.");
    }

    private static void AddField(Panel panel, string label, Control input, string help)
    {
        panel.Children.Add(new Label
        {
            Content = label,
            Target = input,
            Padding = new Thickness(0, 0, 0, 5),
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(input);
        panel.Children.Add(new TextBlock
        {
            Text = help,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 14),
            FontSize = 12
        });
    }

    private void OnSettingsEdited()
    {
        _editVersion++;
        _hasSuccessfulTest = false;
        if (_isClosed)
            return;
        UpdateTargetHost();
        if (!_isTesting)
            SetStatus("Settings changed. Check the destination host, then test or save.");
    }

    private void UpdateTargetHost()
    {
        try
        {
            var uri = ApiConfigStore.GetValidatedBaseUri(_baseUrl.Text);
            var host = uri.IdnHost;
            if (uri.HostNameType == UriHostNameType.IPv6 && !host.StartsWith("[", StringComparison.Ordinal))
                host = "[" + host + "]";
            var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
            // Show only the validated host and port, never URL input that may contain secrets.
            _targetHost.Text = $"Requests and the API key will be sent to: {host}{port} (HTTPS)";
        }
        catch (ArgumentException)
        {
            _targetHost.Text = "Target host: unavailable until a valid HTTPS base URL is entered.";
        }
    }

    private ApiConfig ReadAndValidateForm() => ApiConfigStore.NormalizeAndValidate(new ApiConfig
    {
        BaseUrl = _baseUrl.Text,
        Model = _model.Text,
        ApiKey = _apiKey.Password
    });

    private async void TestConnectionClicked(object sender, RoutedEventArgs e)
    {
        if (_isTesting || _isClosed)
            return;
        ApiConfig snapshot;
        try
        {
            snapshot = ReadAndValidateForm();
        }
        catch (ArgumentException error)
        {
            SetStatus(error.Message);
            return;
        }

        var version = _editVersion;
        using var cancellation = new CancellationTokenSource();
        _testCancellation = cancellation;
        _hasSuccessfulTest = false;
        SetBusy(true);
        SetStatus("Testing the displayed provider with synthetic data only. No settings have been saved. Cancel closes this dialog and stops the test.");
        try
        {
            // Keep all network work and JSON parsing off the WPF UI thread.
            await Task.Run(async () => await new ApiClient(() => snapshot).TestAsync(cancellation.Token)
                .ConfigureAwait(false), cancellation.Token);
            if (!_isClosed && version == _editVersion)
            {
                _hasSuccessfulTest = true;
                SetStatus("Connection successful: the provider returned the expected JSON. Select Save to keep these settings.");
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
                SetStatus("Connection test canceled. Settings were not saved.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or TimeoutException)
        {
            // ApiClient exposes only fixed, sanitized messages for these failure paths.
            if (!_isClosed)
                SetStatus(error.Message);
        }
        catch
        {
            if (!_isClosed)
                SetStatus("The connection test could not be completed. Check the API settings and try again. No settings were saved.");
        }
        finally
        {
            snapshot.ApiKey = "";
            if (ReferenceEquals(_testCancellation, cancellation))
                _testCancellation = null;
            if (!_isClosed)
                SetBusy(false);
        }
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (_isTesting || _isClosed)
            return;
        ApiConfig snapshot;
        try
        {
            snapshot = ReadAndValidateForm();
        }
        catch (ArgumentException error)
        {
            SetStatus(error.Message);
            return;
        }

        try
        {
            if (!_hasSuccessfulTest && MessageBox.Show(this,
                    "These settings have not passed a connection test. Save them anyway? The app will send transcript text to the displayed provider when API features are used.",
                    "Save without a successful test?", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                snapshot.ApiKey = "";
                return;
            }
            ApiConfigStore.Save(snapshot);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            snapshot.ApiKey = "";
            SetStatus(error.Message);
            return;
        }
        catch
        {
            snapshot.ApiKey = "";
            SetStatus("The settings could not be saved securely. Check Windows storage permissions and try again.");
            return;
        }

        Configuration = snapshot;
        Saved = true;
        // This is a modal settings dialog. The caller should use ShowDialog(), then reload the store.
        DialogResult = true;
    }

    private void SetBusy(bool busy)
    {
        _isTesting = busy;
        _baseUrl.IsEnabled = !busy;
        _model.IsEnabled = !busy;
        _apiKey.IsEnabled = !busy;
        _saveButton.IsEnabled = !busy;
        _testButton.IsEnabled = !busy;
        _testButton.Content = busy ? "Testing…" : "Test _connection";
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string message)
    {
        _status.Text = message;
    }
}
