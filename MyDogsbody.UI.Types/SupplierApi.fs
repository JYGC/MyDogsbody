namespace MyDogsbody.UI.Types

open MyDogsbody.Exceptions.Types

/// The whole surface the UI is allowed to reach for suppliers. A record of functions rather than
/// an interface: there is one implementation, built by partial application at startup, and a
/// test substitutes it by building a record literal.
///
/// Writes return unit because a write reloads - the table shows what was stored, not what the
/// dialog held.
type SupplierApi =
    {
        GetAllSuppliers: unit -> Result<SupplierUiType list, MyDogsbodyException>
        AddSupplier: SupplierUiTypeWithoutId -> Result<unit, MyDogsbodyException>
        EditSupplier: SupplierUiType -> Result<unit, MyDogsbodyException>
        DeleteSupplier: string -> Result<unit, MyDogsbodyException>
    }
