namespace MyDogsbody.UI.Types

open MyDogsbody.Exceptions.Types

/// The whole surface the UI is allowed to reach for invoice templates. A record of functions
/// rather than an interface: there is one implementation, built by partial application at
/// startup, and a test substitutes it by building a record literal.
///
/// Writes return unit because a write reloads - the table shows what was stored, not what the
/// dialog held. TestTemplate is the exception: it never writes, so it returns the result the
/// test panel renders directly.
type TemplateApi =
    {
        GetTemplatesForSupplier: string -> Result<TemplateUiType list, MyDogsbodyException>
        AddTemplate: TemplateUiTypeWithoutId -> Result<unit, MyDogsbodyException>
        EditTemplate: TemplateUiType -> Result<unit, MyDogsbodyException>
        DeleteTemplate: string -> Result<unit, MyDogsbodyException>
        ReorderTemplates: string -> string list -> Result<unit, MyDogsbodyException>
        TestTemplate: TemplateTestInputUiType -> Result<TemplateTestResultUiType, MyDogsbodyException>
    }
