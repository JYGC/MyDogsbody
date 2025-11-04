namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

[<Migration(20251104000001L)>]
type CreateBlogTable() =
    inherit Migration()

    override this.Up() =
        this.Create.Table("Blogs")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Title").AsString(200).NotNullable()
            .WithColumn("Content").AsString().NotNullable()
            .WithColumn("CreatedAt").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
            |> ignore

    override this.Down() =
        this.Delete.Table("Blogs") |> ignore