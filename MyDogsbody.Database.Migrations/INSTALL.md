dotnet tool install -g FluentMigrator.DotNet.Cli

dotnet fm migrate -p sqlite -c "Data Source=bin\Debug\net9.0\test.db" -a .\bin\Debug\net9.0\MyDogsbody.Database.Migrations.dll