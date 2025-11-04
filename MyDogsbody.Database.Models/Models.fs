namespace MyDogsbody.Database.Models

[<CLIMutable>]
type Blog = {
    Id: int
    Title: string
    Content: string
    CreatedAt: System.DateTime
}

[<CLIMutable>]
type Comment = {
    Id: int
    BlogId: int
    Author: string
    Content: string
    CreatedAt: System.DateTime
}