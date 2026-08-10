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

/// AttachmentFormat is NULL unless DocumentPart is the Attachment case - split-column encoding,
/// see design.md.
[<CLIMutable>]
type InvoiceTemplateRecord = {
    Id: int
    SupplierId: int
    Name: string
    DocumentPart: string
    AttachmentFormat: string option
    Position: int
}

/// RuleText/RuleOffset/RuleSourceField hold whichever payload RuleKind's case carries and are
/// NULL otherwise; HintText is likewise NULL unless HintKind needs one. Split-column encoding,
/// see design.md.
[<CLIMutable>]
type TemplateFieldRuleRecord = {
    Id: int
    TemplateId: int
    TargetField: string
    RuleKind: string
    RuleText: string option
    RuleOffset: int option
    RuleSourceField: string option
    HintKind: string
    HintText: string option
}