module MyDogsbody.Domain.Calendar.CalendarDateRangeWorkflow

open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar

/// Derives the date range `ListCalendarEvents` is bounded by (Q2.5).
///
/// Mirrored around today rather than applied backwards only: the scan window looks BACKWARDS at
/// when mail ARRIVED, but an invoice event sits on its DUE date, normally ahead of that. The
/// range is then stretched to cover the earliest AND latest due date actually in view, in
/// whichever direction each needs - a supplier on 60-day terms inside a 14-day scan window would
/// otherwise fall outside a [today-14, today+14] range on the far end, and an invoice that was
/// already overdue when it was scanned (a due date earlier than today-14 - not exotic for a
/// recurring bill whose due date precedes the notice email) would fall outside it on the near
/// end. Either direction reads a still-live event as missing and creates a duplicate for it, so
/// requirements.md's "never produce a range that excludes an invoice in view" is unconditional,
/// not forward-only, and both ends of the range widen independently to honour it.
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
