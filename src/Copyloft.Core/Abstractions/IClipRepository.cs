using Copyloft.Core.Models;

namespace Copyloft.Core.Abstractions;

// Clipboard history storage. Re-adding text that is already stored moves it to the top.
public interface IClipRepository
{
    Task AddOrBumpAsync(string text, string? appName, DateTimeOffset copiedAtUtc, CancellationToken ct);

    // Image clips store the full PNG plus a small thumbnail; re-copying the same image bumps it.
    Task AddImageAsync(
        byte[] image, byte[] thumbnail, string? appName, DateTimeOffset copiedAtUtc, CancellationToken ct);

    // The full image for pasting back; null for text clips, missing rows, or a clip in a locked
    // category that is not in unlockedCategories.
    Task<byte[]?> GetImageAsync(long id, CancellationToken ct, IReadOnlyCollection<long>? unlockedCategories = null);

    Task<IReadOnlyList<ClipListItem>> GetRecentAsync(
        PageCursor? after, int pageSize, CancellationToken ct, long? categoryId = null,
        IReadOnlyCollection<long>? unlockedCategories = null);

    Task<IReadOnlyList<ClipListItem>> SearchAsync(
        string query, PageCursor? after, int pageSize, CancellationToken ct, long? categoryId = null,
        IReadOnlyCollection<long>? unlockedCategories = null);

    // Total rows the matching list query can show (same visibility rules), for the "N clips" footer.
    Task<int> CountAsync(
        string? query, long? categoryId, IReadOnlyCollection<long>? unlockedCategories, CancellationToken ct);

    // Row actions change nothing for a clip in a locked category that is not in unlockedCategories.
    Task SetPinnedAsync(long id, bool pinned, CancellationToken ct, IReadOnlyCollection<long>? unlockedCategories = null);

    // Moves a clip into a category, or out of every category when categoryId is null.
    Task AssignCategoryAsync(
        long clipId, long? categoryId, CancellationToken ct, IReadOnlyCollection<long>? unlockedCategories = null);

    Task DeleteAsync(long id, CancellationToken ct, IReadOnlyCollection<long>? unlockedCategories = null);

    Task ClearUnpinnedAsync(CancellationToken ct);

    // Keeps the newest unpinned clips and every pinned clip; returns how many were removed.
    Task<int> PruneAsync(int keepUnpinned, CancellationToken ct);

    // Removes unpinned clips older than the cutoff; pinned clips are kept.
    Task<int> PruneOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct);

    // Session end cannot wait for a task; this clears unpinned clips synchronously.
    int ClearUnpinned();
}
