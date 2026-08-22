namespace MyDogsbody.UI.Types

/// A rule attached to one target field, in the same split-column shape TemplateFieldRuleRecord
/// persists. Like every other UI.Types record this uses plain string/int rather than option -
/// Fun.Blazor bindings work with plain values - so whichever payload a rule kind does not carry
/// is left as an empty string or zero rather than None.
type TemplateFieldRuleUiType =
    {
        Field: string
        RuleKind: string
        RuleText: string
        RuleOffset: int
        RuleSourceField: string
        HintKind: string
        HintText: string
    }

type TemplateUiType =
    {
        Id: string
        SupplierId: string
        Name: string
        DocumentPart: string
        AttachmentFormat: string
        Position: int
        Rules: TemplateFieldRuleUiType list
    }

type TemplateUiTypeWithoutId =
    {
        SupplierId: string
        Name: string
        DocumentPart: string
        AttachmentFormat: string
        Position: int
        Rules: TemplateFieldRuleUiType list
    }
