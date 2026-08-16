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

/// Splits pasted test-panel text into TextLines the way any plain-text reader would: one line
/// per newline, BlockIndex incrementing each time a run of blank lines is crossed - "plain text
/// splits on blank lines" (Documents/DocumentsTypes.fs's own TextLine doc comment). The blank
/// lines themselves are still emitted; TextNormalization.normalize drops them later, the same way
/// it would for any other reader's output. Pure, no mutable state.
///
/// Null degrades to empty, the same way TextNormalization.normalizeText's own first line does and
/// for the same reason: a cleared MudTextField hands a bound `string` back as null, running the
/// panel before pasting anything is the first thing a user does, and String.Replace on null raised
/// NullReferenceException out of an API whose type promises a Result - on a path the UI calls from
/// Async.Start, where neither an alert nor the log would ever see it. Empty text yields one blank
/// line, which normalization drops, so every rule reports finding nothing rather than the panel
/// disappearing.
let private splitPastedTextIntoLines (text: string) : TextLine list =
    let safeText = if isNull text then "" else text
    let rawLines = safeText.Replace("\r\n", "\n").Split '\n' |> Array.toList

    rawLines
    |> List.fold
        (fun (blockIndex, previousWasBlank, acc) lineText ->
            let isBlank = String.IsNullOrWhiteSpace lineText
            let nextBlockIndex = if isBlank && not previousWasBlank then blockIndex + 1 else blockIndex
            nextBlockIndex, isBlank, { Text = lineText; BlockIndex = nextBlockIndex } :: acc)
        (0, false, [])
    |> fun (_, _, acc) -> List.rev acc

/// Builds the ScannedMessage TestTemplate applies against: the pasted subject, the sample
/// filename, and the pasted text ON THE PART THE TEMPLATE ACTUALLY READS.
///
/// Which part that is, is not cosmetic. ApplyTemplateWorkflow.partMatchesSelector gives an
/// Attachment-scoped template no sight of BodyPart at all, so pasted text parked on the body made
/// the panel report every field of a perfectly sound template as unmatched - and a PDF attached to
/// an email is the primary case this change exists for, so that is the template most likely to be
/// tested and the one the panel was least able to answer. Body and AnyPart both read the body, so
/// only the Attachment case moves.
///
/// An Attachment-scoped template gets its part whether or not a filename was supplied: the
/// filename is what an AttachmentName rule reads, not what makes the document exist. Under Body
/// and AnyPart the attachment part is still text-free and still appears only when a filename was
/// given - AttachmentName matches a filename, not content, and duplicating the sample text onto a
/// second part AnyPart already selects would offer the same label twice.
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
/// ValidateTemplateWorkflow.validateDateFormat and TemplateApiMappers.toFieldFailureReason both
/// already pin it. This was the one date rendering left ambient. DateTime.ToString resolves
/// "yyyy" against CultureInfo.CurrentCulture's CALENDAR, so a measured issue date of 4 March 2026
/// printed 2569-03-04 under th-TH and 1447-09-15 under ar-SA: the panel showing the author a date
/// their template never extracted, on the one screen the whole change exists to let them check it
/// against, with nothing to notice it by.
let private toIsoDate (date: DateTime) : string =
    date.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)

let private toFieldTestResult (extracted: Result<ExtractedInvoice, InvoiceError>) (field: TargetField) : FieldTestResultUiType =
    match extracted with
    | Error error ->
        { Field = TemplateApiMappers.toTargetFieldUiString field
          RawValue = ""
          ParsedValue = ""
          Succeeded = false
          FailureReason = TemplateApiMappers.toFieldFailureReason error }
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
            |> Result.mapError (fun ex -> TemplateStoreFailed ex.Message)

    // Outbound: the workflow's domain error becomes the exception the UI renders.
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

        // Runs the same engine a scan calls - ValidateTemplateWorkflow then ApplyTemplateWorkflow
        // - over pasted text, never writing anything.
        //
        // The payment term is the SUPPLIER'S, read through the same dependency AddTemplate uses.
        // It was a hard-coded 0, on the stated grounds that "the test panel has no supplier context
        // of its own to read one from" - which is not so: the input carries the SupplierId, and
        // loadSuppliersForTemplates is bound right here. With 0, DateFromField derived a due date
        // equal to its source and reported it as a SUCCESS, so the panel showed the author a due
        // date their template will never produce, on the one screen the change exists to let them
        // check extraction against, and for the single rule requirements.md calls "the rule that
        // carries extraction from 12% to 39%". requirements.md is explicit in both directions:
        // the derivation adds "the SUPPLIER'S payment term", and testing the template must PROVE
        // DateFromField produces the due date its documents never state.
        //
        // Reading the term means the supplier has to exist, so an unknown one is reported the way
        // AddTemplate reports it rather than silently testing against an invented term. Still no
        // write - this reads suppliers and stores nothing.
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

                    // Only the fields the template actually carries a rule for. IssueDate and
                    // DueDate are optional - ValidateTemplateWorkflow requires a rule only for
                    // Reference, Amount and Currency - so a template that declares no date rule
                    // has not failed to extract a date, it was never asked for one. Reporting the
                    // absent fields anyway told the author that two fields of a sound template
                    // were broken, which is the same false negative the attachment-part fix
                    // closed, arriving from the other direction.
                    //
                    // The ORDER stays the engine's fixed evaluation order rather than the rule
                    // list's, so the panel reads the same way whatever order the rules were
                    // entered in; only the fields with no rule drop out.
                    let ruledFields = ValidTemplate.rules validated |> List.map (fun rule -> rule.Field)

                    return
                        {
                            NormalizedText = normalizedText
                            FieldResults =
                                [ Reference; Amount; Currency; IssueDate; DueDate ]
                                |> List.filter (fun field -> List.contains field ruledFields)
                                |> List.map (toFieldTestResult extracted)
                        }
                }
    }
