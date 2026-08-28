/// The scan orchestration: read the selected account, match a supplier, apply its templates,
/// validate, store; record a problem for every message that yields nothing, and continue.
///
/// The pipeline body lands in Phase 4. What is here now (Phase 3) is the one piece of arithmetic
/// worth testing on its own: the cutoff.
module MyDogsbody.Domain.Invoices.ScanForInvoicesWorkflow

open MyDogsbody.Domain.MailAccounts
open MyDogsbody.Domain.Invoices

/// "The last N days" names a set of DATES, not 24*N hours (Q1.18): the cutoff is the start of the
/// day N days before today, so the same window scanned at 09:00 and at 17:00 covers the same
/// mail. Days are uniform, so there is no month-end trap.
///
/// Pure - the clock is the GetCurrentTime dependency, supplied as a fixed instant in tests.
let computeCutoff (getCurrentTime: GetCurrentTime) (window: ScanWindowDays) : ScanCutoff =
    let today = (getCurrentTime ()).Date
    ScanCutoff.ofStartOfDay (today.AddDays(-float (ScanWindowDays.value window)))
