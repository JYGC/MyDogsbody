/// The measured template shapes, as synthetic documents in the measured LAYOUTS - no real
/// amount, reference, account number, address or supplier name. Each proves one thing the
/// pre-proposal named as unproven before this change: that ApplyTemplateWorkflow, not a scratch
/// script, can actually extract these seven shapes. See design.md -> Testing strategy -> The
/// four measured fixtures.
module MyDogsbody.Tests.Fixtures.MeasuredTemplates

open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Fixture setup built an invalid value: {reason}"

let private line blockIndex text : TextLine = { Text = text; BlockIndex = blockIndex }

let private scannedMessage subject (parts: (MessagePart * TextLine list) list) : ScannedMessage =
    {
        SourceMessageId = SourceMessageId.create "fixture-msg" |> valueOrFail
        Sender = "billing@example.invalid"
        Subject = subject
        ReceivedAt = System.DateTime(2026, 7, 14)
        Parts = parts
    }

let private validTemplate supplierId (rules: TemplateFieldRule list) : ValidTemplate =
    let unvalidated: UnvalidatedTemplate =
        { SupplierId = supplierId; Name = "Fixture template"; Part = AnyPart; Position = 0; Rules = rules }

    match ValidateTemplateWorkflow.validateTemplate unvalidated with
    | Ok template -> template
    | Error error -> failwith $"Fixture setup produced an invalid template: {error}"

let paymentTermDays30 = PaymentTermDays.create 30 |> valueOrFail

// ---------- Invoice-management platform ----------
// Proves: AfterLabel reference, LinesAfterLabel("Total", 1) amount, and DateFromField IssueDate
// producing a due date the document never states anywhere in its text.

module InvoiceManagementPlatform =

    let message =
        scannedMessage
            ""
            [ BodyPart,
              [ line 0 "Invoice Reference: SYN-1001"
                line 0 "Description"
                line 0 "Total"
                line 0 "$245.00"
                line 0 "Date: 14 Jul 2026" ] ]

    let template =
        validTemplate
            "1"
            [ { Field = Reference; Rule = AfterLabel "Invoice Reference:"; Hint = AsText }
              { Field = Amount; Rule = LinesAfterLabel("Total", 1); Hint = AsMoney '.' }
              { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }
              { Field = IssueDate; Rule = AfterLabel "Date:"; Hint = AsDate "d MMM yyyy" }
              { Field = DueDate; Rule = DateFromField IssueDate; Hint = AsDate "d MMM yyyy" } ]

// ---------- Water utility ----------
// Proves: LinesAfterLabel for all three required fields; a second template, differing in its
// labels for a direct-debit customer, is REACHED by SelectTemplateWorkflow when the first
// template's labels are not present in the document.

module WaterUtility =

    /// A "Due date" biller - the customer pays by another method each billing period.
    let dueDateMessage =
        scannedMessage
            ""
            [ BodyPart,
              [ line 0 "Reference"
                line 0 "WU-88213"
                line 0 "Amount due"
                line 0 "$142.50"
                line 0 "Due date"
                line 0 "22 Aug 2026" ] ]

    let dueDateTemplate =
        validTemplate
            "2"
            [ { Field = Reference; Rule = LinesAfterLabel("Reference", 1); Hint = AsText }
              { Field = Amount; Rule = LinesAfterLabel("Amount due", 1); Hint = AsMoney '.' }
              { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }
              { Field = DueDate; Rule = LinesAfterLabel("Due date", 1); Hint = AsDate "d MMM yyyy" } ]

    /// The same supplier's direct-debit customers see this shape instead - no "Due date"
    /// anywhere, so dueDateTemplate's Amount rule (which the direct-debit layout also renumbers
    /// the lines around) fails to find its label, and SelectTemplateWorkflow must move on to
    /// directDebitTemplate to still produce an invoice.
    let directDebitMessage =
        scannedMessage
            ""
            [ BodyPart,
              [ line 0 "Reference"
                line 0 "WU-88214"
                line 0 "Direct debit collection amount"
                line 0 "$142.50"
                line 0 "Collection date"
                line 0 "22 Aug 2026" ] ]

    let directDebitTemplate =
        validTemplate
            "2"
            [ { Field = Reference; Rule = LinesAfterLabel("Reference", 1); Hint = AsText }
              { Field = Amount; Rule = LinesAfterLabel("Direct debit collection amount", 1); Hint = AsMoney '.' }
              { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }
              { Field = DueDate; Rule = LinesAfterLabel("Collection date", 1); Hint = AsDate "d MMM yyyy" } ]

