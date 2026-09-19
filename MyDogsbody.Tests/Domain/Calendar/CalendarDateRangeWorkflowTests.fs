module MyDogsbody.Tests.Domain.Calendar.CalendarDateRangeWorkflowTests

open System
open Xunit
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar
open MyDogsbody.Domain.Calendar.CalendarDateRangeWorkflow

let private orFail =
    function
    | Ok v -> v
    | Error e -> failwith $"test setup: {e}"

let private today = DateTime(2026, 3, 10)
let private getCurrentTime: GetCurrentTime = fun () -> today
let private window = ScanWindowDays.create 14 |> orFail

let private uploadableDue (dueDate: DateTime) : UploadableInvoice =
    { Id = InvoiceId.create "1" |> orFail
      SupplierId = SupplierId.create "sup-1" |> orFail
      Reference = InvoiceReference.create "INV-1" |> orFail
      Amount = Money.create 100m "AUD" |> orFail
      DueDate = InvoiceDueDate.create dueDate |> orFail }

[<Fact; Trait("Level", "Unit")>]
let ``derive mirrors the range around today when no invoice falls due beyond it`` () =
    let range = derive getCurrentTime window []

    Assert.Equal(today.AddDays(-14.0), CalendarDateRange.startDate range)
    Assert.Equal(today.AddDays(14.0), CalendarDateRange.endDate range)

[<Fact; Trait("Level", "Unit")>]
let ``derive gives a valid mirrored range for an empty invoice list`` () =
    let range = derive getCurrentTime window []

    Assert.True(CalendarDateRange.startDate range <= CalendarDateRange.endDate range)

[<Fact; Trait("Level", "Unit")>]
let ``derive stretches the range forward to cover a due date beyond the mirrored end`` () =
    // A supplier on ~60-day terms inside a 14-day scan window: the mirrored end is 2026-03-24,
    // but this invoice is due 2026-05-09 - well beyond it.
    let farOut = uploadableDue (DateTime(2026, 5, 9))

    let range = derive getCurrentTime window [ farOut ]

    Assert.Equal(today.AddDays(-14.0), CalendarDateRange.startDate range)
    Assert.Equal(DateTime(2026, 5, 9), CalendarDateRange.endDate range)

[<Fact; Trait("Level", "Unit")>]
let ``derive stretches the range backward to cover a due date before the mirrored start`` () =
    // An invoice already overdue by the time it was scanned: due 2026-02-01, well before the
    // mirrored start of 2026-02-24 (today - 14). Requirements.md's date-range section states this
    // unconditionally ("THE SYSTEM SHALL never produce a range that excludes an invoice currently
    // in view") - not only for a due date beyond the mirrored END. An event already on the
    // calendar for this invoice, dated 2026-02-01, would sit outside a forward-only-stretched
    // range and read as absent - producing a duplicate CreateEvent for an invoice that already
    // has one, exactly the harm this requirement exists to prevent.
    let overdue = uploadableDue (DateTime(2026, 2, 1))

    let range = derive getCurrentTime window [ overdue ]

    Assert.Equal(DateTime(2026, 2, 1), CalendarDateRange.startDate range)
    Assert.Equal(today.AddDays(14.0), CalendarDateRange.endDate range)

[<Fact; Trait("Level", "Unit")>]
let ``derive never produces a range that excludes an invoice in view`` () =
    let overdue = uploadableDue (DateTime(2026, 1, 5))
    let nearby = uploadableDue (DateTime(2026, 3, 15))
    let farOut = uploadableDue (DateTime(2026, 6, 1))

    let range = derive getCurrentTime window [ overdue; nearby; farOut ]

    [ overdue; nearby; farOut ]
    |> List.iter (fun invoice ->
        let dueDate = InvoiceDueDate.value invoice.DueDate
        Assert.True(dueDate >= CalendarDateRange.startDate range && dueDate <= CalendarDateRange.endDate range))
