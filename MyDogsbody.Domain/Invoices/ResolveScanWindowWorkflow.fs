/// Decides which scan window the invoices page opens on. Pure and total - three cases, one
/// place, so the rule cannot end up half in a module creator and half in a mapper.
module MyDogsbody.Domain.Invoices.ResolveScanWindowWorkflow

open MyDogsbody.Domain.Invoices

/// Given the windows the store holds and the day count the user last chose (a NUMBER, not a
/// foreign key - it survives its row being deleted), return the window to open on:
///
///   remembered is present in the store      -> that one
///   remembered is absent (or None)          -> 14, if 14 is present
///   remembered is absent and 14 is absent   -> the shortest window still present
///   the store holds nothing (cannot happen  -> the fallback constant, constructed
///     - CannotDeleteLastScanWindow forbids it)
///
/// The remembered-but-since-deleted row is the case nobody tries by hand; it has its own test.
let resolveScanWindow
    (storedWindows: StoredScanWindow list)
    (remembered: ScanWindowDays option)
    : ScanWindowDays =

    let present = storedWindows |> List.map (fun window -> window.Days)

    let byDays (days: int) =
        present |> List.tryFind (fun window -> ScanWindowDays.value window = days)

    let rememberedDays = remembered |> Option.map ScanWindowDays.value

    match rememberedDays |> Option.bind byDays with
    | Some window -> window
    | None ->
        match byDays ScanWindowDays.fallback with
        | Some fallbackWindow -> fallbackWindow
        | None ->
            present
            |> List.sortBy ScanWindowDays.value
            |> List.tryHead
            |> Option.defaultValue ScanWindowDays.fallbackWindow
