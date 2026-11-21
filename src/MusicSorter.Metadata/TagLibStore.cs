using MusicSorter.Core;
using MediaFile = TagLib.File;

namespace MusicSorter.Metadata;

public sealed class TagLibStore : ITagStore
{
    public TrackTags Read(string path)
    {
        using var media = MediaFile.Create(path, TagLib.ReadStyle.None);
        var tag = media.Tag;
        return new TrackTags(First(tag.Performers), tag.Title?.Trim() ?? "", First(tag.Genres), tag.Year);
    }

    public void FillMissing(string path, TrackTags proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        using var media = MediaFile.Create(path, TagLib.ReadStyle.None);
        var tag = media.Tag;
        var changed = false;
        if (First(tag.Performers).Length == 0 && !string.IsNullOrWhiteSpace(proposed.Artist))
        {
            tag.Performers = [proposed.Artist.Trim()];
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(tag.Title) && !string.IsNullOrWhiteSpace(proposed.Title))
        {
            tag.Title = proposed.Title.Trim();
            changed = true;
        }
        if (First(tag.Genres).Length == 0 && !string.IsNullOrWhiteSpace(proposed.Genre))
        {
            tag.Genres = [proposed.Genre.Trim()];
            changed = true;
        }
        if (tag.Year == 0 && proposed.Year is > 0 and <= 9999)
        {
            tag.Year = proposed.Year;
            changed = true;
        }
        if (changed)
            media.Save();
    }

    private static string First(string[]? values) =>
        values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}
