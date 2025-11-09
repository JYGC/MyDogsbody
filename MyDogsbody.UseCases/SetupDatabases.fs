module MyDogsbody.UseCases.SetupDatabases

open MyDogsbody.Infrastructure.Database

let infrastructureDatabasePath = "Infrastructure.db"
let infrastructureDatabaseConnectionType = "shared"

let infrastructureDatabaseContext =
    InfrastructureDatabaseContext.getInfrastructureDatabaseContext
        infrastructureDatabasePath
        infrastructureDatabaseConnectionType