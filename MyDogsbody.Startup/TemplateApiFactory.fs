/// Where the abstract meets the real: the adapters that satisfy each InvoiceTemplates dependency
/// function type, the workflows partially applied over them, and the translation between the two
/// error types.
///
/// This is the only place that knows both the main SQLite database and the domain, and the only
/// place the two error types meet. Dependencies are leading parameters, so a test supplies a temp
/// database context; no module-level bindings, so nothing opens a file on import.
module MyDogsbody.Startup.TemplateApiFactory

open System
open MyDogsbody.Builders
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domain
open MyDogsbody.Domain.Documents
open MyDogsbody.Domain.Suppliers
open MyDogsbody.Domain.InvoiceTemplates
open MyDogsbody.Domain.Invoices
open MyDogsbody.Database
open MyDogsbody.UI.Types

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Startup.md - TemplateApiFactory.fs: splitPastedTextIntoLines
let private splitPastedTextIntoLines (text: string) : TextLine list =
    let safeText = if isNull text then "" else text
    let rawLines = safeText.Replace("\r\n", "\n").Split '\n' |> Array.toList

    rawLines
    |> List.fold
        (fun (blockIndex, previousWasBlank, accumulatedLines) lineText ->
            let isBlank = String.IsNullOrWhiteSpace lineText
            let nextBlockIndex = if isBlank && not previousWasBlank then blockIndex + 1 else blockIndex
            nextBlockIndex, isBlank, { Text = lineText; BlockIndex = nextBlockIndex } :: accumulatedLines)
        (0, false, [])
    |> fun (_, _, accumulatedLines) -> List.rev accumulatedLines

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Startup.md - TemplateApiFactory.fs: toTestMessage
let private toTestMessage (part: DocumentPart) (input: TemplateTestInputUiType) : ScannedMessage =
    let sampleLines = splitPastedTextIntoLines input.SampleText
    let attachmentFormat = match part with | Attachment format -> format | Body | AnyPart -> Pdf

    let bodyLines, attachmentParts =
        match part with
        | Attachment _ -> [], [ AttachmentPart(input.SampleAttachmentFilename, attachmentFormat), sampleLines ]
        | Body
        | AnyPart ->
            let namedAttachment =
                if String.IsNullOrWhiteSpace input.SampleAttachmentFilename then
                    []
                else
                    [ AttachmentPart(input.SampleAttachmentFilename, attachmentFormat), [] ]

            sampleLines, namedAttachment

    {
        SourceMessageId = SourceMessageId.create "test-panel" |> Result.defaultWith (fun _ -> failwith "unreachable: constant id")
        Sender = ""
        Subject = input.SampleSubject
        ReceivedAt = DateTime.Now
        Parts = (BodyPart, bodyLines) :: attachmentParts
    }

/// ISO, InvariantCulture, never the ambient one - ParseHint.AsDate carries the rule in its own
/// declaration ("explicit. NEVER DateTime.Parse with ambient culture"), and
/// ValidateTemplateWorkflow.validateAsDateFormatByParsingBackTheDateItWritesNotMerelyByFormatting
/// and TemplateApiMappers.toFieldFailureReason both already pin it. This was the one date
/// rendering left ambient. DateTime.ToString resolves
/// "yyyy" against CultureInfo.CurrentCulture's CALENDAR, so a measured issue date of 4 March 2026
/// printed 2569-03-04 under th-TH and 1447-09-15 under ar-SA: the panel showing the author a date
/// their template never extracted, on the one screen the whole change exists to let them check it
/// against, with nothing to notice it by.
let private toIsoDate (date: DateTime) : string =
    date.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)

/// The rule the run faulted on, handed to the mapper so the sentence can name the input that rule
/// reads rather than assuming the pasted sample text - SubjectCapture reads the subject,
/// AttachmentName the filename, and FixedValue nothing at all. The rules are the validated
/// template's own, so this is a lookup rather than a search for something that might be absent.
let private ruleFor (rules: TemplateFieldRule list) (field: TargetField) : FieldRule option =
    rules |> List.tryFind (fun rule -> rule.Field = field) |> Option.map (fun rule -> rule.Rule)

/// Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Startup.md - TemplateApiFactory.fs: theReasonThisFieldsRowCarriesOutOfTheSingleErrorAWholeRunHandsBack
let private theReasonThisFieldsRowCarriesOutOfTheSingleErrorAWholeRunHandsBack
    (rules: TemplateFieldRule list)
    (field: TargetField)
    (error: InvoiceError)
    : string =
    match TemplateApiMappers.toFailingField error with
    | Some failing when failing <> field ->
        $"Not reported: the run stopped at {TemplateApiMappers.toTargetFieldUiString failing}."
    | Some failing -> TemplateApiMappers.toFieldFailureReason (ruleFor rules failing) error
    | None -> TemplateApiMappers.toFieldFailureReason None error

let private toFieldTestResult
    (rules: TemplateFieldRule list)
    (extracted: Result<ExtractedInvoice, InvoiceError>)
    (field: TargetField)
    : FieldTestResultUiType =
    match extracted with
    | Error error ->
        { Field = TemplateApiMappers.toTargetFieldUiString field
          RawValue = ""
          ParsedValue = ""
          Succeeded = false
          FailureReason = theReasonThisFieldsRowCarriesOutOfTheSingleErrorAWholeRunHandsBack rules field error }
    | Ok invoice ->
        let parsedText, succeeded, failure =
            match field with
            | Reference -> invoice.Reference, true, ""
            | Amount -> string invoice.Amount, true, ""
            | Currency -> invoice.Currency, true, ""
            | IssueDate ->
                match invoice.IssueDate with
                | Some date -> toIsoDate date, true, ""
                | None -> "", false, "No value extracted."
            | DueDate ->
                match invoice.DueDate with
                | Some date -> toIsoDate date, true, ""
                | None -> "", false, "No value extracted."

        { Field = TemplateApiMappers.toTargetFieldUiString field
          RawValue = parsedText
          ParsedValue = parsedText
          Succeeded = succeeded
          FailureReason = failure }

