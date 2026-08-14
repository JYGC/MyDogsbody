module MyDogsbody.Tests.Domain.InvoiceTemplates.TextNormalizationTests

open System
open Xunit
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.InvoiceTemplates

let private line blockIndex text : TextLine = { Text = text; BlockIndex = blockIndex }

[<Fact; Trait("Level", "Unit")>]
let ``normalize applies Unicode NFKC, turning a ligature into its component letters`` () =
    // U+FB01 LATIN SMALL LIGATURE FI has compatibility decomposition "fi"
    let actual = TextNormalization.normalize [ line 0 "aﬁle attached" ]

    Assert.Equal<TextLine list>([ line 0 "afile attached" ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``normalize applies Unicode NFKC, turning a fullwidth letter into its ASCII equivalent`` () =
    // U+FF29 FULLWIDTH LATIN CAPITAL LETTER I has compatibility decomposition "I"
    let actual = TextNormalization.normalize [ line 0 "ＩNVOICE" ]

    Assert.Equal<TextLine list>([ line 0 "INVOICE" ], actual)

[<Theory; Trait("Level", "Unit")>]
[<InlineData("a b")>] // NO-BREAK SPACE
[<InlineData("a b")>] // FIGURE SPACE
[<InlineData("a b")>] // NARROW NO-BREAK SPACE
[<InlineData("a　b")>] // IDEOGRAPHIC SPACE
let ``normalize folds non-breaking and fixed-width space characters to a plain space`` (input: string) =
    let actual = TextNormalization.normalize [ line 0 input ]

    Assert.Equal<TextLine list>([ line 0 "a b" ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``normalize collapses runs of spaces and tabs to a single space`` () =
    let actual = TextNormalization.normalize [ line 0 "Label:   \t\tValue" ]

    Assert.Equal<TextLine list>([ line 0 "Label: Value" ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``normalize strips leading and trailing whitespace from each line`` () =
    let actual = TextNormalization.normalize [ line 0 "  Label: Value  " ]

    Assert.Equal<TextLine list>([ line 0 "Label: Value" ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``normalize joins a wrapped continuation to its predecessor within the same block`` () =
    // "us" starts lower-case and "Please contact" does not end in a sentence terminator
    let actual = TextNormalization.normalize [ line 0 "Please contact"; line 0 "us for help." ]

    Assert.Equal<TextLine list>([ line 0 "Please contact us for help." ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``normalize does not join a continuation across a block boundary`` () =
    // Same text as the join test, but the second line is a different block - LinesAfterLabel
    // depends on the block structure the boundary marks, so this must not merge.
    let actual = TextNormalization.normalize [ line 0 "Please contact"; line 1 "us for help." ]

    Assert.Equal<TextLine list>([ line 0 "Please contact"; line 1 "us for help." ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``normalize drops empty lines before any offset is applied`` () =
    let actual = TextNormalization.normalize [ line 0 "Line one"; line 0 ""; line 0 "Line two" ]

    Assert.Equal<TextLine list>([ line 0 "Line one"; line 0 "Line two" ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``a line that starts lower-case but follows a sentence terminator is not joined`` () =
    // "amount" starts lower-case, but "Total." ends with a sentence terminator - not a wrap.
    let actual = TextNormalization.normalize [ line 0 "Total."; line 0 "amount due separately." ]

    Assert.Equal<TextLine list>([ line 0 "Total."; line 0 "amount due separately." ], actual)

// Order is pinned: NFKC has to run before collapseRuns, because NFKC is what turns each
// non-breaking space into a plain space in the first place - collapseRuns only recognises
// literal ' ' and '\t'. Running collapse before NFKC would see three untouched non-breaking
// spaces, not a collapsible run, and leave all three in the output. Verified empirically before
// writing this test: .NET's NormalizationForm.FormKC alone decomposes U+00A0 to U+0020.
[<Fact; Trait("Level", "Unit")>]
let ``normalize applies NFKC before collapsing runs, so three non-breaking spaces collapse to one`` () =
    let actual = TextNormalization.normalize [ line 0 "Total:   $412.50" ]

    Assert.Equal<TextLine list>([ line 0 "Total: $412.50" ], actual)

// The three measured failure modes (Finding 4) - each one silently produces "rule matched
// nothing" without normalization, so each gets its own test naming the shape it came from.

[<Fact; Trait("Level", "Unit")>]
let ``measured failure mode 1: a label separated from its value by a non-breaking space is matched`` () =
    // Shape: an invoice-management platform separates "Total:" from the amount with U+00A0
    // rather than a plain space.
    let actual = TextNormalization.normalize [ line 0 "Total: $412.50" ]

    Assert.Equal<TextLine list>([ line 0 "Total: $412.50" ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``measured failure mode 2: a label hard-wrapped across two lines is matched`` () =
    // Shape: a water utility's "Amount due:" label wraps across a line break under a narrow
    // column, splitting into "Amount" and "due:".
    let actual = TextNormalization.normalize [ line 0 "Amount"; line 0 "due:" ]

    Assert.Equal<TextLine list>([ line 0 "Amount due:" ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``measured failure mode 3: a label separated from its value by blank lines is matched at offset 1`` () =
    // Shape: a template with "Due Date:" on one line and the date pushed a full blank line
    // below it. LinesAfterLabel(label, 1) means "the next line WITH CONTENT" only because the
    // blank line is dropped here, before any offset is counted.
    let actual = TextNormalization.normalize [ line 0 "Due Date:"; line 0 ""; line 0 "14 Jul 2026" ]

    Assert.Equal<TextLine list>([ line 0 "Due Date:"; line 0 "14 Jul 2026" ], actual)

// normalize's signature promises TextLine list -> TextLine list with no failure channel, but its
// input is text extracted from PDFs and email bodies - the least trustworthy source in the system.
// String.Normalize raises ArgumentException for any string carrying an unpaired surrogate, which
// is exactly what a truncated or mis-decoded extraction produces, and IsNormalized raises on the
// same input so a check-first guard would not help. An exception escaping here would unwind out of
// the domain, past a composition root that maps values rather than catching, and into a UI with no
// alert for it.

/// A line ending in an unpaired high surrogate, built from chars so no literal-encoding step can
/// quietly repair it.
let private loneSurrogateText = String([| 'T'; 'o'; 't'; 'a'; 'l'; ':'; ' '; '4'; '1'; '2'; char 0xD83D |])

[<Fact; Trait("Level", "Unit")>]
let ``normalize returns a line carrying an unpaired surrogate rather than throwing`` () =
    let actual = TextNormalization.normalize [ line 0 loneSurrogateText ]

    // Degraded to the un-normalized line: NFKC is skipped for this one line, every other step
    // still applies. One malformed glyph costs that line its folding, not the whole scan.
    Assert.Equal<TextLine list>([ line 0 loneSurrogateText ], actual)

[<Fact; Trait("Level", "Unit")>]
let ``normalize still normalizes the surrounding lines when one carries an unpaired surrogate`` () =
    let actual =
        TextNormalization.normalize [ line 0 "Invoice:   INV-42"; line 1 loneSurrogateText; line 2 "Due:   1 Jan" ]

    Assert.Equal<TextLine list>(
        [ line 0 "Invoice: INV-42"; line 1 loneSurrogateText; line 2 "Due: 1 Jan" ],
        actual
    )

[<Fact; Trait("Level", "Unit")>]
let ``normalize drops a line whose text is null rather than throwing`` () =
    let actual = TextNormalization.normalize [ line 0 "Invoice: INV-42"; line 1 null ]

    // A null becomes an empty line, and empty lines are dropped by the last step like any other.
    Assert.Equal<TextLine list>([ line 0 "Invoice: INV-42" ], actual)
