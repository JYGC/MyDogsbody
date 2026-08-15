module MyDogsbody.Tests.Domain.Invoices.MatchSupplierWorkflowTests

open System
open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices

let private valueOrFail (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error reason -> failwith $"Test setup built an invalid value: {reason}"

let private supplier id matchers : StoredSupplier =
    {
        Id = SupplierId.create id |> valueOrFail
        Name = SupplierName.create $"Supplier {id}" |> valueOrFail
        PaymentTermDays = PaymentTermDays.create 30 |> valueOrFail
        Matchers = matchers
    }

let private message sender subject : ScannedMessage =
    {
        SourceMessageId = SourceMessageId.create "msg-1" |> valueOrFail
        Sender = sender
        Subject = subject
        ReceivedAt = DateTime(2026, 7, 14)
        Parts = []
    }

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier returns the one supplier whose sender address matches exactly`` () =
    let acme = supplier "1" [ SenderAddress "billing@acme.example" ]
    let other = supplier "2" [ SenderAddress "billing@other.example" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme; other ] (message "billing@acme.example" "Invoice")

    match actual with
    | Ok supplierId -> Assert.Equal("1", SupplierId.value supplierId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier matches a sender-domain rule against an address in that domain`` () =
    let acme = supplier "1" [ SenderDomain "acme.example" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message "invoices@acme.example" "Invoice")

    match actual with
    | Ok supplierId -> Assert.Equal("1", SupplierId.value supplierId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier compares sender address case-insensitively`` () =
    let acme = supplier "1" [ SenderAddress "Billing@Acme.example" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message "billing@acme.example" "Invoice")

    match actual with
    | Ok supplierId -> Assert.Equal("1", SupplierId.value supplierId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier compares sender domain case-insensitively`` () =
    let acme = supplier "1" [ SenderDomain "ACME.example" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message "invoices@acme.example" "Invoice")

    match actual with
    | Ok supplierId -> Assert.Equal("1", SupplierId.value supplierId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier matches a subject substring`` () =
    let acme = supplier "1" [ SubjectPattern "your acme statement" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message "noreply@example.com" "Your Acme Statement is ready")

    match actual with
    | Ok supplierId -> Assert.Equal("1", SupplierId.value supplierId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier returns SupplierNotRecognised carrying the sender when nothing matches`` () =
    let acme = supplier "1" [ SenderAddress "billing@acme.example" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message "unknown@nowhere.example" "Invoice")

    match actual with
    | Error (SupplierNotRecognised sender) -> Assert.Equal("unknown@nowhere.example", sender)
    | other -> Assert.Fail($"Expected Error(SupplierNotRecognised _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier returns MultipleSuppliersMatched carrying every matching supplier`` () =
    let acme = supplier "1" [ SenderDomain "shared.example" ]
    let other = supplier "2" [ SenderDomain "shared.example" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme; other ] (message "billing@shared.example" "Invoice")

    match actual with
    | Error (MultipleSuppliersMatched (sender, suppliers)) ->
        Assert.Equal("billing@shared.example", sender)
        Assert.Equal<string list>([ "1"; "2" ], suppliers |> List.map SupplierId.value)
    | other -> Assert.Fail($"Expected Error(MultipleSuppliersMatched _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier never matches a supplier with no match rules`` () =
    let noRules = supplier "1" []

    let actual = MatchSupplierWorkflow.matchSupplier [ noRules ] (message "billing@acme.example" "Invoice")

    match actual with
    | Error (SupplierNotRecognised _) -> ()
    | other -> Assert.Fail($"Expected Error(SupplierNotRecognised _), but got {other}")

// PR #11 review, finding 7: the domain is the part after the LAST at-sign of the ADDRESS, and a
// Sender arrives as whatever change #3's mail store hands over - very often a full
// "Display Name <address>" header rather than a bare address.

[<Theory; Trait("Level", "Unit")>]
[<InlineData("Acme Billing <billing@acme.example>")>]
[<InlineData("<billing@acme.example>")>]
[<InlineData("Acme Billing  <billing@acme.example>  ")>]
let ``matchSupplier reads the domain out of a display-name sender`` (sender: string) =
    // Splitting on the first '@' and taking the rest leaves the trailing '>' on the domain, so
    // SenderDomain "acme.example" did not match - and neither did SenderAddress, so the supplier
    // fell through to SupplierNotRecognised entirely.
    let acme = supplier "1" [ SenderDomain "acme.example" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message sender "Invoice")

    match actual with
    | Ok supplierId -> Assert.Equal("1", SupplierId.value supplierId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier matches a sender-address rule against a display-name sender`` () =
    let acme = supplier "1" [ SenderAddress "billing@acme.example" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message "Acme Billing <billing@acme.example>" "Invoice")

    match actual with
    | Ok supplierId -> Assert.Equal("1", SupplierId.value supplierId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier takes the domain after the last at-sign, not the first`` () =
    // A quoted local part is legal in an address and carries its own '@'.
    let acme = supplier "1" [ SenderDomain "acme.example" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message "\"a@b\"@acme.example" "Invoice")

    match actual with
    | Ok supplierId -> Assert.Equal("1", SupplierId.value supplierId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("no-at-sign-here")>]
let ``an empty domain matcher does not match a message that carries no sender domain`` (sender: string) =
    // A stored empty matcher (savable before this change) compared equal to the "" that a
    // senderless message yields, so that one supplier claimed every such message.
    let acme = supplier "1" [ SenderDomain "" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message sender "Invoice")

    match actual with
    | Error (SupplierNotRecognised _) -> ()
    | other -> Assert.Fail($"Expected Error(SupplierNotRecognised _), but got {other}")

// PR #11 review, finding 11: Sender and Subject are unconstrained strings an outer-ring adapter
// fills in, and a mail store with no subject hands over null, not "". An exception raised here
// would unwind out of the domain, past a composition root that maps values rather than catching.

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier returns a Result rather than raising when the sender is null`` () =
    let acme = supplier "1" [ SenderDomain "acme.example" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message null "Invoice")

    match actual with
    | Error (SupplierNotRecognised _) -> ()
    | other -> Assert.Fail($"Expected Error(SupplierNotRecognised _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier returns a Result rather than raising when the subject is null`` () =
    let acme = supplier "1" [ SubjectPattern "your invoice" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message "billing@acme.example" null)

    match actual with
    | Error (SupplierNotRecognised _) -> ()
    | other -> Assert.Fail($"Expected Error(SupplierNotRecognised _), but got {other}")

[<Fact; Trait("Level", "Unit")>]
let ``a null sender still matches a supplier on its subject rule, rather than failing the whole match`` () =
    // The guard must make the sender rules not match; it must not abandon the other rules.
    let acme = supplier "1" [ SenderDomain "acme.example"; SubjectPattern "your invoice" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message null "Your Invoice is ready")

    match actual with
    | Ok supplierId -> Assert.Equal("1", SupplierId.value supplierId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")

// PR #11 review, finding 8: a subject is text a rule is evaluated against, so the same
// normalization applies to it.

[<Fact; Trait("Level", "Unit")>]
let ``matchSupplier matches a subject substring across a non-breaking space`` () =
    // U+00A0 between "Acme" and "Statement" in the subject, a plain space in the stored pattern.
    let acme = supplier "1" [ SubjectPattern "your acme statement" ]

    let actual = MatchSupplierWorkflow.matchSupplier [ acme ] (message "billing@acme.example" "Your Acme\u00A0Statement is ready")

    match actual with
    | Ok supplierId -> Assert.Equal("1", SupplierId.value supplierId)
    | Error error -> Assert.Fail($"Expected Ok, but got Error: {error}")
