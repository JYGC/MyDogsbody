module MyDogsbody.Database.Migrations.MigrationSetup

open Microsoft.Extensions.DependencyInjection
open FluentMigrator.Runner

let setupMigrations(connectionString: string) =
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

    let runner = serviceProvider.GetRequiredService<IMigrationRunner>()
    runner.MigrateUp()
