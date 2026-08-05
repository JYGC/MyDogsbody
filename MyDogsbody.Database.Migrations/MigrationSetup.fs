module MyDogsbody.Database.Migrations.MigrationSetup

open System
open Microsoft.Extensions.DependencyInjection
open FluentMigrator.Runner

/// Builds a runner over every migration in this assembly, against the given connection.
///
/// The service provider owns the runner, so callers dispose it - otherwise the SQLite connection
/// it holds keeps a temp file locked on Windows.
let private buildRunner (connectionString: string) =
    let serviceProvider =
        ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(fun rb ->
                rb.AddSQLite()
                  .WithGlobalConnectionString(connectionString)
                  .ScanIn(typeof<Migrations.CreateBlogTable>.Assembly).For.Migrations()
                |> ignore
            )
            .AddLogging(fun lb -> lb.AddFluentMigratorConsole() |> ignore)
            .BuildServiceProvider(false)

    serviceProvider, serviceProvider.GetRequiredService<IMigrationRunner>()

/// Applies every migration that has not yet been applied.
let setupMigrations (connectionString: string) =
    let serviceProvider, runner = buildRunner connectionString

    try
        runner.MigrateUp()
    finally
        (serviceProvider :> IDisposable).Dispose()

/// Walks every Down() back, leaving the database as it was before the first migration.
///
/// Nothing in the application calls this - it exists so the Down() methods are exercised by the
/// migration tests. A migration whose Down() is wrong is otherwise only discovered by someone
/// running `dotnet fm rollback` against real data.
let rollbackAll (connectionString: string) =
    let serviceProvider, runner = buildRunner connectionString

    try
        runner.RollbackToVersion 0L
    finally
        (serviceProvider :> IDisposable).Dispose()
