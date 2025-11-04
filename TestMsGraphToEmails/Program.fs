open MyDogsbody.Database.Migrations.Sqlite

[<EntryPoint>]
let main _ =
    SqliteSetup.setupMigrations("Data Source=app.db")
    0