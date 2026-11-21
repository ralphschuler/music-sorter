using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Styling;
using MusicSorter.Core;
using MusicSorter.Metadata;

namespace MusicSorter.Desktop;

public sealed class MainWindow : Window
{
    private const string DefaultPattern = "{artistInitial}/{artist}/{title}";
    private static readonly string[] Tokens = ["{artistInitial}", "{artist}", "{genre}", "{year}", "{title}", "{fileName}"];
    private readonly TagLibStore _tags = new();
    private readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private readonly Dictionary<(string Path, string Sha256), TrackTags> _approved = new();
    private readonly TextBlock _error = Label("", 13, color: Studio.Muted);
    private SortProfile? _profile;
    private ProfileIndexSession? _index;
    private IndexMailbox? _mailbox;
    private ProfileIndexUpdate? _latest;
    private CancellationTokenSource? _sort;
    private bool _transition;
    private bool _closing;
    private Action? _tick;

    public MainWindow()
    {
        Title = "Music Sorter";
        Width = 920;
        Height = 680;
        MinWidth = 590;
        MinHeight = 490;
        _pulse.Tick += (_, _) => _tick?.Invoke();
        Opened += async (_, _) => await NavigateAsync(ShowProfilesAsync);
        Closing += OnClosing;
        Closed += (_, _) => _pulse.Stop();
        SetPage("Profiles", Label("Loading…", color: Studio.Muted));
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closing) return;
        if (_transition)
        {
            args.Cancel = true;
            return;
        }
        if (_sort is not null)
        {
            args.Cancel = true;
            _sort.Cancel();
            _error.Text = "Cancelling. Close once the results are shown.";
            return;
        }
        if (_index is null) return;
        args.Cancel = true;
        _closing = true;
        await NavigateAsync(StopIndexAsync);
        Close();
    }

    private async Task NavigateAsync(Func<Task> action)
    {
        if (_transition || _sort is not null) return;
        _transition = true;
        if (Content is Control control) control.IsEnabled = false;
        _error.Text = "";
        try { await action(); }
        catch (Exception error) { _error.Text = error.Message; }
        finally
        {
            _transition = false;
            if (Content is Control current) current.IsEnabled = true;
        }
    }

    private async Task ShowProfilesAsync()
    {
        await StopIndexAsync();
        _profile = null;
        _latest = null;
        _approved.Clear();
        var profiles = await ProfileStore.LoadAsync();
        var list = ListSurface();
        list.ItemsSource = profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        list.ItemTemplate = new FuncDataTemplate<SortProfile>((profile, _) => profile is null ? null : ProfileRow(profile));
        SetName(list, "Saved profiles");
        var open = ActionButton("Open", () => list.SelectedItem is SortProfile p ? NavigateAsync(() => OpenProfileAsync(p)) : Task.CompletedTask);
        var edit = ActionButton("Edit", () => list.SelectedItem is SortProfile p
            ? NavigateAsync(() => ShowFoldersAsync(new Draft(p))) : Task.CompletedTask);
        open.IsEnabled = edit.IsEnabled = false;
        list.SelectionChanged += (_, _) => open.IsEnabled = edit.IsEnabled = list.SelectedItem is SortProfile;
        list.DoubleTapped += async (_, _) =>
        {
            if (list.SelectedItem is SortProfile p) await NavigateAsync(() => OpenProfileAsync(p));
        };
        var body = profiles.Count == 0
            ? Label("No saved profiles.", color: Studio.Muted)
            : Table(list, ["Profile", "Input folders"], "*,112");
        SetPage("Profiles", body,
            Actions(edit, open, Primary("New profile", () => NavigateAsync(() => ShowFoldersAsync(new Draft())))));
    }

    private Task ShowFoldersAsync(Draft draft)
    {
        var list = ListSurface();
        list.ItemsSource = new ObservableCollection<string>(draft.Sources);
        list.MinHeight = 100;
        list.ItemTemplate = new FuncDataTemplate<string>((path, _) => path is null ? null : Cell(path, trim: true));
        var inputs = (ObservableCollection<string>)list.ItemsSource!;
        var output = new TextBox { IsReadOnly = true, Text = draft.Output, PlaceholderText = "Choose an output folder" };
        SetName(list, "Input folders");
        SetName(output, "Output folder");
        var add = ActionButton("Add input folders…", () => NavigateAsync(async () =>
        {
            var existing = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            foreach (var selected in await PickFoldersAsync("Choose input folders", multiple: true))
                if (!inputs.Contains(selected, existing)) inputs.Add(selected);
            draft.Sources = inputs.ToArray();
        }));
        var remove = ActionButton("Remove selected", () =>
        {
            if (list.SelectedItem is string selected) inputs.Remove(selected);
            draft.Sources = inputs.ToArray();
            return Task.CompletedTask;
        });
        remove.IsEnabled = false;
        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItem is string;
        var next = Primary("Continue", () => NavigateAsync(() =>
        {
            SortEngine.ValidateProfileFolders(draft.Sources, draft.Output);
            return ShowPatternAsync(draft);
        }));
        next.IsEnabled = inputs.Count > 0 && draft.Output.Length > 0;
        inputs.CollectionChanged += (_, _) => next.IsEnabled = inputs.Count > 0 && draft.Output.Length > 0;
        var browse = ActionButton("Choose output…", () => NavigateAsync(async () =>
        {
            var selected = (await PickFoldersAsync("Choose output folder", multiple: false)).FirstOrDefault();
            if (selected is null) return;
            draft.Output = selected;
            output.Text = selected;
            next.IsEnabled = inputs.Count > 0;
        }));
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), RowSpacing = 10 };
        Place(body, Label("Input folders", 13, FontWeight.SemiBold), 0);
        Place(body, Studio.Surface(list), 1);
        Place(body, Actions(alignRight: false, add, remove), 2);
        var outputLabel = Label("Output folder", 13, FontWeight.SemiBold);
        outputLabel.Margin = new Thickness(0, 12, 0, 0);
        Place(body, outputLabel, 3);
        Place(body, TwoColumns(output, browse), 4);
        SetPage("Folders", body, Actions(ActionButton("Cancel", () => NavigateAsync(ShowProfilesAsync)), next), step: "Step 1 of 2");
        return Task.CompletedTask;
    }

    private Task ShowPatternAsync(Draft draft)
    {
        var pattern = new TextBox { Text = draft.Template, PlaceholderText = DefaultPattern, MaxLength = 240 };
        var name = new TextBox { Text = draft.Name, PlaceholderText = "Profile name", MaxLength = 80 };
        SetName(pattern, "Naming pattern");
        SetName(name, "Profile name");
        void RememberInputs()
        {
            draft.Name = name.Text?.Trim() ?? "";
            draft.Template = pattern.Text?.Trim() ?? "";
        }

        var chips = new WrapPanel();
        foreach (var token in Tokens)
        {
            var part = ActionButton(token, () =>
            {
                InsertToken(pattern, token);
                return Task.CompletedTask;
            });
            part.FontFamily = Studio.Mono;
            part.FontSize = 12;
            chips.Children.Add(part);
        }
        pattern.FontFamily = Studio.Mono;
        var save = Primary("Save and open", () => NavigateAsync(async () =>
        {
            RememberInputs();
            if (draft.Name.Length == 0 || draft.Template.Length == 0)
                throw new InvalidOperationException("Enter a profile name and a naming pattern.");
            SortEngine.ValidateProfileFolders(draft.Sources, draft.Output);
            // The core validates token syntax even for an empty plan; no destination is written.
            SortEngine.Plan([], draft.Output, draft.Template);
            var profile = new SortProfile(draft.Id, draft.Name, draft.Sources, draft.Output, draft.Template);
            await ProfileStore.SaveAsync(profile);
            await OpenProfileAsync(profile);
        }));
        var body = new StackPanel { Spacing = 9, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(Label("Naming pattern", 13, FontWeight.SemiBold));
        body.Children.Add(pattern);
        body.Children.Add(chips);
        body.Children.Add(Label("Use / to separate folders. The file extension stays the same.", 13, color: Studio.Muted));
        var nameLabel = Label("Profile name", 13, FontWeight.SemiBold);
        nameLabel.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(nameLabel);
        body.Children.Add(name);
        var back = ActionButton("Back", () => NavigateAsync(() =>
        {
            RememberInputs();
            return ShowFoldersAsync(draft);
        }));
        SetPage("Naming pattern", body, Actions(back, save), step: "Step 2 of 2");
        name.Focus();
        return Task.CompletedTask;
    }

    private async Task<string[]> PickFoldersAsync(string title, bool multiple)
    {
        if (!StorageProvider.CanPickFolder)
            throw new InvalidOperationException("This platform does not provide a native folder picker.");
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = multiple });
        var local = new List<string>();
        foreach (var item in picked)
        {
            using (item)
            {
                var path = item.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    local.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            }
        }
        if (picked.Count > 0 && local.Count == 0)
            throw new InvalidOperationException("Choose an existing local folder.");
        return local.ToArray();
    }

    private async Task OpenProfileAsync(SortProfile profile)
    {
        await StopIndexAsync();
        if (_profile?.Id != profile.Id)
        {
            _approved.Clear();
            _latest = null;
        }
        _profile = profile;
        var list = ListSurface();
        list.ItemTemplate = new FuncDataTemplate<Track>((track, _) => track is null ? null : IndexRow(track));
        SetName(list, "Indexed tracks");
        var status = Label("Opening index…", 13, color: Studio.Muted);
        var start = Primary("Start sorting", StartSortingAsync);
        var review = ActionButton("Review", () => NavigateAsync(ShowReviewAsync));
        start.IsEnabled = review.IsEnabled = false;

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        Place(body, status, 0);
        Place(body, Table(list, ["Title", "Artist", "Genre", "Year"], "3*,2*,1.4*,52"), 1);
        var edit = ActionButton("Edit profile", () => NavigateAsync(async () =>
        {
            await StopIndexAsync();
            // A saved edit can change the roots, so don't carry the old session's cache/proposals through the editor.
            _profile = null;
            _latest = null;
            _approved.Clear();
            await ShowFoldersAsync(new Draft(profile));
        }));
        SetPage(profile.Name, body, Actions(ActionButton("Profiles", () => NavigateAsync(ShowProfilesAsync)), edit, review, start));

        var mailbox = new IndexMailbox();
        _mailbox = mailbox;
        _latest = null;
        _index = new ProfileIndexSession(profile, _tags, mailbox);
        _tick = () =>
        {
            if (!ReferenceEquals(mailbox, _mailbox) || ReferenceEquals(_latest, mailbox.Latest)) return;
            var update = mailbox.Latest;
            if (update is null) return;
            _latest = update;
            if (!ReferenceEquals(list.ItemsSource, update.Tracks)) list.ItemsSource = update.Tracks;
            status.Text = update.IsRefreshing
                ? $"{update.Tracks.Count:N0} tracks · Updating index…"
                : $"{update.Tracks.Count:N0} tracks" + (string.IsNullOrWhiteSpace(update.Message) ? "" : $" · {update.Message}");
            start.IsEnabled = review.IsEnabled = !update.IsRefreshing && update.Tracks.Count > 0;
        };
        _pulse.Start();
        try { _index.Start(); }
        catch
        {
            await StopIndexAsync();
            throw;
        }
    }

    private async Task StopIndexAsync()
    {
        var index = _index;
        _index = null;
        _mailbox = null; // Disassociate queued updates before disposing the watcher.
        _pulse.Stop();
        _tick = null;
        if (index is not null) await index.DisposeAsync();
    }

    private async Task ShowReviewAsync()
    {
        if (_profile is not SortProfile profile ||
            (_mailbox?.Latest ?? _latest) is not { IsRefreshing: false, Tracks.Count: > 0 } snapshot) return;
        await StopIndexAsync();
        _latest = snapshot;

        var list = ListSurface();
        list.ItemTemplate = new FuncDataTemplate<ReviewRow>((row, _) => row is null ? null : PlanRow(row));
        SetName(list, "Proposed copies");
        var status = Label("", 13, color: Studio.Muted);
        var review = ActionButton("Find missing tags…", () => NavigateAsync(async () =>
        {
            if (list.SelectedItem is not ReviewRow row) return;
            var track = row.Item.Track;
            var fingerprint = snapshot.Fingerprints.GetValueOrDefault(track.Sha256);
            var matches = fingerprint is null ? 0 : await Task.Run(() => snapshot.Fingerprints
                .Where(pair => !pair.Key.Equals(track.Sha256, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(pair.Value.DurationSeconds - fingerprint.DurationSeconds) <= Math.Max(4, fingerprint.DurationSeconds * .05))
                .Take(64).Count(pair => FingerprintSimilarity.Score(fingerprint, pair.Value) >= .92));
            var proposed = await new MetadataDialog(track, fingerprint, matches).ShowDialog<TrackTags?>(this);
            if (proposed is null || proposed == track.Tags) return;
            _approved[(track.Source, track.Sha256)] = proposed;
            await RefreshReviewAsync();
        }));
        review.IsEnabled = false;
        list.SelectionChanged += (_, _) => review.IsEnabled = list.SelectedItem is ReviewRow { Item.Status: not PlanStatus.Duplicate };

        async Task RefreshReviewAsync()
        {
            var rows = await Task.Run(() =>
            {
                var similar = snapshot.Fingerprints.GroupBy(x => x.Value.Value, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1).SelectMany(g => g.Select(x => x.Key))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return MakePlan(profile, snapshot.Tracks).Select(x => new ReviewRow(x,
                    x.Destination is null ? Path.GetFileName(x.Track.Source) : Path.GetRelativePath(profile.Output, x.Destination),
                    similar.Contains(x.Track.Sha256))).ToArray();
            });
            list.ItemsSource = rows;
            var duplicates = rows.Count(x => x.Item.Status == PlanStatus.Duplicate);
            var variants = rows.Count(x => x.Item.Status == PlanStatus.Variant);
            status.Text = $"{duplicates:N0} byte duplicates skipped · {variants:N0} variants kept";
        }
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        Place(body, status, 0);
        Place(body, Table(list, ["Destination", "Status"], "*,170"), 1);
        SetPage("Review", body, Actions(ActionButton("Back to index", () => NavigateAsync(() => OpenProfileAsync(profile))), review));
        await RefreshReviewAsync();
    }

    private IReadOnlyList<PlanItem> MakePlan(SortProfile profile, IReadOnlyList<Track> tracks) =>
        SortEngine.Plan(tracks.Select(track => _approved.TryGetValue((track.Source, track.Sha256), out var tags)
            ? track with { Tags = tags }
            : track), profile.Output, profile.Template);

    private async Task StartSortingAsync()
    {
        if (_transition || _sort is not null || _profile is not SortProfile profile ||
            (_mailbox?.Latest ?? _latest) is not { IsRefreshing: false, Tracks.Count: > 0 } snapshot) return;
        using var operation = new CancellationTokenSource();
        _sort = operation;
        var ct = operation.Token;
        try
        {
            await StopIndexAsync();
            _latest = snapshot;
            var recentRows = new ObservableCollection<string>();
            var resultList = ListSurface();
            resultList.ItemsSource = recentRows;
            resultList.ItemTemplate = new FuncDataTemplate<string>((value, _) => value is null ? null : Cell(value, trim: false));
            SetName(resultList, "Sort results");
            var status = Label("Preparing copies…", 13, color: Studio.Muted);
            var progress = new ProgressBar { IsIndeterminate = true, Height = 7, Minimum = 0 };
            var cancel = ActionButton("Cancel", () =>
            {
                operation.Cancel();
                status.Text = "Cancelling…";
                return Task.CompletedTask;
            });
            var done = Primary("Back to index", () => NavigateAsync(() => OpenProfileAsync(profile)));
            done.IsVisible = false;
            var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 10 };
            Place(body, status, 0);
            Place(body, progress, 1);
            Place(body, Studio.Surface(resultList), 2);
            SetPage("Copying", body, Actions(cancel, done));
            var pending = new ConcurrentQueue<ProfileExportResult>();
            var count = 0;
            var copied = 0;
            var attention = 0;
            var planCount = 0;
            _tick = () =>
            {
                for (var i = 0; i < 100 && pending.TryDequeue(out var row); i++)
                {
                    recentRows.Add(DescribeResult(row, profile.Output));
                }
                progress.Value = Volatile.Read(ref count);
                if (!ct.IsCancellationRequested && planCount > 0)
                    status.Text = $"{Volatile.Read(ref count):N0} of {planCount:N0} checked";
            };
            _pulse.Start();
            try
            {
                var plan = await Task.Run(() => MakePlan(profile, snapshot.Tracks), ct);
                ct.ThrowIfCancellationRequested();
                planCount = plan.Count;
                progress.Maximum = Math.Max(1, planCount);
                progress.IsIndeterminate = false;
                var sink = new InlineProgress<ProfileExportResult>(value =>
                {
                    pending.Enqueue(value);
                    if (value.Outcome == "Copied") Interlocked.Increment(ref copied);
                    if (value.Outcome is "Conflict" or "Failed") Interlocked.Increment(ref attention);
                    Interlocked.Increment(ref count);
                });
                await Task.Run(() => ProfileSorter.ExportAsync(profile, plan, _tags, sink, ct), ct);
                status.Text = attention == 0
                    ? $"Copied {copied:N0} files. Originals are unchanged."
                    : $"Copied {copied:N0} files · {attention:N0} need attention. Originals are unchanged.";
            }
            catch (OperationCanceledException)
            {
                status.Text = $"Cancelled after {copied:N0} copies. Completed copies remain.";
            }
            catch (Exception error)
            {
                status.Text = $"Stopped after {copied:N0} copies: {error.Message}";
            }
            _tick = null;
            _pulse.Stop();
            if (!pending.IsEmpty)
                resultList.ItemsSource = await Task.Run(() => recentRows.Concat(pending.Select(r => DescribeResult(r, profile.Output))).ToArray());
            progress.Value = Volatile.Read(ref count);
            cancel.IsVisible = false;
            done.IsVisible = true;
            Title = "Music Sorter · Results";
        }
        catch (Exception error)
        {
            _error.Text = $"Could not continue: {error.Message}";
        }
        finally
        {
            _tick = null;
            _pulse.Stop();
            _sort = null;
        }
    }

    private static string DescribeResult(ProfileExportResult result, string output)
    {
        var relative = result.Item.Destination is string destination ? Path.GetRelativePath(output, destination)
            : Path.GetFileName(result.Item.Track.Source);
        return $"{result.Outcome} · {relative}" + (string.IsNullOrWhiteSpace(result.Message) ? "" : $" · {result.Message}");
    }

    private void SetPage(string title, Control body, Control? actions = null, string? step = null)
    {
        if (_error.Parent is Grid oldContent) oldContent.Children.Remove(_error);
        var shell = new Grid { Background = Studio.Canvas, RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(28, 17) };
        var name = Label(title, 24, FontWeight.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(0, 0, 12, 0);
        ToolTip.SetTip(name, title);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        header.Children.Add(name);
        if (step is not null)
        {
            var stepLabel = Label(step, 13, color: Studio.Muted);
            stepLabel.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(stepLabel, 1);
            header.Children.Add(stepLabel);
        }
        Place(shell, Studio.Band(header, bottom: true), 0);

        var content = new Grid { Margin = new Thickness(28, 20), RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 6 };
        Place(content, body, 0);
        Place(content, _error, 1);
        Place(shell, content, 1);
        if (actions is not null)
        {
            actions.Margin = new Thickness(28, 10);
            Place(shell, Studio.Band(actions, bottom: false), 2);
        }

        Title = $"Music Sorter · {title}";
        Content = shell;
    }

    private static ListBox ListSurface()
    {
        var list = new ListBox
        {
            Background = Studio.Paper,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
        list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(14, 4)),
                new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
            }
        });
        return list;
    }

    private static Control Table(ListBox list, string[] headings, string columns)
    {
        var headingsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(columns),
            ColumnSpacing = 13, Margin = new Thickness(14, 8, 14, 8)
        };
        for (var i = 0; i < headings.Length; i++)
        {
            var cell = Label(headings[i], 12, FontWeight.SemiBold, Studio.Muted);
            Grid.SetColumn(cell, i);
            headingsGrid.Children.Add(cell);
        }
        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Place(table, new Border
        {
            Background = Studio.Canvas, BorderBrush = Studio.Line,
            BorderThickness = new Thickness(0, 0, 0, 1), Child = headingsGrid
        }, 0);
        Place(table, list, 1);
        return Studio.Surface(table);
    }

    private static Control ProfileRow(SortProfile profile) => Row("*,112",
        Cell(profile.Name, weight: FontWeight.SemiBold),
        Cell(profile.Sources.Length.ToString(), color: Studio.Muted));

    private static Control IndexRow(Track track)
    {
        var title = string.IsNullOrWhiteSpace(track.Tags.Title) ? Path.GetFileName(track.Source) : track.Tags.Title;
        var row = Row("3*,2*,1.4*,52",
            Cell(title, weight: FontWeight.Medium),
            Cell(Display(track.Tags.Artist), color: Studio.Muted),
            Cell(Display(track.Tags.Genre), color: Studio.Muted),
            Cell(track.Tags.Year == 0 ? "–" : track.Tags.Year.ToString(), color: Studio.Muted));
        ToolTip.SetTip(row, track.Source);
        return row;
    }

    private static Control PlanRow(ReviewRow row)
    {
        var state = row.Item.Status switch
        {
            PlanStatus.Duplicate => "Skip duplicate",
            PlanStatus.Variant => "Keep variant",
            _ => "Copy",
        };
        if (row.PossibleMatch) state += " · Audio match?";
        var content = Row("*,170", Cell(row.Relative), Cell(state, color: Studio.Muted));
        ToolTip.SetTip(content, row.Item.Track.Source);
        return content;
    }

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "–" : value;

    private static Control Row(string columns, params TextBlock[] values)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 13, MinHeight = 26 };
        for (var i = 0; i < values.Length; i++)
        {
            Grid.SetColumn(values[i], i);
            row.Children.Add(values[i]);
        }
        return row;
    }

    private static TextBlock Cell(string value, bool trim = true, FontWeight? weight = null, IBrush? color = null) =>
        new()
        {
            Text = value, FontSize = 13, FontWeight = weight ?? FontWeight.Normal,
            TextWrapping = trim ? TextWrapping.NoWrap : TextWrapping.Wrap,
            TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            VerticalAlignment = VerticalAlignment.Center, Foreground = color ?? Studio.Ink
        };

    private static TextBlock Label(string value, double size = 14, FontWeight? weight = null, IBrush? color = null) =>
        new() { Text = value, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap, Foreground = color ?? Studio.Ink };

    private static Control Actions(params Button[] items) => Actions(true, items);

    private static Control Actions(bool alignRight, params Button[] items)
    {
        var panel = new WrapPanel { HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        foreach (var item in items) panel.Children.Add(item);
        return panel;
    }

    private static Button ActionButton(string caption, Func<Task> onClick)
    {
        var button = new Button { Content = caption, Margin = new Thickness(3, 2), Padding = new Thickness(13, 7), FontSize = 13 };
        button.Click += async (_, _) => await onClick();
        return button;
    }

    private static Button Primary(string caption, Func<Task> onClick)
    {
        var button = ActionButton(caption, onClick);
        button.Classes.Add("accent");
        return button;
    }

    private static Control TwoColumns(Control primary, Control secondary)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(primary);
        Grid.SetColumn(secondary, 1);
        row.Children.Add(secondary);
        return row;
    }

    private static void Place(Grid grid, Control child, int row)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private static void InsertToken(TextBox pattern, string token)
    {
        var current = pattern.Text ?? "";
        var start = Math.Clamp(Math.Min(pattern.SelectionStart, pattern.SelectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(pattern.SelectionStart, pattern.SelectionEnd), start, current.Length);
        if (current.Length - (end - start) + token.Length > pattern.MaxLength) return;
        pattern.Text = current[..start] + token + current[end..];
        pattern.Focus();
        pattern.CaretIndex = start + token.Length;
    }

    private static void SetName(Control control, string name) => control.SetValue(AutomationProperties.NameProperty, name);

    private sealed class Draft(SortProfile? original = null)
    {
        public readonly Guid Id = original?.Id ?? Guid.NewGuid();
        public string Name = original?.Name ?? "";
        public string[] Sources = original?.Sources.ToArray() ?? [];
        public string Output = original?.Output ?? "";
        public string Template = original?.Template ?? DefaultPattern;
    }

    private sealed class IndexMailbox : IProgress<ProfileIndexUpdate>
    {
        private ProfileIndexUpdate? _latest;
        public ProfileIndexUpdate? Latest => Volatile.Read(ref _latest);
        public void Report(ProfileIndexUpdate value) => Volatile.Write(ref _latest, value);
    }

    private sealed class InlineProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    private sealed record ReviewRow(PlanItem Item, string Relative, bool PossibleMatch);
}
