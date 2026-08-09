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

[<CLIMutable>]
type SupplierRecord = {
    Id: int
    Name: string
    PaymentTermDays: int
}

[<CLIMutable>]
type SupplierMatcherRecord = {
    Id: int
    SupplierId: int
    Kind: string
    Value: string
}