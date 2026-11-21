namespace MusicSorter.Core;

public record TrackTags(string Artist = "", string Title = "", string Genre = "", uint Year = 0);

/// <summary>Metadata adapters must support concurrent reads. Export writes are sequential.</summary>
public interface ITagStore
{
    TrackTags Read(string path);
    void FillMissing(string path, TrackTags proposed);
}

public record Track(string Source, string Sha256, TrackTags Tags);

public enum PlanStatus { Ready, Variant, Duplicate }

public record PlanItem(Track Track, string? Destination, PlanStatus Status)
{
    // Retain the chosen root, which cannot otherwise be inferred from an expanded path.
    internal string? OutputRoot { get; init; }
}

public record ProfileExportResult(PlanItem Item, string Outcome, string? Message = null);
