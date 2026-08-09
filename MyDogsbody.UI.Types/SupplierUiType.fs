namespace MyDogsbody.UI.Types

type SupplierMatcherUiType =
    {
        Kind: string
        Value: string
    }

type SupplierUiType =
    {
        Id: string
        Name: string
        PaymentTermDays: int
        Matchers: SupplierMatcherUiType list
    }

type SupplierUiTypeWithoutId =
    {
        Name: string
        PaymentTermDays: int
        Matchers: SupplierMatcherUiType list
    }
