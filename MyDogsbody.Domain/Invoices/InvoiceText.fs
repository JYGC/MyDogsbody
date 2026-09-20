/// A file of its own rather than a corner of a workflow, for the reason TextNormalization is one:
/// change #4's InvoiceReference.create needs foldReferenceWhitespace, and a constrained type
/// reaching into a workflow module to borrow a helper inverts the dependency between them.
/// CLAUDE.md -> Conventions also asks a *Workflow.fs to expose exactly one public function, which
/// ApplyTemplateWorkflow could not while it owned this.
module MyDogsbody.Domain.Invoices.InvoiceText

open System
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.InvoiceTemplates

/// task 4.6: the same reference printed "1234 5678 90" in a PDF and "1234567890" in an
/// attachment filename must produce one value, or the natural key later turns one invoice into
/// two ledger rows and two calendar events. Folded where the two sources first meet.
let foldReferenceWhitespace (raw: string) : string =
    if isNull raw then "" else raw |> String.filter (fun character -> not (Char.IsWhiteSpace character))

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - InvoiceText.fs: normalizeLine
let normalizeLine (text: string) : string =
    match TextNormalization.normalize [ { Text = (if isNull text then "" else text); BlockIndex = 0 } ] with
    | [ normalized ] -> normalized.Text
    | _ -> ""
