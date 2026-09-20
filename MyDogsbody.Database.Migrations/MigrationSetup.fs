module MyDogsbody.Database.Migrations.MigrationSetup

open System
open Microsoft.Extensions.DependencyInjection
open FluentMigrator.Runner

/// Callers dispose the service provider - otherwise the SQLite connection the runner holds keeps a
/// temp file locked on Windows.
let private buildRunnerOverEveryMigrationInThisAssemblyReturningTheServiceProviderThatOwnsIt (connectionString: string) =
    let serviceProvider =
        ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(fun runnerBuilder ->
                runnerBuilder.AddSQLite()
                  .WithGlobalConnectionString(connectionString)
                  .ScanIn(typeof<Migrations.CreateBlogTable>.Assembly).For.Migrations()
                |> ignore
            )
            .AddLogging(fun loggingBuilder -> loggingBuilder.AddFluentMigratorConsole() |> ignore)
            .BuildServiceProvider(false)

    serviceProvider, serviceProvider.GetRequiredService<IMigrationRunner>()

/// Applies every migration that has not yet been applied.
let setupMigrations (connectionString: string) =
    let serviceProvider, runner = buildRunnerOverEveryMigrationInThisAssemblyReturningTheServiceProviderThatOwnsIt connectionString

    try
        runner.MigrateUp()
    finally
        (serviceProvider :> IDisposable).Dispose()

/// Nothing in the application calls this - it exists so the Down() methods are exercised by the
/// migration tests. A migration whose Down() is wrong is otherwise only discovered by someone
/// running `dotnet fm rollback` against real data.
let rollbackAll (connectionString: string) =
    let serviceProvider, runner = buildRunnerOverEveryMigrationInThisAssemblyReturningTheServiceProviderThatOwnsIt connectionString

    try
        runner.RollbackToVersion 0L
    finally
        (serviceProvider :> IDisposable).Dispose()

/// So a test can exercise one migration's Down() without dropping every table before it. Also
/// test-only.
let rollbackToVersion (connectionString: string) (lastMigrationVersionThatStaysApplied: int64) =
    let serviceProvider, runner = buildRunnerOverEveryMigrationInThisAssemblyReturningTheServiceProviderThatOwnsIt connectionString

    try
        runner.RollbackToVersion lastMigrationVersionThatStaysApplied
    finally
        (serviceProvider :> IDisposable).Dispose()
