using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

namespace MusicSorter.Metadata;

public sealed record AudioFingerprint(string Value, double DurationSeconds)
{
    // Uncompressed words are optional and strictly local; never serialized into an AcoustID request.
    public uint[]? Raw { get; init; }
}

public sealed class ChromaprintFingerprinter(string? executable = null)
{
    internal static string? FindInstalledExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("MUSIC_SORTER_FPCALC");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.IsPathFullyQualified(overridePath) && File.Exists(overridePath) ? overridePath : null;
        var name = OperatingSystem.IsWindows() ? "fpcalc.exe" : "fpcalc";
        var adjacent = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(adjacent))
            return adjacent;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var cleaned = entry.Trim().Trim('"');
            if (Path.IsPathFullyQualified(cleaned) && File.Exists(Path.Combine(cleaned, name)))
                return Path.Combine(cleaned, name);
        }
        return null;
    }

    private static string FindExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var overridePath = Environment.GetEnvironmentVariable("MUSIC_SORTER_FPCALC");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);
        return FindInstalledExecutable() ?? (OperatingSystem.IsWindows() ? "fpcalc.exe" : "fpcalc");
    }

    public async Task<AudioFingerprint> ComputeAsync(string path, CancellationToken ct = default)
    {
        var localPath = Path.GetFullPath(path);
        if (!File.Exists(localPath))
            throw new FileNotFoundException("The audio file could not be opened.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        // fpcalc returns either the encoded lookup fingerprint or the raw local comparison words.
        using var compressed = await RunAsync(localPath, raw: false, timeout.Token).ConfigureAwait(false);
        var root = compressed.RootElement;
        var value = root.GetProperty("fingerprint").GetString();
        // In non-chunk mode upstream fpcalc reports the decoder's whole-file duration,
        // not the number of seconds from which the fingerprint was derived.
        var duration = root.GetProperty("duration").GetDouble();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 65_536 ||
            !double.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException("Chromaprint returned an invalid fingerprint or duration.");
        using var uncompressed = await RunAsync(localPath, raw: true, timeout.Token).ConfigureAwait(false);
        var words = uncompressed.RootElement.GetProperty("fingerprint");
        if (words.ValueKind != JsonValueKind.Array || words.GetArrayLength() is 0 or > 2048)
            throw new InvalidDataException("Chromaprint returned invalid local comparison data.");
        return new AudioFingerprint(value, duration)
        {
            Raw = words.EnumerateArray().Select(word => word.GetUInt32()).ToArray()
        };
    }

    private async Task<JsonDocument> RunAsync(string path, bool raw, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(FindExecutable(executable))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-json");
        start.ArgumentList.Add("-length");
        start.ArgumentList.Add("120");
        if (raw)
            start.ArgumentList.Add("-raw");
        start.ArgumentList.Add(path);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Chromaprint failed to start.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("Chromaprint fpcalc is unavailable. Install a trusted fpcalc or set MUSIC_SORTER_FPCALC to its executable path.", ex);
        }

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(process.WaitForExitAsync(ct), stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0 || stdout.Result.Length > 131_072)
                throw new InvalidDataException("Chromaprint could not fingerprint this file.");
            return JsonDocument.Parse(stdout.Result);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            throw;
        }
    }
}

public static class FingerprintSimilarity
{
    // Heuristic candidate rank, not a probability or proof of identity. SHA-256 remains the deduplication key.
    public static double Score(AudioFingerprint left, AudioFingerprint right)
    {
        if (!double.IsFinite(left.DurationSeconds) || !double.IsFinite(right.DurationSeconds) ||
            left.DurationSeconds <= 0 || right.DurationSeconds <= 0 ||
            Math.Abs(left.DurationSeconds - right.DurationSeconds) >
                Math.Max(4, Math.Min(left.DurationSeconds, right.DurationSeconds) * .05))
            return 0;
        if (left.Value.Length > 0 && string.Equals(left.Value, right.Value, StringComparison.Ordinal))
            return 1;
        if (left.Raw is not { Length: >= 32 } a || right.Raw is not { Length: >= 32 } b)
            return 0;
        var best = 0.0;
        for (var offset = -16; offset <= 16; offset++)
        {
            var aStart = Math.Max(0, -offset);
            var bStart = Math.Max(0, offset);
            var count = Math.Min(a.Length - aStart, b.Length - bStart);
            if (count < 32 || count < Math.Min(a.Length, b.Length) * 0.8 ||
                count < Math.Max(a.Length, b.Length) * 0.75)
                continue;
            long differing = 0;
            for (var i = 0; i < count; i++)
                differing += BitOperations.PopCount(a[aStart + i] ^ b[bStart + i]);
            best = Math.Max(best, 1 - (double)differing / (32.0 * count));
        }
        return best;
    }
}
