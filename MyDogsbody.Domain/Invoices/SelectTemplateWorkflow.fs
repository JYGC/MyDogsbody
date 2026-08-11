module MyDogsbody.Domain.Invoices.SelectTemplateWorkflow

open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates

let private messageCarries (part: DocumentPart) (message: ScannedMessage) : bool =
    match part with
    | AnyPart -> true
    | Body -> message.Parts |> List.exists (fun (messagePart, _) -> messagePart = BodyPart)
    | Attachment wantedFormat ->
        message.Parts
        |> List.exists (fun (messagePart, _) ->
            match messagePart with
            | AttachmentPart(_, format) -> format = wantedFormat
            | BodyPart
            | SubjectPart -> false)

let rec private tryInOrder paymentTerm message (candidate: StoredTemplate) (remaining: StoredTemplate list) =
    match ApplyTemplateWorkflow.applyTemplate paymentTerm candidate.Id candidate.Template message with
    | Ok invoice -> Ok invoice
    | Error lastError ->
        match remaining with
        | [] -> Error lastError
        | next :: rest -> tryInOrder paymentTerm message next rest

/// Filters a supplier's templates to those whose document part this message carries, tries them
/// in stored order, and takes the first that yields every required field. When every applicable
/// template fails, the reported error is the LAST one tried - a real diagnostic, not "nothing
/// worked". supplierId is not part of design.md's listed signature, but NoTemplateForSupplier
/// needs one and an empty template list would otherwise carry none to report - the same gap
/// ApplyTemplateWorkflow's missing TemplateId was.
let selectTemplate
    (paymentTerm: PaymentTermDays)
    (supplierId: SupplierId)
    (templates: StoredTemplate list)
    (message: ScannedMessage)
    : Result<ExtractedInvoice, InvoiceError> =
    let applicable = templates |> List.filter (fun stored -> messageCarries (ValidTemplate.part stored.Template) message)

    match applicable with
    | [] -> Error (NoTemplateForSupplier supplierId)
    | first :: rest -> tryInOrder paymentTerm message first rest