// ---------- Accounting platform ----------
// Proves: a different rule kind per field in one document.

module AccountingPlatform =

    let message =
        scannedMessage
            ""
            [ BodyPart,
              [ line 0 "Invoice No"
                line 0 "ACC-77410"
                line 0 "Total $389.20 including tax"
                line 0 "Payment due: 11 Sep 2026" ] ]

    let template =
        validTemplate
            "3"
            [ { Field = Reference; Rule = LinesAfterLabel("Invoice No", 1); Hint = AsText }
              { Field = Amount; Rule = RegexCapture @"Total \$([\d.]+)"; Hint = AsMoney '.' }
              { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }
              { Field = DueDate; Rule = AfterLabel "Payment due:"; Hint = AsDate "d MMM yyyy" } ]

// ---------- Energy retailer ----------
// Proves: LinesAfterLabel throughout, with both "Due Date" and "Date of Issue" read directly
// (not derived) in the same document.

module EnergyRetailer =

    let message =
        scannedMessage
            ""
            [ BodyPart,
              [ line 0 "Account"
                line 0 "ER-50291"
                line 0 "Amount payable"
                line 0 "$210.75"
                line 0 "Date of Issue"
                line 0 "1 Jul 2026"
                line 0 "Due Date"
                line 0 "29 Jul 2026" ] ]

    let template =
        validTemplate
            "4"
            [ { Field = Reference; Rule = LinesAfterLabel("Account", 1); Hint = AsText }
              { Field = Amount; Rule = LinesAfterLabel("Amount payable", 1); Hint = AsMoney '.' }
              { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText }
              { Field = IssueDate; Rule = LinesAfterLabel("Date of Issue", 1); Hint = AsDate "d MMM yyyy" }
              { Field = DueDate; Rule = LinesAfterLabel("Due Date", 1); Hint = AsDate "d MMM yyyy" } ]

// ---------- Attachment-name variant ----------
// Proves: AttachmentName "^(\d+)\.pdf$" - the most reliable single field source measured.

module AttachmentNameVariant =

    let message =
        scannedMessage
            ""
            [ BodyPart, [ line 0 "Total"; line 0 "$67.00" ]
              AttachmentPart("cover-note.pdf", Pdf), []
              AttachmentPart("9034521.pdf", Pdf), [] ]

    let template =
        validTemplate
            "5"
            [ { Field = Reference; Rule = AttachmentName @"^(\d+)\.pdf$"; Hint = AsText }
              { Field = Amount; Rule = LinesAfterLabel("Total", 1); Hint = AsMoney '.' }
              { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText } ]

// ---------- Subject variant ----------
// Proves: SubjectCapture - 17% of candidates carry the reference in the subject.

module SubjectVariant =

    let message =
        scannedMessage
            "Your invoice REF-66201 is attached"
            [ BodyPart, [ line 0 "Total"; line 0 "$88.40" ] ]

    let template =
        validTemplate
            "6"
            [ { Field = Reference; Rule = SubjectCapture @"invoice (\S+) is attached"; Hint = AsText }
              { Field = Amount; Rule = LinesAfterLabel("Total", 1); Hint = AsMoney '.' }
              { Field = Currency; Rule = FixedValue "AUD"; Hint = AsText } ]
