using System.Text;
using Copyloft.Core.Abstractions;
using Copyloft.Core.Export;
using Copyloft.Core.Models;

namespace Copyloft.Presentation.Services;

// Reads the history in cursor pages and writes it out as one JSON or CSV file.
public sealed class ClipExportService(IClipRepository repository, ICategoryLockService locks, int pageSize = 200)
    : IClipExportService
{
    public async Task<int> ExportAsync(string path, ClipExportFormat format, CancellationToken ct)
    {
        var unlocked = locks.UnlockedCategoryIds.Count == 0 ? null : locks.UnlockedCategoryIds.ToArray();
        var rows = new List<ClipExportRow>();
        PageCursor? after = null;
        while (true)
        {
            var page = await repository.GetRecentAsync(after, pageSize, ct, null, unlocked);
            rows.AddRange(page.Select(item =>
                new ClipExportRow(item.Text, item.AppName, item.IsPinned, item.CreatedAtUtc, item.CategoryName)));

            if (page.Count < pageSize) break;

            // A page that does not move would loop forever; stop instead.
            var next = page[^1].Cursor;
            if (next == after) break;
            after = next;
        }

        var text = ClipExportSerializer.Serialize(rows, format);
        await File.WriteAllTextAsync(path, text, EncodingFor(format), ct);
        return rows.Count;
    }

    // A UTF-8 BOM makes Excel read non-ASCII CSV text correctly; JSON stays clean without it.
    private static Encoding EncodingFor(ClipExportFormat format) =>
        format == ClipExportFormat.Csv
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
