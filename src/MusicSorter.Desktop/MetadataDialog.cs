using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using MusicSorter.Core;
using MusicSorter.Metadata;

namespace MusicSorter.Desktop;

/// <summary>Operator selection and informed, per-request opt-in; no request is initiated by a scan or opening this window.</summary>
internal sealed class MetadataDialog : Window
{
    private readonly TrackTags _current;
    private readonly AudioFingerprint? _fingerprint;
    private readonly TextBox _artist = new() { PlaceholderText = "Artist for MusicBrainz" };
    private readonly TextBox _title = new() { PlaceholderText = "Title for MusicBrainz" };
    private readonly CheckBox _sendText = new() { Content = "Send the artist and title above to MusicBrainz" };
    private readonly CheckBox _sendFingerprint = new() { Content = "Send the local fingerprint and duration to AcoustID" };
    private readonly Button _musicBrainz = new() { Content = "Search MusicBrainz" };
    private readonly Button _acoustId = new() { Content = "Search AcoustID (fallback)" };
    private readonly Button _use = new() { Content = "Use missing tags on copy", Classes = { "accent" } };
    private readonly Button _close = new() { Content = "Close" };
    private readonly ListBox _results = new() { MinHeight = 120 };
    private readonly TextBlock _status = Label("Review your search values and choose a provider.");
    private CancellationTokenSource? _lookup;

