module MyDogsbody.Tests.Database.SqliteConnectionPoolingTests

open System
open System.IO
open Xunit

/// Walks up from the test assembly to the repo root, the same way
/// Contracts/DomainIsolationTests.fs and SuppliersBrowserModuleCreatorsTests.fs do.
let private repositoryRoot () =
    let rec find (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "Could not locate MyDogsbody.sln above the test assembly."
        elif File.Exists(Path.Combine(directory.FullName, "MyDogsbody.sln")) then
            directory.FullName
        else
            find directory.Parent

    find (DirectoryInfo(AppContext.BaseDirectory))

let private testsRoot () = Path.Combine(repositoryRoot (), "MyDogsbody.Tests")

/// Every hand-written `.fs` under MyDogsbody.Tests: build output is skipped by matching whole path
/// segments (a `Contains "bin\\"` misses a file sitting directly in a folder named `bin`), and this
/// file is skipped because it is the checker - it necessarily spells out the patterns it forbids.
let private testSourceFiles () =
    let root = testsRoot ()

    Directory.EnumerateFiles(root, "*.fs", SearchOption.AllDirectories)
    |> Seq.filter (fun path ->
        let segments = Path.GetRelativePath(root, path).Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |])

        not (segments |> Array.exists (fun segment ->
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase))))
    |> Seq.filter (fun path -> Path.GetFileName path <> "SqliteConnectionPoolingTests.fs")

/// `SqliteConnection.ClearAllPools()` is process-global: called in a test harness's teardown it
/// disposes pooled connections that other collections, running in parallel, are mid-command on,
/// which surfaces as an `ObjectDisposedException: 'SQLitePCL.sqlite3'` on whichever test was
/// unlucky. The fix (docs/changes/sqlite-pool-flake) is `Pooling=False` on every SQLite connection
/// string the tests build, so a dispose releases the file handle with nothing to clear. This test
/// fails if a new harness reintroduces the global call.
[<Fact; Trait("Level", "Unit")>]
let ``no test source file calls SqliteConnection.ClearAllPools`` () =
    let needle = "SqliteConnection.ClearAllPools"

    let offenders =
        testSourceFiles ()
        |> Seq.filter (fun path -> (File.ReadAllText path).Contains needle)
        |> Seq.map (fun path -> Path.GetRelativePath(testsRoot (), path))
        |> Seq.toList

    Assert.True(
        List.isEmpty offenders,
        $"""these test files still call the process-global SqliteConnection.ClearAllPools() - use a Pooling=False connection string instead: {String.Join(", ", offenders)}"""
    )

/// The other half of the same rule, and the half that actually has to hold going forward.
/// Dropping `ClearAllPools()` only stops harnesses trampling each other; it is `;Pooling=False` on
/// every connection string a test builds that makes the temp file deletable in the first place -
/// without it, Microsoft.Data.Sqlite keeps the handle in a pool after `Dispose()` and the harness
/// silently leaks a GUID-named file into %TEMP% on every Windows run. CLAUDE-project.md ->
/// *Testing in this codebase* -> *Integration* states the rule; this is what enforces it.
///
/// Deliberately line-based: every connection string in the suite is a single interpolated literal,
/// so the keyword belongs on the same line as `Data Source=`.
[<Fact; Trait("Level", "Unit")>]
let ``every SQLite connection string a test builds disables pooling`` () =
    let offenders =
        testSourceFiles ()
        |> Seq.collect (fun path ->
            File.ReadAllLines path
            |> Array.indexed
            |> Array.filter (fun (_, line) -> line.Contains "Data Source=" && not (line.Contains "Pooling=False"))
            |> Array.map (fun (index, line) ->
                $"{Path.GetRelativePath(testsRoot (), path)}:{index + 1}: {line.Trim()}"))
        |> Seq.toList

    Assert.True(
        List.isEmpty offenders,
        $"""these SQLite connection strings do not disable pooling - add ;Pooling=False so the temp file's handle is released on dispose (docs/changes/sqlite-pool-flake):{Environment.NewLine}{String.Join(Environment.NewLine, offenders)}"""
    )
