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
/// Not private: TestTemplate never touches storage, so TemplateApiContractTests.fs's fake reuses
/// this rather than duplicating it - there is no real-vs-fake split to test for a pure function.
let splitPastedTextIntoLines (text: string) : TextLine list =
    let rawLines = text.Replace("\r\n", "\n").Split '\n' |> Array.toList

    rawLines
    |> List.fold
        (fun (blockIndex, previousWasBlank, acc) lineText ->
            let isBlank = String.IsNullOrWhiteSpace lineText
            let nextBlockIndex = if isBlank && not previousWasBlank then blockIndex + 1 else blockIndex
            nextBlockIndex, isBlank, { Text = lineText; BlockIndex = nextBlockIndex } :: acc)
        (0, false, [])
    |> fun (_, _, acc) -> List.rev acc

/// Builds the ScannedMessage TestTemplate applies against: the pasted text as the body, the
/// pasted subject, and - only when a filename was given - a single attachment part carrying that
/// name and no text of its own (AttachmentName matches a filename, not content).
let toTestMessage (part: DocumentPart) (input: TemplateTestInputUiType) : ScannedMessage =
    let pastedLines = splitPastedTextIntoLines input.SampleText

    // The pasted text has to hang off whichever part the template actually selects. An
    // Attachment template's selector filters BodyPart out (ApplyTemplateWorkflow.selectContent),
    // so leaving the text only on the body meant the engine ran against no lines at all while
    // the panel still rendered the normalized text - the exact "silent guess" Q7.6.6's raw vs
    // normalized display exists to prevent. Exactly one part ever carries the lines, so AnyPart -
    // which selects both body and attachment - never sees them twice.
    let attachmentParts =
        match part with
        | Attachment format -> [ AttachmentPart(input.SampleAttachmentFilename, format), pastedLines ]
        | Body
        | AnyPart ->
            if String.IsNullOrWhiteSpace input.SampleAttachmentFilename then
                []
            else
                [ AttachmentPart(input.SampleAttachmentFilename, Pdf), [] ]

    let bodyLines =
        match part with
        | Attachment _ -> []
        | Body
        | AnyPart -> pastedLines

    {
        SourceMessageId = SourceMessageId.create "test-panel" |> Result.defaultWith (fun _ -> failwith "unreachable: constant id")
        Sender = ""
        Subject = input.SampleSubject
        ReceivedAt = DateTime.Now
        Parts = (BodyPart, bodyLines) :: attachmentParts
    }

/// One row of the test panel.
///
/// applyTemplate returns a single Result and stops at the first field that fails, so an error
/// names exactly one field at fault. Reporting that error against all five rows would blame
/// fields that actually matched - so only the named field carries the failure, and the rest say
/// they were not reached. Nothing is rendered from `string error`: that prints the union case
/// with its constructor syntax and the placeholder TemplateId, which is developer output rather
/// than a sentence.
///
/// Known gap, deliberately left: RawValue repeats ParsedValue on the Ok path, so the raw-vs-parsed
/// distinction FieldTestResultUiType names is not actually populated. Populating it truthfully
/// needs ExtractedInvoice to carry each field's pre-parse text, which is a MyDogsbody.Domain
/// change belonging with change #4 rather than to this UI slice. Nothing renders RawValue today.
let toFieldTestResult (extracted: Result<ExtractedInvoice, InvoiceError>) (field: TargetField) : FieldTestResultUiType =
    match extracted with
    | Error error ->
        let failureReason =
            match TemplateApiMappers.invoiceErrorField error with
            | Some blamed when blamed = field -> TemplateApiMappers.toInvoiceErrorMessage error
            | Some blamed -> $"Not evaluated: the run stopped at {TemplateApiMappers.toTargetFieldUiString blamed}."
            | None -> TemplateApiMappers.toInvoiceErrorMessage error

        { Field = TemplateApiMappers.toTargetFieldUiString field
          RawValue = ""
          ParsedValue = ""
          Succeeded = false
          FailureReason = failureReason }
    | Ok invoice ->
        let parsedText, succeeded, failure =
            match field with
            | Reference -> invoice.Reference, true, ""
            | Amount -> string invoice.Amount, true, ""
            | Currency ->
                match invoice.Currency with
                | Some currency -> currency, true, ""
                | None -> "", false, "No value extracted."
            | IssueDate ->
                match invoice.IssueDate with
                | Some date -> date.ToString "yyyy-MM-dd", true, ""
                | None -> "", false, "No value extracted."
            | DueDate ->
                match invoice.DueDate with
                | Some date -> date.ToString "yyyy-MM-dd", true, ""
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
        // - over pasted text, never writing anything. A payment term of 0 is a placeholder: the
        // test panel has no supplier context of its own to read one from (Q7.6.6 asks only that
        // normalized text and per-field results are shown, not that DateFromField's arithmetic
        // uses a real term here).
        TestTemplate =
            fun input ->
                result {
                    let! validated =
                        input.Template
                        |> TemplateApiMappers.toUnvalidatedTemplate
                        |> Result.bind ValidateTemplateWorkflow.validateTemplate
                        |> Result.mapError (toException ActionNames.MyDogsbody.Startup.TemplateApi.testTemplate)

                    let message = toTestMessage (ValidTemplate.part validated) input
                    let noTerm = PaymentTermDays.create 0 |> Result.defaultWith (fun _ -> failwith "unreachable: 0 is always in range")
                    let placeholderId = TemplateId.create "test" |> Result.defaultWith (fun _ -> failwith "unreachable: constant id")

                    let extracted = ApplyTemplateWorkflow.applyTemplate noTerm placeholderId validated message
                    let normalizedText =
                        TextNormalization.normalize (splitPastedTextIntoLines input.SampleText)
                        |> List.map (fun line -> line.Text)
                        |> String.concat "\n"

                    return
                        {
                            NormalizedText = normalizedText
                            FieldResults = [ Reference; Amount; Currency; IssueDate; DueDate ] |> List.map (toFieldTestResult extracted)
                        }
                }
    }
