module MyDogsbody.Domain.Calendar.CalendarDateRangeWorkflow

open MyDogsbody.Domain.Invoices
open MyDogsbody.Domain.Calendar

/// Derives the date range `ListCalendarEvents` is bounded by (Q2.5).
///
/// Mirrored around today rather than applied backwards only: the scan window looks BACKWARDS at
/// when mail ARRIVED, but an invoice event sits on its DUE date, normally ahead of that. The
/// range is then stretched forward, never back, to cover the latest due date actually in view -
/// a supplier on 60-day terms inside a 14-day scan window would otherwise fall outside a
/// [today-14, today+14] range, read as a missing event, and get a duplicate created for it.
let derive
    (getCurrentTime: GetCurrentTime)
    (window: ScanWindowDays)
    (invoicesInWindow: UploadableInvoice list)
    : CalendarDateRange =
    let today = (getCurrentTime ()).Date
    let days = float (ScanWindowDays.value window)

    let mirroredStart = today.AddDays(-days)
    let mirroredEnd = today.AddDays(days)

    let stretchedEnd =
        invoicesInWindow
        |> List.map (fun invoice -> InvoiceDueDate.value invoice.DueDate)
        |> List.fold max mirroredEnd

    match CalendarDateRange.create mirroredStart stretchedEnd with
    | Ok range -> range
    | Error reason ->
        // mirroredStart can never exceed stretchedEnd (mirroredEnd >= mirroredStart always, and
        // stretchedEnd only ever grows from there), so this is unreachable - failing loud rather
        // than silently substituting a range is the honest response if it is ever wrong.
        failwith $"CalendarDateRangeWorkflow derived an invalid range: {reason}"
