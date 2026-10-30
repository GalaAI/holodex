using Omniwand;
using Xunit;

namespace Holodex.Tests;

/// <summary>
/// A judge must never be left without a pointer or with a changed mouse speed: a killed run leaves markers the next
/// start undoes, and an error Holodex survives brings the pointer back at once. Uses temp markers and fake restore
/// actions, so the test machine's own cursors and speed are never touched.
/// </summary>
public class CrashGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "holodex-crashguard-" + Guid.NewGuid().ToString("N"));
    private readonly CrashMarker _oldCursors = CursorCrashGuard.CursorsHidden;
    private readonly CrashMarker _oldSpeed = CursorCrashGuard.OriginalMouseSpeed;
    private readonly string _oldLog = CursorCrashGuard.LogPath;

    public CrashGuardTests()
    {
        Directory.CreateDirectory(_dir);
        CursorCrashGuard.CursorsHidden = new CrashMarker(Path.Combine(_dir, "cursors-hidden.marker"));
        CursorCrashGuard.OriginalMouseSpeed = new CrashMarker(Path.Combine(_dir, "mouse-speed-original.marker"));
        CursorCrashGuard.LogPath = Path.Combine(_dir, "errors.log");
    }

    public void Dispose()
    {
        CursorCrashGuard.CancelPendingRehide();
        CursorCrashGuard.CursorsHidden = _oldCursors;
        CursorCrashGuard.OriginalMouseSpeed = _oldSpeed;
        CursorCrashGuard.LogPath = _oldLog;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void A_killed_run_gets_its_cursors_and_speed_back_at_the_next_start()
    {
        // What a run writes while it has the cursors hidden and the speed changed, then is killed.
        CursorCrashGuard.CursorsHidden.Set("4242");
        CursorCrashGuard.OriginalMouseSpeed.Set("7");

        int cursorRestores = 0;
        var speeds = new List<int>();
        string restored = CursorCrashGuard.RecoverAtStartup(() => cursorRestores++, speeds.Add);

        Assert.Equal(1, cursorRestores);
        Assert.Equal(new[] { 7 }, speeds);
        Assert.Equal("cursors, mouse speed 7", restored);
        Assert.False(CursorCrashGuard.CursorsHidden.Exists);
        Assert.False(CursorCrashGuard.OriginalMouseSpeed.Exists);
        Assert.Contains("recovered after an unclean exit", File.ReadAllText(CursorCrashGuard.LogPath));

        // The start after that is clean: nothing is restored twice.
        Assert.Equal("", CursorCrashGuard.RecoverAtStartup(() => cursorRestores++, speeds.Add));
        Assert.Equal(1, cursorRestores);
    }

    [Fact]
    public void A_clean_exit_leaves_nothing_to_recover()
    {
        bool called = false;
        Assert.Equal("", CursorCrashGuard.RecoverAtStartup(() => called = true, _ => called = true));
        Assert.False(called);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fast")]
    [InlineData("0")]
    [InlineData("21")]
    public void A_bad_speed_marker_is_dropped_without_setting_a_speed(string raw)
    {
        CursorCrashGuard.OriginalMouseSpeed.Set(raw);
        bool set = false;
        CursorCrashGuard.RecoverAtStartup(() => { }, _ => set = true);
        Assert.False(set);
        Assert.False(CursorCrashGuard.OriginalMouseSpeed.Exists);
    }

    [Fact]
    public async Task A_survived_error_shows_the_pointer_at_once_and_hides_it_again_when_quiet()
    {
        int restores = 0, rehides = 0;
        CursorCrashGuard.OnSurvivedError(new InvalidOperationException("overlay"), cursorsWereHidden: true,
            () => restores++, () => Interlocked.Increment(ref rehides), settleMs: 80);
        Assert.Equal(1, restores);
        Assert.Equal(0, Volatile.Read(ref rehides));
        await WaitFor(() => Volatile.Read(ref rehides) == 1);
        Assert.Contains("survived error", File.ReadAllText(CursorCrashGuard.LogPath));
    }

    [Fact]
    public async Task Errors_that_keep_coming_keep_the_stock_pointer()
    {
        int rehides = 0;
        // The window is long and the second failure comes at once, so a busy machine cannot stretch the gap past it.
        CursorCrashGuard.OnSurvivedError(null, true, () => { }, () => Interlocked.Increment(ref rehides), settleMs: 1500);
        // A second failure inside the window replaces the first: only the last one may re-hide.
        CursorCrashGuard.OnSurvivedError(null, true, () => { }, () => Interlocked.Increment(ref rehides), settleMs: 1500);
        await WaitFor(() => Volatile.Read(ref rehides) >= 1);
        await Task.Delay(1800);
        Assert.Equal(1, Volatile.Read(ref rehides));
    }

    [Fact]
    public async Task No_rehide_when_the_pointer_was_already_shown_or_the_user_chose_it()
    {
        int rehides = 0;
        CursorCrashGuard.OnSurvivedError(null, cursorsWereHidden: false, () => { }, () => Interlocked.Increment(ref rehides), settleMs: 30);
        CursorCrashGuard.OnSurvivedError(null, cursorsWereHidden: true, () => { }, () => Interlocked.Increment(ref rehides), settleMs: 60);
        CursorCrashGuard.CancelPendingRehide();
        await Task.Delay(250);
        Assert.Equal(0, Volatile.Read(ref rehides));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }
}
