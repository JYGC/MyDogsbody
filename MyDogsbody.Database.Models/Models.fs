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

// --- change #4: the ledger ---

/// Amount is TEXT (exact decimal, InvariantCulture); IssueDate / DueDate are TEXT yyyy-MM-dd and
/// nullable (Q1.10); MessageReceivedAt and ScannedAt are TEXT ISO 8601.
[<CLIMutable>]
type InvoiceRecord = {
    Id: int
    SupplierId: int
    TemplateId: int
    Reference: string
    Amount: string
    Currency: string
    IssueDate: string option
    DueDate: string option
    SourceMessageId: string
    MessageReceivedAt: string
    ScannedAt: string
}

/// Cause names the ScanProblemCause union case; Detail holds its payload (unit-separator joined
/// where the case carries several values); SupplierId is the "primary" supplier when the cause
/// names exactly one, NULL otherwise. See InvoiceRecordMappers for the encoding.
[<CLIMutable>]
type ScanProblemRecord = {
    Id: int
    SourceMessageId: string
    SupplierId: int option
    Sender: string
    Subject: string
    ReceivedAt: string
    Cause: string
    Detail: string option
    RecordedAt: string
}

[<CLIMutable>]
type InvoiceTombstoneRecord = {
    Id: int
    SupplierId: int
    Reference: string
    DeletedAt: string
}

[<CLIMutable>]
type ScanWindowRecord = { Id: int; Days: int }

/// The single settings row. SelectedScanWindowDays is a NUMBER (design decision 6), nullable -
/// NULL means nothing chosen yet.
[<CLIMutable>]
type InvoiceSettingsRecord = { Id: int; SelectedScanWindowDays: int option }