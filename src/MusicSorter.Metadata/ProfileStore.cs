using System.Text.Json;

namespace MusicSorter.Metadata;

public sealed record SortProfile(Guid Id, string Name, string[] Sources, string Output, string Template);

public static class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static async Task<IReadOnlyList<SortProfile>> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Locate();
        if (!Directory.Exists(directory))
            return [];
        var profiles = new List<SortProfile>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
                continue;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    continue;
                await using var stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Open, Access = FileAccess.Read, Options = FileOptions.Asynchronous,
                    Share = FileShare.Read | FileShare.Delete
                });
                var profile = await JsonSerializer.DeserializeAsync<SortProfile>(stream, Json, ct).ConfigureAwait(false);
                if (profile?.Id == id)
                    profiles.Add(Normalize(profile));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or FileNotFoundException)
            {
                // One invalid or concurrently removed file must not hide the other profiles.
            }
        }

        return profiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(profile => profile.Id).ToArray();
    }

    public static async Task SaveAsync(SortProfile profile, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = Normalize(profile);
        var directory = Locate();
        Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, $"{snapshot.Id:N}.json");
        var temp = Path.Combine(directory, $".{snapshot.Id:N}.{Guid.NewGuid():N}.tmp");
        var ownTemp = false;
        try
        {
            var options = new FileStreamOptions
                { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
            {
                ownTemp = true;
                await JsonSerializer.SerializeAsync(stream, snapshot, Json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, final, overwrite: true);
            ownTemp = false;
        }
        finally
        {
            if (ownTemp)
                File.Delete(temp);
        }
    }

    internal static SortProfile Normalize(SortProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) ||
            profile.Sources is not { Length: > 0 } || string.IsNullOrWhiteSpace(profile.Template))
            throw new ArgumentException("A profile needs an ID, name, source folders, and a path template.", nameof(profile));
        var sources = profile.Sources.Select(FullPath).Distinct(PathComparer).ToArray();
        return profile with { Name = profile.Name.Trim(), Sources = sources, Output = FullPath(profile.Output) };
    }

    internal static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Choose an absolute folder path.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static string Locate()
    {
        var configured = Environment.GetEnvironmentVariable("MUSIC_SORTER_PROFILE_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
            return FullPath(configured);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new InvalidOperationException("Local application data is unavailable.");
        return Path.Combine(local, "MusicSorter", "Profiles");
    }
}
