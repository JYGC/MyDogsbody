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