let createTemplateApi (handleError: HandleErrorBuilder) (databaseContext: DatabaseContext) : TemplateApi =

    // Inbound: an adapter becomes a dependency. The store speaks MyDogsbodyException; the
    // workflow is handed a function that speaks TemplateError.
    let loadTemplatesForSupplier: LoadTemplatesForSupplier =
        fun supplierId ->
            TemplateStore.getForSupplier
                handleError
                databaseContext.GetDatabaseConnection
                databaseContext.GetInvoiceTemplates
                databaseContext.GetTemplateFieldRules
                supplierId
            |> Result.mapError TemplateApiMappers.toTemplateError

    let saveTemplate: SaveTemplate =
        fun template ->
            TemplateStore.insertOne
                handleError
                databaseContext.GetDatabaseConnection
                databaseContext.GetInvoiceTemplates
                databaseContext.GetTemplateFieldRules
                template
            |> Result.mapError TemplateApiMappers.toTemplateError

    let updateTemplate: UpdateTemplate =
        fun templateId template ->
            TemplateStore.updateOne
                handleError
                databaseContext.GetDatabaseConnection
                databaseContext.GetInvoiceTemplates
                databaseContext.GetTemplateFieldRules
                templateId
                template
            |> Result.mapError TemplateApiMappers.toTemplateError

    let deleteTemplateDependency: DeleteTemplate =
        fun id ->
            TemplateStore.deleteOne handleError databaseContext.GetDatabaseConnection databaseContext.GetInvoiceTemplates id
            |> Result.mapError TemplateApiMappers.toTemplateError

    let reorderTemplatesDependency: ReorderTemplates =
        fun supplierId templateIds ->
            TemplateStore.reorder
                handleError
                databaseContext.GetDatabaseConnection
                databaseContext.GetInvoiceTemplates
                supplierId
                templateIds
            |> Result.mapError TemplateApiMappers.toTemplateError

    let loadSuppliersForTemplates: LoadSuppliersForTemplates =
        fun () ->
            SupplierStore.getAll
                handleError
                databaseContext.GetDatabaseConnection
                databaseContext.GetSuppliers
                databaseContext.GetSupplierMatchers
                ()
            |> Result.mapError (fun caughtException -> TemplateStoreFailed caughtException.Message)

    let toException = TemplateApiMappers.toMyDogsbodyException

    {
        GetTemplatesForSupplier =
            fun supplierIdString ->
                ListTemplatesWorkflow.listTemplates loadTemplatesForSupplier supplierIdString
                |> Result.map (List.map TemplateApiMappers.toUiType)
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.TemplateApi.getTemplatesForSupplier)

        AddTemplate =
            fun uiType ->
                uiType
                |> TemplateApiMappers.toUnvalidatedTemplate
                |> Result.bind (AddTemplateWorkflow.addTemplate loadSuppliersForTemplates loadTemplatesForSupplier saveTemplate)
                |> Result.map ignore
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.TemplateApi.addTemplate)

        EditTemplate =
            fun uiType ->
                uiType
                |> TemplateApiMappers.toUnvalidatedTemplateEdit
                |> Result.bind (fun (id, unvalidated) ->
                    EditTemplateWorkflow.editTemplate loadTemplatesForSupplier updateTemplate id unvalidated)
                |> Result.map ignore
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.TemplateApi.editTemplate)

        DeleteTemplate =
            fun idString ->
                DeleteTemplateWorkflow.deleteTemplate deleteTemplateDependency idString
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.TemplateApi.deleteTemplate)

        ReorderTemplates =
            fun supplierIdString templateIdStrings ->
                ReorderTemplatesWorkflow.reorderTemplates
                    loadTemplatesForSupplier
                    reorderTemplatesDependency
                    supplierIdString
                    templateIdStrings
                |> Result.mapError (toException ActionNames.MyDogsbody.Startup.TemplateApi.reorderTemplates)

        // Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Startup.md - TemplateApiFactory.fs: TestTemplate =
        TestTemplate =
            fun input ->
                let testTemplateAction = ActionNames.MyDogsbody.Startup.TemplateApi.testTemplate

                result {
                    let! validated =
                        input.Template
                        |> TemplateApiMappers.toUnvalidatedTemplate
                        |> Result.bind ValidateTemplateWorkflow.validateTemplate
                        |> Result.mapError (toException testTemplateAction)

                    let supplierId = ValidTemplate.supplierId validated

                    let! paymentTerm =
                        loadSuppliersForTemplates ()
                        |> Result.bind (fun suppliers ->
                            match suppliers |> List.tryFind (fun supplier -> supplier.Id = supplierId) with
                            | Some supplier -> Ok supplier.PaymentTermDays
                            | None -> Error (TemplateSupplierNotFound supplierId))
                        |> Result.mapError (toException testTemplateAction)

                    let message = toTestMessage (ValidTemplate.part validated) input
                    let placeholderId = TemplateId.create "test" |> Result.defaultWith (fun _ -> failwith "unreachable: constant id")

                    // normalizeMessage is the only door to NormalizedMessage, and applyTemplate
                    // takes one - so the panel runs the same normalization a scan runs, once,
                    // rather than handing the engine raw text.
                    let extracted =
                        ApplyTemplateWorkflow.applyTemplate paymentTerm placeholderId validated (MessageNormalization.normalizeMessage message)
                    let normalizedText =
                        TextNormalization.normalize (splitPastedTextIntoLines input.SampleText)
                        |> List.map (fun line -> line.Text)
                        |> String.concat "\n"

                    // Rationale: docs/changes/comments-to-names/rationale/MyDogsbody.Startup.md - TemplateApiFactory.fs: rules
                    let rules = ValidTemplate.rules validated
                    let ruledFields = rules |> List.map (fun rule -> rule.Field)

                    return
                        {
                            NormalizedText = normalizedText
                            FieldResults =
                                [ Reference; Amount; Currency; IssueDate; DueDate ]
                                |> List.filter (fun field -> List.contains field ruledFields)
                                |> List.map (toFieldTestResult rules extracted)
                        }
                }
    }
