module MyDogsbody.Domain.Calendar.CalendarDateRangeWorkflow

open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - CalendarDateRangeWorkflow.fs: derive
let derive
    (getCurrentTime: GetCurrentTime)
    (window: ScanWindowDays)
    (invoicesInWindow: UploadableInvoice list)
    : CalendarDateRange =
    let today = (getCurrentTime ()).Date
    let days = float (ScanWindowDays.value window)

    let mirroredStart = today.AddDays(-days)
    let mirroredEnd = today.AddDays(days)

    let dueDatesInWindow =
        invoicesInWindow |> List.map (fun invoice -> InvoiceDueDate.value invoice.DueDate)

    let stretchedStart = dueDatesInWindow |> List.fold min mirroredStart
    let stretchedEnd = dueDatesInWindow |> List.fold max mirroredEnd

    match CalendarDateRange.create stretchedStart stretchedEnd with
    | Ok range -> range
    | Error reason ->
        // stretchedStart can never exceed stretchedEnd (mirroredEnd >= mirroredStart always, and
        // each end only ever moves outward from there, in its own direction, from the same list of
        // due dates), so this is unreachable - failing loud rather than silently substituting a
        // range is the honest response if it is ever wrong.
        failwith $"CalendarDateRangeWorkflow derived an invalid range: {reason}"
