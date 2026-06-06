using System.IO;
using System.Runtime.CompilerServices;
using FujinTerm.Services;

namespace FujinTerm.Tests;

/// <summary>
/// One-shot sweep that runs before any test in this assembly fires.
/// </summary>
/// <remarks>
/// <para>
/// Several test classes have to operate against <see cref="AppPaths.GameDataRoot"/>
/// + <see cref="AppPaths"/>'s per-BBS folders because the AppPaths
/// roots are cached at static-init and can't be swapped per test.
/// Each fixture uses a per-test GUID suffix and cleans up in
/// <c>Dispose</c>, but a crash (or a debugger session killed mid-test)
/// leaves the directory behind.
/// </para>
/// <para>
/// The user's real Data tree then accumulates <c>test-set-*</c>,
/// <c>test-lairtimer-*</c>, <c>test-laireditor-*</c>, etc. folders
/// over time. This <see cref="ModuleInitializer"/> wipes any
/// <c>test-*</c> directories under the active GameDataRoot + BBS
/// root before tests start, so every run begins from a clean slate
/// regardless of what prior runs left behind.
/// </para>
/// <para>
/// Best-effort — IO exceptions are swallowed so test execution
/// never blocks on environmental issues. Production
/// <c>game data/</c> subfolders (real MDB exports) don't carry the
/// <c>test-</c> prefix and are never touched.
/// </para>
/// </remarks>
internal static class TestSessionCleanup
{
    [ModuleInitializer]
    public static void Sweep()
    {
        // Run at session start so leftovers from a prior crashed
        // run are gone before tests start, AND at session end so a
        // best-effort cleanup catches Dispose-skipped fixtures
        // (tests that create per-fixture set folders with their own
        // GUIDs and rely on per-test Dispose). Each pass is
        // idempotent.
        SweepNow();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SweepNow();
    }

    private static void SweepNow()
    {
        SweepFolder(AppPaths.GameDataRoot);
        try
        {
            string bbsParent = Path.GetDirectoryName(AppPaths.BbsFolder("dummy"))!;
            SweepFolder(bbsParent);
        }
        catch { /* best-effort */ }
    }

    private static void SweepFolder(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (string dir in Directory.EnumerateDirectories(root, "test-*"))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* best-effort — leaked folders that won't delete now will get cleaned next run */ }
            }
        }
        catch { /* best-effort */ }
    }
}
