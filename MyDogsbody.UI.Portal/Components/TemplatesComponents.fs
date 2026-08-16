module MyDogsbody.UI.Portal.Components.TemplatesComponents

open System
open Fun.Blazor
open MudBlazor
open FSharp.Data.Adaptive
open MyDogsbody.UI.Types
open MyDogsbody.UI.Types.Module
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Forms

let private targetFields = [ "Reference"; "Amount"; "Currency"; "IssueDate"; "DueDate" ]

/// Mirrors ValidateTemplateWorkflow's own requiredFields - Currency, IssueDate and DueDate are
/// optional. Duplicated here rather than reached from the domain (the UI cannot reference
/// MyDogsbody.Domain), the same way SuppliersComponents mirrors "name must not be empty" for its
/// own Ok-button disable check: the domain still re-validates on submit, this is a UX nicety only.
let private requiredFieldsForTest = [ "Reference"; "Amount" ]

let private missingRequiredFieldsForTest (rules: TemplateFieldRuleUiType list) =
    requiredFieldsForTest |> List.filter (fun field -> rules |> List.forall (fun r -> r.Field <> field))

/// Cheapest and most-used first, RegexCapture last - it fired once in 1,199 measured candidates
/// (design.md -> Testing strategy). This is the order the rule-kind dropdown lists them in.
let private ruleKinds =
    [ "LinesAfterLabel"; "AfterLabel"; "FixedValue"; "AttachmentName"; "SubjectCapture"; "DateFromField"; "RegexCapture" ]

let private hintKinds = [ "AsText"; "AsMoney"; "AsDate" ]
let private documentParts = [ "AnyPart"; "Body"; "Attachment" ]
let private documentFormats = [ "Pdf"; "Word"; "PlainText"; "EmailBody" ]

let private formatMatchers (rules: TemplateFieldRuleUiType list) =
    rules |> List.map (fun r -> $"{r.Field}: {r.RuleKind}") |> String.concat ", "

/// The filename MudFileUpload's FilesChanged just handed over, or "" when the selection was
/// cleared. MudBlazor invokes FilesChanged with default(T) - null for IBrowserFile - on
/// ClearAsync / ResetValueAsync and on the picker's cancel path, so reading .Name straight off it
/// raises inside a Blazor event handler, which unwinds to Shell.fs's ErrorBoundary' and blanks
/// the dialog rather than surfacing an alert.
let attachmentFilenameOf (file: IBrowserFile) : string =
    if isNull (box file) then "" else file.Name

/// The rule kinds whose RuleText is literal text the engine matches against, rather than a
/// pattern or a derivation. Blank text on one of these is what ValidateTemplateWorkflow refuses
/// with RuleTextEmpty; mirrored here so the Add button refuses it before a round trip, the same
/// way missingRequiredFieldsForTest mirrors the domain's own requiredFields.
let private ruleKindsNeedingText = [ "LinesAfterLabel"; "AfterLabel"; "FixedValue"; "AttachmentName"; "SubjectCapture"; "RegexCapture" ]

let templatesBrowser
  (supplierName: string)
  (templatesBrowserModule: TemplatesBrowserModule)
  (showAddTemplateModal: unit -> unit)
  (showEditTemplateModal: TemplateUiType -> unit)
  (confirmAndDeleteTemplate: TemplateUiType -> unit)
  (moveTemplate: TemplateUiType -> int -> unit) =
    fragment {
        adapt {
            let! error = templatesBrowserModule.ErrorAval
            match error with
            | Some message ->
                MudAlert'' {
                    Severity Severity.Error
                    Variant Variant.Filled
                    Dense true
                    message
                }
            | None -> ()
        }
        adapt {
            let! templates = templatesBrowserModule.TemplatesListAval
            let! isLoading = templatesBrowserModule.IsLoadingAval
            let ordered = templates |> List.sortBy (fun t -> t.Position)
            MudTable'' {
                Items ordered
                Breakpoint Breakpoint.Sm
                Loading isLoading
                FixedHeader true
                LoadingProgressColor Color.Info
                Striped true
                Height "80vh"
                NoRecordsContent (fragment {
                    MudText'' { "No templates yet - add one to get started." }
                })
                ToolBarContent (fragment {
                    MudText'' {
                        Typo Typo.h3
                        $"Templates for {supplierName}"
                    }
                    MudSpacer'' {}
                    MudButton'' {
                        Variant Variant.Filled
                        Color Color.Primary
                        EndIcon Icons.Material.Filled.Add
                        OnClick (fun _ -> showAddTemplateModal ())
                        "New Template"
                    }
                })
                HeaderContent (
                    fragment {
                        MudTh'' { "Name" }
                        MudTh'' { "Document part" }
                        MudTh'' { "Rules" }
                        MudTh'' { }
                    }
                )
                RowTemplate (fun (template: TemplateUiType) ->
                    fragment {
                        MudTd'' { $"{template.Name}" }
                        MudTd'' { $"{template.DocumentPart}" }
                        MudTd'' { formatMatchers template.Rules }
                        MudTd'' {
                            MudIconButton'' {
                                Icon Icons.Material.Filled.ArrowUpward
                                OnClick (fun _ -> moveTemplate template -1)
                            }
                            MudIconButton'' {
                                Icon Icons.Material.Filled.ArrowDownward
                                OnClick (fun _ -> moveTemplate template 1)
                            }
                            MudButton'' {
                                Variant Variant.Filled
                                Color Color.Primary
                                OnClick (fun _ -> showEditTemplateModal template)
                                "Edit"
                            }
                            MudButton'' {
                                Variant Variant.Filled
                                Color Color.Error
                                OnClick (fun _ -> confirmAndDeleteTemplate template)
                                "Delete"
                            }
                        }
                    }
                )
            }
        }
    }

