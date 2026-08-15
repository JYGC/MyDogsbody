module MyDogsbody.Domain.Invoices.SelectTemplateWorkflow

open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates

let private messageCarries (part: DocumentPart) (message: NormalizedMessage) : bool =
    match part with
    | AnyPart -> true
    | Body -> NormalizedMessage.parts message |> List.exists (fun part -> part.Part = BodyPart)
    | Attachment wantedFormat ->
        NormalizedMessage.parts message
        |> List.exists (fun part ->
            match part.Part with
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

/// Filters a supplier's templates to that supplier's own, and to those whose document part this
/// message carries; tries them in stored Position order; and takes the first that yields every
/// required field. When every applicable template fails, the reported error is the LAST one
/// tried - a real diagnostic, not "nothing worked". supplierId is not part of design.md's listed
/// signature, but NoTemplateForSupplier needs one and an empty template list would otherwise
/// carry none to report - the same gap ApplyTemplateWorkflow's missing TemplateId was.
///
/// Two things this deliberately does not delegate:
///
/// The SORT is this workflow's, not the store's. design.md's diagram for it opens with
/// "templates for supplier, in stored Position order" and ValidTemplate carries Position' for
/// exactly this. A LoadTemplatesForSupplier adapter returning rows in insertion or rowid order
/// would otherwise silently override everything ReorderTemplatesWorkflow exists to let the user
/// configure - and here the order decides WHICH TEMPLATE WINS, not merely what a page displays,
/// so the failure is a wrong-but-plausible invoice from the wrong template. List.sortBy is
/// stable, so templates sharing a position keep the order the dependency returned them in.
///
/// The SUPPLIER is reconciled rather than assumed. ExtractedInvoice.SupplierId comes from the
/// template, so a caller pairing one supplier's id with another supplier's template list used to
/// get an Ok invoice filed under the template's supplier, silently. Templates belonging to anyone
/// else are dropped here, which makes the parameter load-bearing on the success path instead of
/// only in the error case.
let selectTemplate
    (paymentTerm: PaymentTermDays)
    (supplierId: SupplierId)
    (templates: StoredTemplate list)
    (message: ScannedMessage)
    : Result<ExtractedInvoice, InvoiceError> =
    // Once for the message, before any template is tried - not once per candidate inside the
    // loop below.
    let normalized = MessageNormalization.normalizeMessage message

    let applicable =
        templates
        |> List.filter (fun stored -> ValidTemplate.supplierId stored.Template = supplierId)
        |> List.filter (fun stored -> messageCarries (ValidTemplate.part stored.Template) normalized)
        |> List.sortBy (fun stored -> ValidTemplate.position stored.Template)

    match applicable with
    | [] -> Error (NoTemplateForSupplier supplierId)
    | first :: rest -> tryInOrder paymentTerm normalized first rest
