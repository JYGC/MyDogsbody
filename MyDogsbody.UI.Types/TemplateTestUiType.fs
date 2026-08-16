namespace MyDogsbody.UI.Types

/// What the test panel sends: the template as currently held in the editor - not yet saved, so
/// TemplateUiTypeWithoutId rather than TemplateUiType - plus sample content to run it against.
type TemplateTestInputUiType =
    {
        Template: TemplateUiTypeWithoutId
        SampleText: string
        SampleSubject: string
        SampleAttachmentFilename: string
    }

type FieldTestResultUiType =
    {
        Field: string
        RawValue: string
        ParsedValue: string
        Succeeded: bool
        FailureReason: string
    }

/// NormalizedText is the text after TextNormalization.normalize - the test panel shows exactly
/// what the rules will see (Q7.6.6), not a guess, and it calls the same engine a scan calls
/// rather than reimplementing it.
///
/// PaymentTermDaysApplied is the term a DateFromField rule's arithmetic actually used, which
/// requirements.md asks the panel to show alongside the derived due date. Reported rather than
/// assumed: a derived due date is only meaningful next to the term that produced it, and a
/// term that could not be resolved falls back to 0 - which has to be visible, not silent.
type TemplateTestResultUiType =
    {
        NormalizedText: string
        PaymentTermDaysApplied: int
        FieldResults: FieldTestResultUiType list
    }
