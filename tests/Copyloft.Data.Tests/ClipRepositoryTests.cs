using Copyloft.Core.Abstractions;
using Copyloft.Core.Models;
using Copyloft.Data.Repositories;
using NSubstitute;

namespace Copyloft.Data.Tests;

public sealed class ClipRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ClipRepository New(TempDatabase db, IDataChangeNotifier? changes = null) =>
        new(db.Factory, changes ?? Substitute.For<IDataChangeNotifier>());

    [Fact]
    public async Task Added_clip_appears_in_recent()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("hello", "Notepad", T0, ct);
        var items = await repo.GetRecentAsync(null, 50, ct);

        var item = Assert.Single(items);
        Assert.Equal("hello", item.Text);
        Assert.Equal("Notepad", item.AppName);
        Assert.False(item.IsPinned);
        Assert.Equal(T0, item.CreatedAtUtc);
    }

    [Fact]
    public async Task Re_copying_an_existing_clip_moves_it_to_the_top_without_duplicating()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("first", null, T0, ct);
        await repo.AddOrBumpAsync("second", null, T0.AddMinutes(1), ct);
        await repo.AddOrBumpAsync("first", "Notepad", T0.AddMinutes(2), ct);

        var items = await repo.GetRecentAsync(null, 50, ct);

        Assert.Equal(2, items.Count);
        Assert.Equal("first", items[0].Text);
        Assert.Equal(T0.AddMinutes(2), items[0].CreatedAtUtc);
        Assert.Equal("Notepad", items[0].AppName);
    }

    [Fact]
    public async Task Recent_pages_with_a_cursor()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("a", null, T0, ct);
        await repo.AddOrBumpAsync("b", null, T0.AddMinutes(1), ct);
        await repo.AddOrBumpAsync("c", null, T0.AddMinutes(2), ct);

        var page1 = await repo.GetRecentAsync(null, 2, ct);
        var page2 = await repo.GetRecentAsync(page1[^1].Cursor, 2, ct);

        Assert.Equal("c,b", string.Join(',', page1.Select(i => i.Text)));
        Assert.Equal("a", string.Join(',', page2.Select(i => i.Text)));
    }

    [Fact]
    public async Task Search_matches_a_substring_ignoring_case()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("Hello World", null, T0, ct);
        await repo.AddOrBumpAsync("Goodbye", null, T0.AddMinutes(1), ct);

        var items = await repo.SearchAsync("hello", null, 50, ct);

        Assert.Equal("Hello World", Assert.Single(items).Text);
    }

    [Fact]
    public async Task Search_treats_wildcards_as_literal_text()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("100% sure", null, T0, ct);
        await repo.AddOrBumpAsync("100x sure", null, T0.AddMinutes(1), ct);

        var items = await repo.SearchAsync("0%", null, 50, ct);

        Assert.Equal("100% sure", Assert.Single(items).Text);
    }

    [Fact]
    public async Task SetPinned_toggles_the_flag()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("x", null, T0, ct);
        var id = (await repo.GetRecentAsync(null, 50, ct))[0].Id;

        await repo.SetPinnedAsync(id, true, ct);
        Assert.True((await repo.GetRecentAsync(null, 50, ct))[0].IsPinned);

        await repo.SetPinnedAsync(id, false, ct);
        Assert.False((await repo.GetRecentAsync(null, 50, ct))[0].IsPinned);
    }

    [Fact]
    public async Task Delete_removes_the_clip()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("x", null, T0, ct);
        var id = (await repo.GetRecentAsync(null, 50, ct))[0].Id;

        await repo.DeleteAsync(id, ct);

        Assert.Empty(await repo.GetRecentAsync(null, 50, ct));
    }

    [Fact]
    public async Task Clear_unpinned_keeps_pinned_items()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("drop", null, T0, ct);
        await repo.AddOrBumpAsync("keep", null, T0.AddMinutes(1), ct);
        var keepId = (await repo.GetRecentAsync(null, 50, ct)).Single(i => i.Text == "keep").Id;
        await repo.SetPinnedAsync(keepId, true, ct);

        await repo.ClearUnpinnedAsync(ct);

        Assert.Equal("keep", Assert.Single(await repo.GetRecentAsync(null, 50, ct)).Text);
    }

    [Fact]
    public async Task Prune_keeps_the_newest_unpinned_and_all_pinned()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("old", null, T0, ct);
        await repo.AddOrBumpAsync("mid", null, T0.AddMinutes(1), ct);
        await repo.AddOrBumpAsync("new", null, T0.AddMinutes(2), ct);
        var oldId = (await repo.GetRecentAsync(null, 50, ct)).Single(i => i.Text == "old").Id;
        await repo.SetPinnedAsync(oldId, true, ct);

        var removed = await repo.PruneAsync(keepUnpinned: 1, ct);

        Assert.Equal(1, removed);
        Assert.Equal("new,old", string.Join(',', (await repo.GetRecentAsync(null, 50, ct)).Select(i => i.Text)));
    }

    [Fact]
    public async Task Prune_removes_a_large_batch_in_one_pass()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 600; i++)
            await repo.AddOrBumpAsync($"clip {i}", null, T0.AddSeconds(i), ct);

        var removed = await repo.PruneAsync(keepUnpinned: 100, ct);

        Assert.Equal(500, removed);
        var items = await repo.GetRecentAsync(null, 1000, ct);
        Assert.Equal(100, items.Count);
        Assert.Equal("clip 599", items[0].Text);
        Assert.Equal("clip 500", items[^1].Text);
    }

    [Fact]
    public async Task Prune_older_than_removes_old_unpinned_clips()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("old", null, T0, ct);
        await repo.AddOrBumpAsync("new", null, T0.AddDays(10), ct);

        var removed = await repo.PruneOlderThanAsync(T0.AddDays(5), ct);

        Assert.Equal(1, removed);
        Assert.Equal("new", Assert.Single(await repo.GetRecentAsync(null, 50, ct)).Text);
    }

    [Fact]
    public async Task Prune_older_than_keeps_pinned_clips()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("old", null, T0, ct);
        await repo.AddOrBumpAsync("new", null, T0.AddDays(10), ct);
        var oldId = (await repo.GetRecentAsync(null, 50, ct)).Single(i => i.Text == "old").Id;
        await repo.SetPinnedAsync(oldId, true, ct);

        var removed = await repo.PruneOlderThanAsync(T0.AddDays(5), ct);

        Assert.Equal(0, removed);
        Assert.Equal(2, (await repo.GetRecentAsync(null, 50, ct)).Count);
    }

    [Fact]
    public async Task Clear_unpinned_now_removes_rows_synchronously()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("drop", null, T0, ct);
        await repo.AddOrBumpAsync("keep", null, T0.AddMinutes(1), ct);
        var keepId = (await repo.GetRecentAsync(null, 50, ct)).Single(i => i.Text == "keep").Id;
        await repo.SetPinnedAsync(keepId, true, ct);

        var removed = repo.ClearUnpinned();

        Assert.Equal(1, removed);
        Assert.Equal("keep", Assert.Single(await repo.GetRecentAsync(null, 50, ct)).Text);
    }

    [Fact]
    public async Task Assigning_a_category_shows_it_on_the_clip()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var categories = new CategoryRepository(db.Factory, Substitute.For<IDataChangeNotifier>());
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("keep", null, T0, ct);
        var id = (await repo.GetRecentAsync(null, 50, ct))[0].Id;
        var category = await categories.GetOrAddAsync("Work", ct);

        await repo.AssignCategoryAsync(id, category.Id, ct);

        var item = Assert.Single(await repo.GetRecentAsync(null, 50, ct));
        Assert.Equal(category.Id, item.CategoryId);
        Assert.Equal("Work", item.CategoryName);
    }

    [Fact]
    public async Task Leaving_a_category_hides_it_from_the_clip()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var categories = new CategoryRepository(db.Factory, Substitute.For<IDataChangeNotifier>());
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("x", null, T0, ct);
        var id = (await repo.GetRecentAsync(null, 50, ct))[0].Id;
        await repo.AssignCategoryAsync(id, (await categories.GetOrAddAsync("Work", ct)).Id, ct);

        await repo.AssignCategoryAsync(id, null, ct);

        var item = Assert.Single(await repo.GetRecentAsync(null, 50, ct));
        Assert.Null(item.CategoryId);
        Assert.Null(item.CategoryName);
    }

    [Fact]
    public async Task Clear_unpinned_keeps_categorized_items()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var categories = new CategoryRepository(db.Factory, Substitute.For<IDataChangeNotifier>());
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("drop", null, T0, ct);
        await repo.AddOrBumpAsync("keep", null, T0.AddMinutes(1), ct);
        var keep = (await repo.GetRecentAsync(null, 50, ct)).Single(i => i.Text == "keep");
        await repo.AssignCategoryAsync(keep.Id, (await categories.GetOrAddAsync("Work", ct)).Id, ct);

        await repo.ClearUnpinnedAsync(ct);

        Assert.Equal("keep", Assert.Single(await repo.GetRecentAsync(null, 50, ct)).Text);
    }

    [Fact]
    public async Task Prune_keeps_categorized_items()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var categories = new CategoryRepository(db.Factory, Substitute.For<IDataChangeNotifier>());
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("saved", null, T0, ct);
        await repo.AddOrBumpAsync("mid", null, T0.AddMinutes(1), ct);
        await repo.AddOrBumpAsync("new", null, T0.AddMinutes(2), ct);
        var saved = (await repo.GetRecentAsync(null, 50, ct)).Single(i => i.Text == "saved");
        await repo.AssignCategoryAsync(saved.Id, (await categories.GetOrAddAsync("Work", ct)).Id, ct);

        var removed = await repo.PruneAsync(keepUnpinned: 1, ct);

        Assert.Equal(1, removed);
        Assert.Equal(
            "new,saved", string.Join(',', (await repo.GetRecentAsync(null, 50, ct)).Select(i => i.Text)));
    }

    [Fact]
    public async Task Prune_older_than_keeps_categorized_items()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var categories = new CategoryRepository(db.Factory, Substitute.For<IDataChangeNotifier>());
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("old-plain", null, T0, ct);
        await repo.AddOrBumpAsync("old-saved", null, T0.AddMinutes(1), ct);
        await repo.AddOrBumpAsync("new", null, T0.AddDays(10), ct);
        var saved = (await repo.GetRecentAsync(null, 50, ct)).Single(i => i.Text == "old-saved");
        await repo.AssignCategoryAsync(saved.Id, (await categories.GetOrAddAsync("Work", ct)).Id, ct);

        var removed = await repo.PruneOlderThanAsync(T0.AddDays(5), ct);

        Assert.Equal(1, removed);
        Assert.Equal(
            "new,old-saved", string.Join(',', (await repo.GetRecentAsync(null, 50, ct)).Select(i => i.Text)));
    }

    [Fact]
    public async Task Recent_can_be_narrowed_to_one_category()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var categories = new CategoryRepository(db.Factory, Substitute.For<IDataChangeNotifier>());
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("plain", null, T0, ct);
        await repo.AddOrBumpAsync("saved", null, T0.AddMinutes(1), ct);
        var saved = (await repo.GetRecentAsync(null, 50, ct)).Single(i => i.Text == "saved");
        var work = await categories.GetOrAddAsync("Work", ct);
        await repo.AssignCategoryAsync(saved.Id, work.Id, ct);

        var items = await repo.GetRecentAsync(null, 50, ct, work.Id);

        Assert.Equal("saved", Assert.Single(items).Text);
    }

    [Fact]
    public async Task Search_can_be_narrowed_to_one_category()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var categories = new CategoryRepository(db.Factory, Substitute.For<IDataChangeNotifier>());
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("apple plain", null, T0, ct);
        await repo.AddOrBumpAsync("apple saved", null, T0.AddMinutes(1), ct);
        var saved = (await repo.GetRecentAsync(null, 50, ct)).Single(i => i.Text == "apple saved");
        var work = await categories.GetOrAddAsync("Work", ct);
        await repo.AssignCategoryAsync(saved.Id, work.Id, ct);

        var items = await repo.SearchAsync("apple", null, 50, ct, work.Id);

        Assert.Equal("apple saved", Assert.Single(items).Text);
    }

    [Fact]
    public async Task Locked_category_clips_stay_hidden_until_unlocked()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var categories = new CategoryRepository(db.Factory, Substitute.For<IDataChangeNotifier>());
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("secret", null, T0, ct);
        var id = (await repo.GetRecentAsync(null, 50, ct))[0].Id;
        var work = await categories.GetOrAddAsync("Work", ct);
        await repo.AssignCategoryAsync(id, work.Id, ct);
        await categories.SetLockedAsync(work.Id, true, ct);

        Assert.Empty(await repo.GetRecentAsync(null, 50, ct));

        var visible = await repo.GetRecentAsync(null, 50, ct, null, new long[] { work.Id });

        Assert.Equal("secret", Assert.Single(visible).Text);
    }

    [Fact]
    public async Task Search_skips_locked_category_clips()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var categories = new CategoryRepository(db.Factory, Substitute.For<IDataChangeNotifier>());
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("secret note", null, T0, ct);
        var id = (await repo.GetRecentAsync(null, 50, ct))[0].Id;
        var work = await categories.GetOrAddAsync("Work", ct);
        await repo.AssignCategoryAsync(id, work.Id, ct);
        await categories.SetLockedAsync(work.Id, true, ct);

        Assert.Empty(await repo.SearchAsync("secret", null, 50, ct));

        var visible = await repo.SearchAsync("secret", null, 50, ct, null, new long[] { work.Id });

        Assert.Equal("secret note", Assert.Single(visible).Text);
    }

    [Fact]
    public async Task Count_matches_visible_clips_and_skips_locked_categories()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var categories = new CategoryRepository(db.Factory, Substitute.For<IDataChangeNotifier>());
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("plain", null, T0, ct);
        await repo.AddOrBumpAsync("saved", null, T0.AddMinutes(1), ct);
        await repo.AddOrBumpAsync("secret", null, T0.AddMinutes(2), ct);
        var items = await repo.GetRecentAsync(null, 50, ct);
        var work = await categories.GetOrAddAsync("Work", ct);
        var secret = await categories.GetOrAddAsync("Secret", ct);
        await repo.AssignCategoryAsync(items.Single(i => i.Text == "saved").Id, work.Id, ct);
        await repo.AssignCategoryAsync(items.Single(i => i.Text == "secret").Id, secret.Id, ct);
        await categories.SetLockedAsync(secret.Id, true, ct);

        Assert.Equal(2, await repo.CountAsync(null, null, null, ct));
        Assert.Equal(3, await repo.CountAsync(null, null, new long[] { secret.Id }, ct));
        Assert.Equal(1, await repo.CountAsync(null, work.Id, null, ct));
    }

    [Fact]
    public async Task Row_actions_skip_locked_clips()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var categories = new CategoryRepository(db.Factory, Substitute.For<IDataChangeNotifier>());
        var ct = TestContext.Current.CancellationToken;

        await repo.AddImageAsync([1, 2, 3], [9], null, T0, ct);
        var id = Assert.Single(await repo.GetRecentAsync(null, 50, ct)).Id;
        var secret = await categories.GetOrAddAsync("Secret", ct);
        await repo.AssignCategoryAsync(id, secret.Id, ct);
        await categories.SetLockedAsync(secret.Id, true, ct);

        // A row loaded before the relock still carries this id; nothing may reach the clip.
        Assert.Null(await repo.GetImageAsync(id, ct));
        await repo.AssignCategoryAsync(id, null, ct);
        await repo.SetPinnedAsync(id, true, ct);
        await repo.DeleteAsync(id, ct);

        var unlocked = new long[] { secret.Id };
        var item = Assert.Single(await repo.GetRecentAsync(null, 50, ct, unlockedCategories: unlocked));
        Assert.Equal(secret.Id, item.CategoryId);
        Assert.False(item.IsPinned);
        Assert.NotNull(await repo.GetImageAsync(id, ct, unlocked));

        // Once unlocked, the same actions apply.
        await repo.SetPinnedAsync(id, true, ct, unlocked);
        Assert.True(Assert.Single(await repo.GetRecentAsync(null, 50, ct, unlockedCategories: unlocked)).IsPinned);
        await repo.DeleteAsync(id, ct, unlocked);
        Assert.Empty(await repo.GetRecentAsync(null, 50, ct, unlockedCategories: unlocked));
    }

    [Fact]
    public async Task Count_matches_search_results()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("apple one", null, T0, ct);
        await repo.AddOrBumpAsync("apple two", null, T0.AddMinutes(1), ct);
        await repo.AddOrBumpAsync("pear", null, T0.AddMinutes(2), ct);

        Assert.Equal(2, await repo.CountAsync("apple", null, null, ct));
        Assert.Equal(0, await repo.CountAsync("zzz", null, null, ct));
    }

    [Fact]
    public async Task An_image_clip_round_trips_with_its_thumbnail()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;
        byte[] image = [1, 2, 3, 4, 5];
        byte[] thumbnail = [9, 9];

        await repo.AddImageAsync(image, thumbnail, "Snipping Tool", T0, ct);

        var item = Assert.Single(await repo.GetRecentAsync(null, 50, ct));
        Assert.True(item.HasImage);
        Assert.Equal(thumbnail, item.ThumbnailBytes);
        Assert.Equal(string.Empty, item.Text);
        Assert.Equal<byte[]?>(image, await repo.GetImageAsync(item.Id, ct));
    }

    [Fact]
    public async Task A_text_clip_has_no_image()
    {
        using var db = new TempDatabase();
        var repo = New(db);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("hello", null, T0, ct);
        var item = Assert.Single(await repo.GetRecentAsync(null, 50, ct));

        Assert.False(item.HasImage);
        Assert.Null(await repo.GetImageAsync(item.Id, ct));
    }

    [Fact]
    public async Task Writes_notify_open_views()
    {
        using var db = new TempDatabase();
        var changes = Substitute.For<IDataChangeNotifier>();
        var repo = New(db, changes);
        var ct = TestContext.Current.CancellationToken;

        await repo.AddOrBumpAsync("x", null, T0, ct);

        changes.Received(1).Notify(Arg.Is<DataChanged>(c => c.Area == "clip"));
    }
}
