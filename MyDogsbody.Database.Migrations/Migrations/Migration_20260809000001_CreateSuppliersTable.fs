namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

[<Migration(20260809000001L)>]
type CreateSuppliersTable() =
    inherit Migration()

    override this.Up() =
        this.Create.Table("Suppliers")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(200).NotNullable()
            .WithColumn("PaymentTermDays").AsInt32().NotNullable()
            |> ignore

        this.Create.Index("IX_Suppliers_Name")
            .OnTable("Suppliers")
            .OnColumn("Name").Ascending()
            .WithOptions().Unique()
            |> ignore

    override this.Down() =
        this.Delete.Index("IX_Suppliers_Name").OnTable("Suppliers") |> ignore
        this.Delete.Table("Suppliers") |> ignore
