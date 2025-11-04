namespace MyDogsbody.Database.Migrations.Migrations

open FluentMigrator

[<Migration(20251104000002L)>]
type CreateCommentTable() =
    inherit Migration()

    override this.Up() =
        this.Create.Table("Comments")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("BlogId").AsInt32().NotNullable()
            .WithColumn("Author").AsString(100).NotNullable()
            .WithColumn("Content").AsString().NotNullable()
            .WithColumn("CreatedAt").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
            |> ignore

    override this.Down() =
        this.Delete.Table("Comments") |> ignore