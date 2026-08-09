/// The bottom mapping point: the plain SQLite record (int identity, plain strings) <-> domain
/// type. One of the architecture's two mappers. Everything between this file and
/// Startup/SupplierApiMappers.fs travels as domain types, unmapped.
///
/// Pure - no I/O, no handleError, no Dapper calls. SupplierStore does the talking; this file
/// only translates, so it can be asserted field-for-field without a database.
module MyDogsbody.Database.SupplierRecordMappers

open MyDogsbody.Domain.Suppliers
open MyDogsbody.Database.Models

/// Domain -> persistence. Exhaustive: adding a case to MatcherKind breaks this build.
let toMatcherKindString (kind: MatcherKind) : string =
    match kind with
    | Sender -> "Sender"
    | Domain -> "Domain"
    | Subject -> "Subject"

/// Persistence -> domain. Returns Result because the column is a plain TEXT value: a row
/// written by an older build, or edited by hand, can carry a string no current build declares.
let fromMatcherKindString (value: string) : Result<MatcherKind, string> =
    match value with
    | "Sender" -> Ok Sender
    | "Domain" -> Ok Domain
    | "Subject" -> Ok Subject
    | unknown -> Error $"Stored matcher kind '{unknown}' has no domain equivalent."

/// The identifier the domain carries, as the store's own key type. Only ever called on an id
/// that came from a row already read (SupplierId.create only checks non-empty, so nothing
/// upstream guarantees it parses) - a value that does not parse is a data-integrity failure, not
/// something a user did, so it is allowed to raise and be caught like any other unexpected
/// adapter failure.
let toRowId (id: SupplierId) : int = int (SupplierId.value id)

/// Persistence -> domain, whole row plus its matchers.
let toStoredSupplier
    (row: SupplierRecord)
    (matcherRows: SupplierMatcherRecord list)
    : Result<StoredSupplier, string> =
    match SupplierId.create (string row.Id) with
    | Error reason -> Error reason
    | Ok id ->

    match SupplierName.create row.Name with
    | Error reason -> Error reason
    | Ok name ->

    match PaymentTermDays.create row.PaymentTermDays with
    | Error reason -> Error reason
    | Ok paymentTermDays ->

    let rec mapMatchers remaining acc =
        match remaining with
        | [] -> Ok (List.rev acc)
        | (matcherRow: SupplierMatcherRecord) :: rest ->
            match fromMatcherKindString matcherRow.Kind with
            | Error reason -> Error reason
            | Ok kind ->

            match SupplierMatcher.create kind matcherRow.Value with
            | Error reason -> Error reason
            | Ok matcher -> mapMatchers rest (matcher :: acc)

    match mapMatchers matcherRows [] with
    | Error reason -> Error reason
    | Ok matchers ->
        Ok
            {
                Id = id
                Name = name
                PaymentTermDays = paymentTermDays
                Matchers = matchers
            }

/// Domain -> persistence, for a row the store has not seen before. Id is a placeholder - the
/// insert excludes that column so SQLite assigns it.
let toNewSupplierRecord (supplier: ValidSupplier) : SupplierRecord =
    {
        Id = 0
        Name = SupplierName.value supplier.Name
        PaymentTermDays = PaymentTermDays.value supplier.PaymentTermDays
    }

/// Domain -> persistence, for a matcher row the store has not seen before.
let toNewMatcherRecord (supplierId: int) (matcher: SupplierMatcher) : SupplierMatcherRecord =
    {
        Id = 0
        SupplierId = supplierId
        Kind = toMatcherKindString (SupplierMatcher.kind matcher)
        Value = SupplierMatcher.value matcher
    }
