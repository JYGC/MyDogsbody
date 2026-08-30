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

/// `SqliteConnection.ClearAllPools()` is process-global: called in a test harness's teardown it
/// disposes pooled connections that other collections, running in parallel, are mid-command on,
/// which surfaces as an `ObjectDisposedException: 'SQLitePCL.sqlite3'` on whichever test was
/// unlucky. The fix (docs/changes/sqlite-pool-flake) is `Pooling=False` on every SQLite connection
/// string the tests build, so a dispose releases the file handle with nothing to clear. This test
/// fails if a new harness reintroduces the global call.
[<Fact; Trait("Level", "Unit")>]
let ``no test source file calls SqliteConnection.ClearAllPools`` () =
    let testsRoot = Path.Combine(repositoryRoot (), "MyDogsbody.Tests")

    let needle = "SqliteConnection.ClearAllPools"

    let offenders =
        Directory.EnumerateFiles(testsRoot, "*.fs", SearchOption.AllDirectories)
        |> Seq.filter (fun path ->
            let dir = Path.GetDirectoryName path
            not (dir.Contains(Path.Combine("bin", "")) || dir.Contains(Path.Combine("obj", ""))))
        // this file is the checker - it names the call it forbids
        |> Seq.filter (fun path -> Path.GetFileName path <> "SqliteConnectionPoolingTests.fs")
        |> Seq.filter (fun path -> (File.ReadAllText path).Contains needle)
        |> Seq.map (fun path -> Path.GetRelativePath(testsRoot, path))
        |> Seq.toList

    Assert.True(
        List.isEmpty offenders,
        $"""these test files still call the process-global SqliteConnection.ClearAllPools() - use a Pooling=False connection string instead: {String.Join(", ", offenders)}"""
    )
