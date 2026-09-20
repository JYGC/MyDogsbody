/// Decides which scan window the invoices page opens on. Pure and total - three cases, one
/// place, so the rule cannot end up half in a module creator and half in a mapper.
module MyDogsbody.Domain.Invoices.ResolveScanWindowWorkflow

open MyDogsbody.Domain.Invoices

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - ResolveScanWindowWorkflow.fs: resolveScanWindow
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
