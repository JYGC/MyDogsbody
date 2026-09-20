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

let rec private tryEachTemplateInOrderUntilOneSucceedsReportingTheLastErrorWhenAllFail
    paymentTerm
    message
    (candidate: StoredTemplate)
    (remaining: StoredTemplate list)
    =
    match ApplyTemplateWorkflow.applyTemplate paymentTerm candidate.Id candidate.Template message with
    | Ok invoice -> Ok invoice
    | Error lastError ->
        match remaining with
        | [] -> Error lastError
        | next :: rest ->
            tryEachTemplateInOrderUntilOneSucceedsReportingTheLastErrorWhenAllFail paymentTerm message next rest

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Domain.md - SelectTemplateWorkflow.fs: selectTemplate
let selectTemplate
    (paymentTerm: PaymentTermDays)
    (supplierId: SupplierId)
    (templates: StoredTemplate list)
    (message: ScannedMessage)
    : Result<ExtractedInvoice, InvoiceError> =
    let messageNormalizedOnceBeforeAnyTemplateIsTried = MessageNormalization.normalizeMessage message

    let templatesOfThisSupplierWhoseDocumentPartTheMessageCarriesInStoredPositionOrder =
        templates
        |> List.filter (fun stored -> ValidTemplate.supplierId stored.Template = supplierId)
        |> List.filter (fun stored ->
            messageCarries (ValidTemplate.part stored.Template) messageNormalizedOnceBeforeAnyTemplateIsTried)
        |> List.sortBy (fun stored -> ValidTemplate.position stored.Template)

    match templatesOfThisSupplierWhoseDocumentPartTheMessageCarriesInStoredPositionOrder with
    | [] -> Error (NoTemplateForSupplier supplierId)
    | first :: rest ->
        tryEachTemplateInOrderUntilOneSucceedsReportingTheLastErrorWhenAllFail
            paymentTerm
            messageNormalizedOnceBeforeAnyTemplateIsTried
            first
            rest