    public MetadataDialog(Track track, AudioFingerprint? fingerprint, int possibleMatches)
    {
        _current = track.Tags;
        _fingerprint = fingerprint;
        var fileStem = Path.GetFileNameWithoutExtension(track.Source).Replace('_', ' ').Trim();
        var fileParts = fileStem.Split(" - ", 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _artist.Text = !string.IsNullOrWhiteSpace(_current.Artist) ? _current.Artist
            : fileParts.Length == 2 ? fileParts[0] : "";
        _title.Text = !string.IsNullOrWhiteSpace(_current.Title) ? _current.Title
            : fileParts.Length == 2 ? fileParts[1] : fileStem;
        _artist.SetValue(AutomationProperties.NameProperty, "MusicBrainz search artist");
        _title.SetValue(AutomationProperties.NameProperty, "MusicBrainz search title");
        _results.SetValue(AutomationProperties.NameProperty, "Metadata candidates");
        Title = "Review metadata";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 690;
        Height = 610;
        MinWidth = 570;
        MinHeight = 550;
        _results.Background = Studio.Paper;
        _results.BorderThickness = new Thickness(0);
        _results.ItemTemplate = new FuncDataTemplate<Choice>((candidate, _) =>
        {
            if (candidate is null) return null;
            var title = candidate.Year == 0 ? candidate.Title : $"{candidate.Title} ({candidate.Year})";
            var name = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold,
                Foreground = Studio.Ink, TextWrapping = TextWrapping.Wrap };
            var detail = Label($"{candidate.Artist} · {candidate.Evidence}");
            return new StackPanel { Spacing = 2, Margin = new Thickness(3, 4), Children = { name, detail } };
        });

        var header = new StackPanel { Spacing = 3, Margin = new Thickness(24, 15) };
        header.Children.Add(new TextBlock { Text = "Find missing tags", FontSize = 21,
            FontWeight = FontWeight.SemiBold, Foreground = Studio.Ink });
        header.Children.Add(Label(Path.GetFileName(track.Source)));

        var terms = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        terms.Children.Add(_artist);
        Grid.SetColumn(_title, 1);
        terms.Children.Add(_title);
        var fields = new StackPanel { Spacing = 7 };
        fields.Children.Add(terms);
        fields.Children.Add(ConsentRow(_sendText, _musicBrainz));
        fields.Children.Add(ConsentRow(_sendFingerprint, _acoustId));
        fields.Children.Add(Label("Lookups never send audio or its file path. Only an accepted result changes missing tags, on the copy."));
        if (fingerprint is not null)
            fields.Children.Add(Label($"Local audio shortlist: {possibleMatches:N0} possible matches (up to 64 compared). This never skips a file."));

        var body = new Grid { Margin = new Thickness(24, 16, 24, 12),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 9 };
        Add(body, fields, 0);
        Add(body, Studio.Surface(_results), 1);
        Add(body, _status, 2);

        var actions = new StackPanel { Margin = new Thickness(24, 10), HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_close);
        actions.Children.Add(_use);
        var layout = new Grid { Background = Studio.Canvas, RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Add(layout, Studio.Band(header, bottom: true), 0);
        Add(layout, body, 1);
        Add(layout, Studio.Band(actions, bottom: false), 2);
        Content = layout;

        _artist.TextChanging += (_, _) => TermsChanged();
        _title.TextChanging += (_, _) => TermsChanged();
        _sendText.IsCheckedChanged += (_, _) => Refresh();
        _sendFingerprint.IsCheckedChanged += (_, _) => Refresh();
        _results.SelectionChanged += (_, _) => ShowSelection();
        _musicBrainz.Click += async (_, _) => await LookupAsync(acoustId: false);
        _acoustId.Click += async (_, _) => await LookupAsync(acoustId: true);
        _use.Click += (_, _) =>
        {
            if (_lookup is null && _results.SelectedItem is Choice choice && Merge(choice.Tags) is { } proposed && proposed != _current)
                Close(proposed);
        };
        _close.Click += (_, _) =>
        {
            if (_lookup is not null) _lookup.Cancel();
            else Close(null);
        };
        Closing += (_, args) =>
        {
            if (_lookup is null) return;
            args.Cancel = true;
            _lookup.Cancel();
        };
        Refresh();
    }

    private void TermsChanged()
    {
        if (_lookup is not null) return;
        _sendText.IsChecked = false;
        _results.ItemsSource = null;
        _status.Text = "Review your updated search values before choosing a provider.";
        Refresh();
    }

    private void ShowSelection()
    {
        if (_lookup is null && _results.SelectedItem is Choice choice)
        {
            var merged = Merge(choice.Tags);
            var fill = new List<string>();
            if (merged.Artist != _current.Artist) fill.Add($"artist: {merged.Artist}");
            if (merged.Title != _current.Title) fill.Add($"title: {merged.Title}");
            if (merged.Year != _current.Year) fill.Add($"year: {merged.Year}");
            _status.Text = fill.Count == 0 ? "This candidate adds no missing fields." : "On the copy only: " + string.Join(" · ", fill);
        }
        Refresh();
    }

    private TrackTags Merge(TrackTags proposed) => _current with
    {
        Artist = string.IsNullOrWhiteSpace(_current.Artist) ? proposed.Artist : _current.Artist,
        Title = string.IsNullOrWhiteSpace(_current.Title) ? proposed.Title : _current.Title,
        Year = _current.Year == 0 ? proposed.Year : _current.Year
    };

    private async Task LookupAsync(bool acoustId)
    {
        if (_lookup is not null) return;
        if (acoustId ? _fingerprint is null || _sendFingerprint.IsChecked != true
                     : _sendText.IsChecked != true || (string.IsNullOrWhiteSpace(_artist.Text) && string.IsNullOrWhiteSpace(_title.Text)))
            return;
        var artist = _artist.Text ?? "";
        var title = _title.Text ?? "";
        using var operation = new CancellationTokenSource();
        _lookup = operation;
        _results.ItemsSource = null;
        _status.Text = acoustId ? "Looking up the selected local fingerprint…" : "Looking up the reviewed text…";
        Refresh();
        try
        {
            Choice[] candidates = acoustId
                ? (await new AcoustIdClient().LookupAsync(_fingerprint!, operation.Token))
                    .Select(c => new Choice(c.Artist, c.Title, 0, $"AcoustID score {c.Score:P0}")).ToArray()
                : (await new MusicBrainzClient().LookupAsync(artist, title, durationSeconds: null, ct: operation.Token))
                    .Select(c => new Choice(c.Artist, c.Title, c.Year, $"MusicBrainz score {c.Score}")).ToArray();
            operation.Token.ThrowIfCancellationRequested();
            _results.ItemsSource = candidates;
            _status.Text = candidates.Length == 0
                ? "No candidates returned. Review the terms or try the other provider explicitly."
                : "Choose a candidate to review exactly which missing fields would be added.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = operation.IsCancellationRequested ? "Lookup cancelled." : "Lookup timed out. No tags were changed.";
        }
        catch (InvalidOperationException) when (acoustId && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACOUSTID_API_KEY")))
        {
            _status.Text = "Set ACOUSTID_API_KEY before opening Music Sorter to use AcoustID.";
        }
        catch (Exception error) when (error is ArgumentException or HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            // Network errors can include a provider URL or request parameters. Never surface those here.
            _status.Text = "Lookup couldn't complete. Check your connection or search values. No tags were changed.";
        }
        catch (Exception)
        {
            _status.Text = "Lookup couldn't complete. No tags were changed.";
        }
        finally
        {
            _lookup = null;
            _sendText.IsChecked = false;
            _sendFingerprint.IsChecked = false;
            Refresh();
        }
    }

    private void Refresh()
    {
        var idle = _lookup is null;
        _artist.IsEnabled = _title.IsEnabled = _sendText.IsEnabled = idle;
        _sendFingerprint.IsEnabled = idle && _fingerprint is not null;
        _musicBrainz.IsEnabled = idle && _sendText.IsChecked == true &&
            (!string.IsNullOrWhiteSpace(_artist.Text) || !string.IsNullOrWhiteSpace(_title.Text));
        _acoustId.IsEnabled = idle && _fingerprint is not null && _sendFingerprint.IsChecked == true;
        _use.IsEnabled = idle && _results.SelectedItem is Choice c && Merge(c.Tags) != _current;
        _close.Content = idle ? "Close" : "Cancel lookup";
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, FontSize = 13, Foreground = Studio.Muted, TextWrapping = TextWrapping.Wrap };

    private static Control ConsentRow(CheckBox consent, Button action)
    {
        if (consent.Content is string text)
            consent.Content = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        consent.VerticalAlignment = VerticalAlignment.Center;
        action.VerticalAlignment = VerticalAlignment.Center;
        action.FontSize = 13;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(consent);
        Grid.SetColumn(action, 1);
        row.Children.Add(action);
        return row;
    }

    private static void Add(Grid grid, Control item, int row)
    {
        Grid.SetRow(item, row);
        grid.Children.Add(item);
    }

    private sealed record Choice(string Artist, string Title, uint Year, string Evidence)
    {
        public TrackTags Tags => new(Artist, Title, "", Year);
        public override string ToString() => $"{Artist}  ·  {Title}" + (Year > 0 ? $"  ({Year})" : "") + $"  ·  {Evidence}";
    }
}