type TemplatesEditorDialog() =
    inherit FunComponent()

    [<CascadingParameter>]
    member val private __mudDialogInstance: IMudDialogInstance = null with get, set

    [<Parameter>]
    member val public Title: string = "Add Template" with get, set

    [<Parameter>]
    member val public SupplierId: string = "" with get, set

    [<Parameter>]
    member val public TemplateUiType: TemplateUiTypeWithoutId = {
        SupplierId = ""
        Name = ""
        DocumentPart = "AnyPart"
        AttachmentFormat = ""
        Position = 0
        Rules = [] } with get, set

    [<Parameter>]
    member val public OnTemplateSubmitted: (TemplateUiTypeWithoutId -> unit) = fun _ -> () with get, set

    [<Parameter>]
    member val public TestTemplate: (TemplateTestInputUiType -> Result<TemplateTestResultUiType, MyDogsbody.Exceptions.Types.MyDogsbodyException>) =
        fun _ -> failwith "not wired" with get, set

    override this.Render() =
        let nameCval = cval this.TemplateUiType.Name
        let documentPartCval = cval this.TemplateUiType.DocumentPart
        let attachmentFormatCval = cval this.TemplateUiType.AttachmentFormat
        let rulesCval = cval this.TemplateUiType.Rules

        let newFieldCval = cval "Reference"
        let newRuleKindCval = cval "LinesAfterLabel"
        let newRuleTextCval = cval ""
        let newRuleOffsetCval = cval 1
        let newRuleSourceFieldCval = cval "IssueDate"
        let newHintKindCval = cval "AsText"
        let newHintTextCval = cval ""

        let sampleTextCval = cval ""
        let sampleSubjectCval = cval ""
        let sampleAttachmentFilenameCval = cval ""
        let testResultCval = cval<TemplateTestResultUiType option> None
        let testErrorCval = cval<string option> None

        fragment {
            MudDialog'' {
                TitleContent (fragment {
                    MudText'' {
                        Typo Typo.h6
                        this.Title
                    }
                })
                DialogContent (fragment {
                    MudGrid'' {
                        MudItem'' {
                            xs 12; sm 6; md 6
                            adapt {
                                let! templateName, setName = nameCval.WithSetter()
                                MudTextField'' {
                                    Label "Name"
                                    Variant Variant.Text
                                    Value templateName
                                    Immediate true
                                    ValueChanged setName
                                }
                            }
                        }
                        MudItem'' {
                            xs 12; sm 3; md 3
                            adapt {
                                let! documentPart, setDocumentPart = documentPartCval.WithSetter()
                                MudSelect'' {
                                    Label "Document part"
                                    Variant Variant.Text
                                    Value documentPart
                                    ValueChanged setDocumentPart
                                    fragment {
                                        for part in documentParts do
                                            MudSelectItem'' { Value part; part }
                                    }
                                }
                            }
                        }
                        MudItem'' {
                            xs 12; sm 3; md 3
                            adapt {
                                let! documentPart = documentPartCval
                                if documentPart = "Attachment" then
                                    let! attachmentFormat, setAttachmentFormat = attachmentFormatCval.WithSetter()
                                    MudSelect'' {
                                        Label "Attachment format"
                                        Variant Variant.Text
                                        Value attachmentFormat
                                        ValueChanged setAttachmentFormat
                                        fragment {
                                            for format in documentFormats do
                                                MudSelectItem'' { Value format; format }
                                        }
                                    }
                            }
                        }
                    }

                    MudDivider'' { class' "my-4" }
                    MudText'' { Typo Typo.subtitle1; "Field rules" }

                    adapt {
                        let! rules = rulesCval
                        MudList'' {
                            // Indexed, because a template may legitimately carry more than one
                            // rule sharing the same shape before it is fixed - removing by value
                            // would take every identical rule with it, the same reasoning
                            // SuppliersComponents' matcher list uses.
                            for index, rule in List.indexed rules do
                                MudListItem'' {
                                    MudStack'' {
                                        Row true
                                        // The label goes in the child content, not MudListItem's
                                        // Text - a list item renders one or the other.
                                        let offsetPart = if rule.RuleOffset <> 0 then $" +{rule.RuleOffset}" else ""
                                        let sourcePart = if rule.RuleSourceField <> "" then $" from {rule.RuleSourceField}" else ""
                                        let hintTextPart = if rule.HintText <> "" then $" '{rule.HintText}'" else ""
                                        let label =
                                            $"{rule.Field}: {rule.RuleKind} \"{rule.RuleText}\"{offsetPart}{sourcePart} ({rule.HintKind}{hintTextPart})"
                                        MudText'' { label }
                                        MudIconButton'' {
                                            Icon Icons.Material.Filled.Delete
                                            OnClick (fun _ ->
                                                transact (fun _ -> rulesCval.Value <- rules |> List.removeAt index))
                                        }
                                    }
                                }
                        }
                    }

                    MudGrid'' {
                        MudItem'' {
                            xs 6; sm 2; md 2
                            adapt {
                                let! field, setField = newFieldCval.WithSetter()
                                MudSelect'' {
                                    Label "Field"
                                    Variant Variant.Text
                                    Value field
                                    ValueChanged setField
                                    fragment {
                                        for f in targetFields do
                                            MudSelectItem'' { Value f; f }
                                    }
                                }
                            }
                        }
                        MudItem'' {
                            xs 6; sm 2; md 2
                            adapt {
                                let! ruleKind, setRuleKind = newRuleKindCval.WithSetter()
                                MudSelect'' {
                                    Label "Rule kind"
                                    Variant Variant.Text
                                    Value ruleKind
                                    ValueChanged setRuleKind
                                    fragment {
                                        for ruleKindOption in ruleKinds do
                                            MudSelectItem'' { Value ruleKindOption; ruleKindOption }
                                    }
                                }
                            }
                        }
                        MudItem'' {
                            xs 12; sm 3; md 3
                            adapt {
                                let! ruleKind = newRuleKindCval
                                if ruleKind = "DateFromField" then
                                    let! sourceField, setSourceField = newRuleSourceFieldCval.WithSetter()
                                    MudSelect'' {
                                        Label "Source field"
                                        Variant Variant.Text
                                        Value sourceField
                                        ValueChanged setSourceField
                                        fragment {
                                            for f in targetFields do
                                                MudSelectItem'' { Value f; f }
                                        }
                                    }
                                else
                                    let! ruleText, setRuleText = newRuleTextCval.WithSetter()
                                    MudTextField'' {
                                        Label "Label / pattern / value"
                                        Variant Variant.Text
                                        Value ruleText
                                        Immediate true
                                        ValueChanged setRuleText
                                    }
                            }
                        }
                        MudItem'' {
                            xs 6; sm 2; md 2
                            adapt {
                                let! ruleKind = newRuleKindCval
                                if ruleKind = "LinesAfterLabel" then
                                    let! offset, setOffset = newRuleOffsetCval.WithSetter()
                                    MudNumericField'' {
                                        Label "Offset"
                                        Variant Variant.Text
                                        Value offset
                                        Min 0
                                        Max 20
                                        Immediate true
                                        ValueChanged setOffset
                                    }
                            }
                        }
                        MudItem'' {
                            xs 6; sm 2; md 2
                            adapt {
                                let! hintKind, setHintKind = newHintKindCval.WithSetter()
                                MudSelect'' {
                                    Label "Parse as"
                                    Variant Variant.Text
                                    Value hintKind
                                    ValueChanged setHintKind
                                    fragment {
                                        for hint in hintKinds do
                                            MudSelectItem'' { Value hint; hint }
                                    }
                                }
                            }
                        }
                        MudItem'' {
                            xs 6; sm 2; md 2
                            adapt {
                                let! hintKind = newHintKindCval
                                if hintKind <> "AsText" then
                                    let! hintText, setHintText = newHintTextCval.WithSetter()
                                    MudTextField'' {
                                        Label (if hintKind = "AsMoney" then "Decimal separator" else "Date format")
                                        Variant Variant.Text
                                        Value hintText
                                        Immediate true
                                        ValueChanged setHintText
                                    }
                            }
                        }
                        MudItem'' {
                            xs 12; sm 1; md 1
                            adapt {
                                let! field = newFieldCval
                                let! ruleKind = newRuleKindCval
                                let! ruleText = newRuleTextCval
                                let! offset = newRuleOffsetCval
                                let! sourceField = newRuleSourceFieldCval
                                let! hintKind = newHintKindCval
                                let! hintText = newHintTextCval
                                MudIconButton'' {
                                    Icon Icons.Material.Filled.Add
                                    Disabled (List.contains ruleKind ruleKindsNeedingText && String.IsNullOrWhiteSpace ruleText)
                                    OnClick (fun _ ->
                                        let newRule: TemplateFieldRuleUiType =
                                            {
                                                Field = field
                                                RuleKind = ruleKind
                                                RuleText = if ruleKind = "DateFromField" then "" else ruleText
                                                RuleOffset = if ruleKind = "LinesAfterLabel" then offset else 0
                                                RuleSourceField = if ruleKind = "DateFromField" then sourceField else ""
                                                HintKind = hintKind
                                                HintText = if hintKind = "AsText" then "" else hintText
                                            }
                                        transact (fun _ ->
                                            rulesCval.Value <- rulesCval.Value @ [ newRule ]
                                            newRuleTextCval.Value <- ""))
                                }
                            }
                        }
                    }

                    MudDivider'' { class' "my-4" }
                    MudText'' { Typo Typo.subtitle1; "Test panel" }
                    MudText'' {
                        Typo Typo.body2
                        "Paste sample text, subject or an attachment filename, then run the template against it - the same engine a scan uses."
                    }
                    MudGrid'' {
                        MudItem'' {
                            xs 12; sm 6; md 6
                            adapt {
                                let! sampleText, setSampleText = sampleTextCval.WithSetter()
                                MudTextField'' {
                                    Label "Sample text"
                                    Variant Variant.Text
                                    Lines 6
                                    Value sampleText
                                    Immediate true
                                    ValueChanged setSampleText
                                }
                            }
                        }
                        MudItem'' {
                            xs 12; sm 3; md 3
                            adapt {
                                let! sampleSubject, setSampleSubject = sampleSubjectCval.WithSetter()
                                MudTextField'' {
                                    Label "Sample subject"
                                    Variant Variant.Text
                                    Value sampleSubject
                                    Immediate true
                                    ValueChanged setSampleSubject
                                }
                            }
                        }
                        MudItem'' {
                            xs 12; sm 3; md 3
                            adapt {
                                let! sampleAttachmentFilename = sampleAttachmentFilenameCval
                                // AttachmentName matches a filename, not file content (see
                                // toTestMessage in TemplateApiFactory.fs) - so only the browser's
                                // IBrowserFile.Name is read here, never the file's bytes. WebView2
                                // backs BlazorWebView, so this opens the real Windows file picker
                                // with no extra JS interop needed.
                                MudFileUpload'' {
                                    FilesChanged (fun (file: IBrowserFile) ->
                                        transact (fun _ -> sampleAttachmentFilenameCval.Value <- attachmentFilenameOf file))
                                    ActivatorContent (fragment {
                                        MudButton'' {
                                            Variant Variant.Filled
                                            StartIcon Icons.Material.Filled.AttachFile
                                            if String.IsNullOrWhiteSpace sampleAttachmentFilename then
                                                "Choose attachment file"
                                            else
                                                sampleAttachmentFilename
                                        }
                                    })
                                }
                            }
                        }
                        MudItem'' {
                            xs 12
                            adapt {
                                let! templateName = nameCval
                                let! documentPart = documentPartCval
                                let! attachmentFormat = attachmentFormatCval
                                let! rules = rulesCval
                                let! sampleText = sampleTextCval
                                let! sampleSubject = sampleSubjectCval
                                let! sampleAttachmentFilename = sampleAttachmentFilenameCval
                                let missingRequiredFields = missingRequiredFieldsForTest rules
                                MudButton'' {
                                    Variant Variant.Filled
                                    Color Color.Secondary
                                    Disabled (not (List.isEmpty missingRequiredFields))
                                    OnClick (fun _ ->
                                        let input: TemplateTestInputUiType =
                                            {
                                                Template =
                                                    {
                                                        SupplierId = this.SupplierId
                                                        Name = templateName
                                                        DocumentPart = documentPart
                                                        AttachmentFormat = attachmentFormat
                                                        Position = 0
                                                        Rules = rules
                                                    }
                                                SampleText = sampleText
                                                SampleSubject = sampleSubject
                                                SampleAttachmentFilename = sampleAttachmentFilename
                                            }
                                        match this.TestTemplate input with
                                        | Ok result ->
                                            transact (fun _ ->
                                                testResultCval.Value <- Some result
                                                testErrorCval.Value <- None)
                                        | Error ex ->
                                            transact (fun _ ->
                                                testResultCval.Value <- None
                                                testErrorCval.Value <- Some ex.Message))
                                    "Run test"
                                }
                                if not (List.isEmpty missingRequiredFields) then
                                    MudText'' {
                                        Typo Typo.caption
                                        Color Color.Warning
                                        $"""Add a rule for: {String.concat ", " missingRequiredFields} before testing."""
                                    }
                            }
                        }
                        MudItem'' {
                            xs 12
                            adapt {
                                let! testError = testErrorCval
                                match testError with
                                | Some message ->
                                    MudAlert'' { Severity Severity.Error; message }
                                | None -> ()
                            }
                        }
                        MudItem'' {
                            xs 12
                            adapt {
                                let! testResult = testResultCval
                                let! rawSampleText = sampleTextCval
                                match testResult with
                                | Some result ->
                                    fragment {
                                        // Q7.6.6: the raw text stays visible alongside the
                                        // normalized text a rule actually sees, so a mismatch
                                        // (e.g. a letter-spaced heading normalization cannot
                                        // repair) is diagnosable rather than a silent guess.
                                        MudText'' { Typo Typo.caption; "Raw text:" }
                                        MudPaper'' { class' "pa-2"; rawSampleText }
                                        MudText'' { Typo Typo.caption; "Normalized text:" }
                                        MudPaper'' { class' "pa-2"; result.NormalizedText }
                                        MudSimpleTable'' {
                                            fragment {
                                                thead {
                                                    tr {
                                                        th { "Field" }
                                                        th { "Result" }
                                                    }
                                                }
                                                tbody {
                                                    for fieldResult in result.FieldResults do
                                                        tr {
                                                            td { fieldResult.Field }
                                                            td {
                                                                if fieldResult.Succeeded then
                                                                    fieldResult.ParsedValue
                                                                else
                                                                    $"Failed: {fieldResult.FailureReason}"
                                                            }
                                                        }
                                                }
                                            }
                                        }
                                    }
                                | None -> ()
                            }
                        }
                    }
                })
                DialogActions (fragment {
                    MudButton'' {
                        OnClick (fun _ -> this.__mudDialogInstance.Cancel() |> ignore)
                        "Cancel"
                    }
                    adapt {
                        let! templateName = nameCval
                        let! documentPart = documentPartCval
                        let! attachmentFormat = attachmentFormatCval
                        let! rules = rulesCval
                        let disableOkButton = String.IsNullOrWhiteSpace templateName
                        MudButton'' {
                            Disabled disableOkButton
                            Color Color.Primary
                            OnClick (fun _ ->
                                this.OnTemplateSubmitted
                                    {
                                        SupplierId = this.SupplierId
                                        Name = templateName
                                        DocumentPart = documentPart
                                        AttachmentFormat = attachmentFormat
                                        Position = this.TemplateUiType.Position
                                        Rules = rules
                                    }
                                this.__mudDialogInstance.Close() |> ignore)
                            "Ok"
                        }
                    }
                })
            }
        }

let showTemplatesEditorDialog
  (dialogService: IDialogService)
  (dialogTitle: string)
  (supplierId: string)
  (onTemplateSubmitted: TemplateUiTypeWithoutId -> unit)
  (testTemplate: TemplateTestInputUiType -> Result<TemplateTestResultUiType, MyDogsbody.Exceptions.Types.MyDogsbodyException>)
  (templateOption: TemplateUiTypeWithoutId option) =
    let options = DialogOptions(CloseOnEscapeKey = false, BackdropClick = false, FullWidth = true, MaxWidth = MaxWidth.Large)
    let parameters = DialogParameters<TemplatesEditorDialog>()
    parameters.Add("Title", dialogTitle)
    parameters.Add("SupplierId", supplierId)
    parameters.Add("OnTemplateSubmitted", onTemplateSubmitted)
    parameters.Add("TestTemplate", testTemplate)
    if templateOption.IsSome then
        parameters.Add("TemplateUiType", templateOption.Value)
    dialogService.ShowAsync<TemplatesEditorDialog>(dialogTitle, parameters, options)
