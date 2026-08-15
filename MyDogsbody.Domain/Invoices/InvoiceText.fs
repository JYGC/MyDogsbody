/// Pure text helpers the Invoices area shares between its workflows and its types.
///
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
    if isNull raw then "" else raw |> String.filter (fun c -> not (Char.IsWhiteSpace c))

/// One free-standing string - a mail subject, an attachment filename - put through the same
/// normalization a document line gets, by making it a one-line, one-block document.
///
/// requirements.md: "WHEN any rule is evaluated THE SYSTEM SHALL first apply a defined
/// normalization to the text, and SHALL apply the identical normalization at authoring time and
/// at scan time." A subject is text a rule is evaluated against, so "identical" has to mean this
/// function and not an approximation of it - the authoring test panel shows the user normalized
/// text, and a pattern written against what the panel showed has to match at scan time.
///
/// normalize drops a line that normalizes to nothing, so an empty or whitespace-only input comes
/// back as "" rather than as a missing element.
let normalizeLine (text: string) : string =
    match TextNormalization.normalize [ { Text = (if isNull text then "" else text); BlockIndex = 0 } ] with
    | [ normalized ] -> normalized.Text
    | _ -> ""
