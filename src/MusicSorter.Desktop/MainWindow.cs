using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
    private readonly TextBlock _error = Label("", 13);
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
        SetPage("Music Sorter", Label("Loading profiles…"));
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
        var list = new ListBox
        {
            ItemsSource = profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            ItemTemplate = new FuncDataTemplate<SortProfile>((profile, _) => profile is null ? null : Label(profile.Name, 16, FontWeight.SemiBold))
        };
        SetName(list, "Saved profiles");
        var open = Primary("Open", () => list.SelectedItem is SortProfile p ? NavigateAsync(() => OpenProfileAsync(p)) : Task.CompletedTask);
        var edit = ActionButton("Edit", () => list.SelectedItem is SortProfile p
            ? NavigateAsync(() => ShowFoldersAsync(new Draft(p))) : Task.CompletedTask);
        open.IsEnabled = edit.IsEnabled = false;
        list.SelectionChanged += (_, _) => open.IsEnabled = edit.IsEnabled = list.SelectedItem is SortProfile;
        list.DoubleTapped += async (_, _) =>
        {
            if (list.SelectedItem is SortProfile p) await NavigateAsync(() => OpenProfileAsync(p));
        };
        SetPage("Profiles", profiles.Count == 0 ? Label("No saved profiles.") : list,
            Actions(Primary("New profile", () => NavigateAsync(() => ShowFoldersAsync(new Draft()))), open, edit));
    }

    private Task ShowFoldersAsync(Draft draft)
    {
        var list = new ListBox { ItemsSource = new ObservableCollection<string>(draft.Sources), MinHeight = 100 };
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
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), RowSpacing = 8 };
        Place(body, Label("Input folders", 15, FontWeight.SemiBold), 0);
        Place(body, list, 1);
        Place(body, Actions(add, remove), 2);
        Place(body, Label("Output folder", 15, FontWeight.SemiBold), 3);
        Place(body, TwoColumns(output, browse), 4);
        SetPage("1 of 2 · Folders", body, Actions(ActionButton("Cancel", () => NavigateAsync(ShowProfilesAsync)), next));
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
            chips.Children.Add(ActionButton(token, () =>
            {
                InsertToken(pattern, token);
                return Task.CompletedTask;
            }));
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
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Label("Naming pattern", 15, FontWeight.SemiBold));
        body.Children.Add(pattern);
        body.Children.Add(chips);
        body.Children.Add(Label("Use / between folders. The file extension is preserved.", 13));
        body.Children.Add(Label("Profile name", 15, FontWeight.SemiBold));
        body.Children.Add(name);
        var back = ActionButton("Back", () => NavigateAsync(() =>
        {
            RememberInputs();
            return ShowFoldersAsync(draft);
        }));
        SetPage("2 of 2 · Pattern", body, Actions(back, save));
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
        var list = new ListBox { ItemTemplate = new FuncDataTemplate<Track>((track, _) => track is null ? null : IndexRow(track)) };
        SetName(list, "Indexed tracks");
        var status = Label("Opening local index…", 13);
        var start = Primary("Start sorting", StartSortingAsync);
        var review = ActionButton("Review", () => NavigateAsync(ShowReviewAsync));
        start.IsEnabled = review.IsEnabled = false;

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        Place(body, status, 0);
        Place(body, list, 1);
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

        var list = new ListBox { ItemTemplate = new FuncDataTemplate<ReviewRow>((row, _) => row is null ? null : PlanRow(row)) };
        SetName(list, "Proposed copies");
        var status = Label("", 13);
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
        Place(body, list, 1);
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
            var resultList = new ListBox { ItemsSource = recentRows };
            SetName(resultList, "Sort results");
            var status = Label("Preparing copies…", 13);
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
            Place(body, resultList, 2);
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

    private void SetPage(string title, Control body, Control? actions = null)
    {
        // Reparent the per-window error to the new page.
        if (_error.Parent is Grid oldPage) oldPage.Children.Remove(_error);
        var chrome = new Grid { Margin = new Thickness(24), RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 12 };
        Place(chrome, Label(title, 26, FontWeight.SemiBold), 0);
        Place(chrome, body, 1);
        Place(chrome, _error, 2);
        if (actions is not null) Place(chrome, actions, 3);
        Title = $"Music Sorter · {title}";
        Content = chrome;
    }

    private static Control IndexRow(Track track)
    {
        var title = string.IsNullOrWhiteSpace(track.Tags.Title) ? Path.GetFileName(track.Source) : track.Tags.Title;
        var artist = string.IsNullOrWhiteSpace(track.Tags.Artist) ? "" : $"{track.Tags.Artist} · ";
        return Label(artist + title);
    }

    private static Control PlanRow(ReviewRow row)
    {
        var badge = row.Item.Status switch
        {
            PlanStatus.Duplicate => "Duplicate · skip",
            PlanStatus.Variant => "Variant · keep",
            _ => "Copy",
        };
        var status = Label(badge, 12, FontWeight.SemiBold);
        var detail = Label(row.Relative + (row.PossibleMatch ? "  ·  Possible audio match" : ""));
        var rowContent = new StackPanel { Spacing = 2, Margin = new Thickness(0, 3) };
        rowContent.Children.Add(status);
        rowContent.Children.Add(detail);
        ToolTip.SetTip(rowContent, row.Item.Track.Source);
        return rowContent;
    }

    private static TextBlock Label(string value, double size = 14, FontWeight? weight = null) =>
        new() { Text = value, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };

    private static Control Actions(params Button[] items)
    {
        var panel = new WrapPanel();
        foreach (var item in items) panel.Children.Add(item);
        return panel;
    }

    private static Button ActionButton(string caption, Func<Task> onClick)
    {
        var button = new Button { Content = caption, Margin = new Thickness(0, 0, 8, 4) };
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
