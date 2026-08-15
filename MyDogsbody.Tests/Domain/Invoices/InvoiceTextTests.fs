module MyDogsbody.Tests.Domain.Invoices.InvoiceTextTests

open Xunit
open MyDogsbody.Domain.Invoices

// PR #11 review, finding 14: foldReferenceWhitespace was a second public function inside
// ApplyTemplateWorkflow.fs, which CLAUDE.md -> Conventions asks a *Workflow.fs not to have. It
// lives in InvoiceText now, where change #4's InvoiceReference.create can reach it without
// importing a workflow module - and where it gets tests of its own rather than only being
// exercised through the engine.

[<Theory; Trait("Level", "Unit")>]
[<InlineData("1234 5678 90", "1234567890")>]
[<InlineData("1234567890", "1234567890")>]
[<InlineData("  INV-1042  ", "INV-1042")>]
[<InlineData("INV\t1042\n", "INV1042")>]
[<InlineData("", "")>]
[<InlineData(null, "")>]
let ``foldReferenceWhitespace removes every whitespace character, inside and out`` (raw: string) (expected: string) =
    // task 4.6: the same reference printed "1234 5678 90" in a PDF and "1234567890" in a
    // filename must produce ONE value, or change #4's natural key turns one invoice into two
    // ledger rows and two calendar events.
    Assert.Equal(expected, InvoiceText.foldReferenceWhitespace raw)

[<Theory; Trait("Level", "Unit")>]
[<InlineData("  Total:   100.00  ", "Total: 100.00")>]
[<InlineData("Your invoice\u00A0REF-1", "Your invoice REF-1")>]
[<InlineData("Your\u00A0invoice REF-1", "Your invoice REF-1")>]
[<InlineData("９０３.pdf", "903.pdf")>]
[<InlineData("", "")>]
[<InlineData("   ", "")>]
[<InlineData(null, "")>]
let ``normalizeLine puts one free-standing string through the document normalization`` (raw: string) (expected: string) =
    // A subject or a filename is text a rule is evaluated against, and requirements.md asks for
    // "the identical normalization at authoring time and at scan time" - so this defers to
    // TextNormalization rather than approximating it. An input that normalizes away entirely
    // comes back as "" rather than as a missing line.
    Assert.Equal(expected, InvoiceText.normalizeLine raw)
