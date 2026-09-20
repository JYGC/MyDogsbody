module MyDogsbody.Tests.Domain.Calendar.InvoiceSyncKeyTests

open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar

// The one derivation (Q2.10). Three hand-rolled derivations would agree right up until one did
// not, so every test here goes through InvoiceSyncKey.derive rather than building a key by hand.

let private supplierId value =
    match SupplierId.create value with
    | Ok id -> id
    | Error reason -> failwith $"test fixture invalid: {reason}"

let private reference value =
    match InvoiceReference.create value with
    | Ok r -> r
    | Error reason -> failwith $"test fixture invalid: {reason}"

[<Fact; Trait("Level", "Unit")>]
let ``derive is stable for the same supplier and reference`` () =
    let first = InvoiceSyncKey.derive (supplierId "1") (reference "INV-1042")
    let second = InvoiceSyncKey.derive (supplierId "1") (reference "INV-1042")

    Assert.Equal(first, second)
    Assert.Equal(InvoiceSyncKey.value first, InvoiceSyncKey.value second)

[<Fact; Trait("Level", "Unit")>]
let ``derive gives different keys for two different invoices`` () =
    let bySupplier = InvoiceSyncKey.derive (supplierId "1") (reference "INV-1042")
    let byReference = InvoiceSyncKey.derive (supplierId "2") (reference "INV-1042")
    let byBoth = InvoiceSyncKey.derive (supplierId "1") (reference "INV-9999")

    Assert.NotEqual(bySupplier, byReference)
    Assert.NotEqual(bySupplier, byBoth)

[<Fact; Trait("Level", "Unit")>]
let ``parse (value (derive a b)) round-trips`` () =
    let derived = InvoiceSyncKey.derive (supplierId "7") (reference "ABC-123")

    match InvoiceSyncKey.parse (InvoiceSyncKey.value derived) with
    | Ok parsed -> Assert.Equal(derived, parsed)
    | Error reason -> Assert.Fail($"Expected Ok, but got Error: {reason}")

[<Theory; Trait("Level", "Unit")>]
[<InlineData(null)>]
[<InlineData("")>]
[<InlineData("no-separator-here")>]
let ``parse rejects a value that is not a derived key`` (entered: string) =
    match InvoiceSyncKey.parse entered with
    | Error _ -> ()
    | Ok _ -> Assert.Fail("Expected Error, but got Ok")

[<Fact; Trait("Level", "Unit")>]
let ``the extended-property name is a single literal`` () =
    Assert.Equal("mydogsbody.invoice", InvoiceSyncKey.PrivateExtendedPropertyNameOnAGoogleCalendarEvent)

[<Fact; Trait("Level", "Unit")>]
let ``supplierRowIdAndInvoiceReferenceAsPlainStrings recovers the raw supplier id and reference a key was derived from`` () =
    let derived = InvoiceSyncKey.derive (supplierId "42") (reference "INV-1042")

    match InvoiceSyncKey.supplierRowIdAndInvoiceReferenceAsPlainStrings derived with
    | Some(supplierIdPart, referencePart) ->
        Assert.Equal("42", supplierIdPart)
        Assert.Equal("INV-1042", referencePart)
    | None -> Assert.Fail("Expected Some, but got None")
