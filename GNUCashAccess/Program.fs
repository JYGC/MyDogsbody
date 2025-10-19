open System
open System.IO
open System.Text
open FSharp.Data
open System.Xml

// Register code page provider for proper encoding support
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)

// Define types for GNUCash data structure
type AccountType = 
    | Bank
    | Cash
    | Credit
    | Asset
    | Liability
    | Stock
    | Mutual
    | Income
    | Expense
    | Equity
    | Receivable
    | Payable
    | Root
    | Unknown of string

    static member FromString(str: string) =
        match str.ToLower() with
        | "bank" -> Bank
        | "cash" -> Cash
        | "credit" -> Credit
        | "asset" -> Asset
        | "liability" -> Liability
        | "stock" -> Stock
        | "mutual" -> Mutual
        | "income" -> Income
        | "expense" -> Expense
        | "equity" -> Equity
        | "receivable" -> Receivable
        | "payable" -> Payable
        | "root" -> Root
        | _ -> Unknown str

type Account = {
    Id: string
    Name: string
    Type: AccountType
    Description: string option
    ParentId: string option
    Commodity: string
}

type Split = {
    Id: string
    AccountId: string
    Value: decimal
    Quantity: decimal
    Memo: string option
}

type Transaction = {
    Id: string
    Currency: string
    DatePosted: DateTime
    DateEntered: DateTime
    Description: string
    Splits: Split list
}

type GnuCashData = {
    Accounts: Account list
    Transactions: Transaction list
}

module GnuCashParser =
    
    let parseAccount (accountNode: XmlElement) =
        try
            let id = accountNode.GetAttribute("id")
            let name = accountNode.SelectSingleNode("name") |> Option.ofObj |> Option.map (fun n -> n.InnerText) |> Option.defaultValue ""
            let accountType = accountNode.SelectSingleNode("type") |> Option.ofObj |> Option.map (fun n -> AccountType.FromString(n.InnerText)) |> Option.defaultValue (Unknown "unknown")
            let description = accountNode.SelectSingleNode("description") |> Option.ofObj |> Option.map (fun n -> n.InnerText)
            let parent = accountNode.SelectSingleNode("parent") |> Option.ofObj |> Option.map (fun n -> n.InnerText)
            let commodity = accountNode.SelectSingleNode("commodity/id") |> Option.ofObj |> Option.map (fun n -> n.InnerText) |> Option.defaultValue "USD"
            
            Some {
                Id = id
                Name = name
                Type = accountType
                Description = description
                ParentId = parent
                Commodity = commodity
            }
        with
        | ex -> 
            printfn "Error parsing account: %s" ex.Message
            None

    let parseSplit (splitNode: XmlElement) =
        try
            let id = splitNode.GetAttribute("id")
            let accountId = splitNode.SelectSingleNode("account") |> Option.ofObj |> Option.map (fun n -> n.InnerText) |> Option.defaultValue ""
            let value = splitNode.SelectSingleNode("value") |> Option.ofObj |> Option.map (fun n -> n.InnerText) |> Option.bind (fun v -> match Decimal.TryParse(v) with | (true, result) -> Some result | _ -> None) |> Option.defaultValue 0m
            let quantity = splitNode.SelectSingleNode("quantity") |> Option.ofObj |> Option.map (fun n -> n.InnerText) |> Option.bind (fun v -> match Decimal.TryParse(v) with | (true, result) -> Some result | _ -> None) |> Option.defaultValue 0m
            let memo = splitNode.SelectSingleNode("memo") |> Option.ofObj |> Option.map (fun n -> n.InnerText)
            
            Some {
                Id = id
                AccountId = accountId
                Value = value
                Quantity = quantity
                Memo = memo
            }
        with
        | ex -> 
            printfn "Error parsing split: %s" ex.Message
            None

    let parseTransaction (transactionNode: XmlElement) =
        try
            let id = transactionNode.GetAttribute("id")
            let currency = transactionNode.SelectSingleNode("currency/id") |> Option.ofObj |> Option.map (fun n -> n.InnerText) |> Option.defaultValue "USD"
            
            let datePosted = 
                transactionNode.SelectSingleNode("date-posted/date") 
                |> Option.ofObj 
                |> Option.map (fun n -> n.InnerText) 
                |> Option.bind (fun d -> match DateTime.TryParse(d) with | (true, result) -> Some result | _ -> None)
                |> Option.defaultValue DateTime.MinValue
                
            let dateEntered = 
                transactionNode.SelectSingleNode("date-entered/date") 
                |> Option.ofObj 
                |> Option.map (fun n -> n.InnerText) 
                |> Option.bind (fun d -> match DateTime.TryParse(d) with | (true, result) -> Some result | _ -> None)
                |> Option.defaultValue DateTime.MinValue
                
            let description = transactionNode.SelectSingleNode("description") |> Option.ofObj |> Option.map (fun n -> n.InnerText) |> Option.defaultValue ""
            
            let splits = 
                transactionNode.SelectNodes("splits/split") 
                |> Seq.cast<XmlElement>
                |> Seq.choose parseSplit
                |> List.ofSeq
            
            Some {
                Id = id
                Currency = currency
                DatePosted = datePosted
                DateEntered = dateEntered
                Description = description
                Splits = splits
            }
        with
        | ex -> 
            printfn "Error parsing transaction: %s" ex.Message
            None

    let private getNamespaceManager() =
        let nsManager = System.Xml.XmlNamespaceManager(NameTable())
        nsManager.AddNamespace("gnc", "http://www.gnucash.org/XML/gnc")
        nsManager.AddNamespace("act", "http://www.gnucash.org/XML/act")
        nsManager.AddNamespace("book", "http://www.gnucash.org/XML/book")
        nsManager.AddNamespace("cd", "http://www.gnucash.org/XML/cd")
        nsManager.AddNamespace("cmdty", "http://www.gnucash.org/XML/cmdty")
        nsManager.AddNamespace("slot", "http://www.gnucash.org/XML/slot")
        nsManager.AddNamespace("split", "http://www.gnucash.org/XML/split")
        nsManager.AddNamespace("sx", "http://www.gnucash.org/XML/sx")
        nsManager.AddNamespace("trn", "http://www.gnucash.org/XML/trn")
        nsManager.AddNamespace("ts", "http://www.gnucash.org/XML/ts")
        nsManager.AddNamespace("fs", "http://www.gnucash.org/XML/fs")
        nsManager.AddNamespace("bgt", "http://www.gnucash.org/XML/bgt")
        nsManager.AddNamespace("recurrence", "http://www.gnucash.org/XML/recurrence")
        nsManager.AddNamespace("lot", "http://www.gnucash.org/XML/lot")
        nsManager.AddNamespace("addr", "http://www.gnucash.org/XML/addr")
        nsManager.AddNamespace("billterm", "http://www.gnucash.org/XML/billterm")
        nsManager.AddNamespace("bt-days", "http://www.gnucash.org/XML/bt-days")
        nsManager.AddNamespace("bt-prox", "http://www.gnucash.org/XML/bt-prox")
        nsManager.AddNamespace("cust", "http://www.gnucash.org/XML/cust")
        nsManager.AddNamespace("employee", "http://www.gnucash.org/XML/employee")
        nsManager.AddNamespace("entry", "http://www.gnucash.org/XML/entry")
        nsManager.AddNamespace("invoice", "http://www.gnucash.org/XML/invoice")
        nsManager.AddNamespace("job", "http://www.gnucash.org/XML/job")
        nsManager.AddNamespace("order", "http://www.gnucash.org/XML/order")
        nsManager.AddNamespace("owner", "http://www.gnucash.org/XML/owner")
        nsManager.AddNamespace("taxtable", "http://www.gnucash.org/XML/taxtable")
        nsManager.AddNamespace("tte", "http://www.gnucash.org/XML/tte")
        nsManager.AddNamespace("vendor", "http://www.gnucash.org/XML/vendor")
        nsManager

    let loadGnuCashFile (filePath: string) =
        try
            if not (File.Exists filePath) then
                Error "File does not exist"
            else
                let xmlContent = File.ReadAllText(filePath)
                let doc = XmlDocument()
                doc.LoadXml(xmlContent)
                
                let accounts = 
                    doc.SelectNodes("//gnc:account", getNamespaceManager())
                    |> Seq.cast<XmlElement>
                    |> Seq.choose parseAccount
                    |> List.ofSeq
                
                let transactions = 
                    doc.SelectNodes("//gnc:transaction", getNamespaceManager())
                    |> Seq.cast<XmlElement>
                    |> Seq.choose parseTransaction
                    |> List.ofSeq
                
                Ok {
                    Accounts = accounts
                    Transactions = transactions
                }
        with
        | ex -> Error (sprintf "Error loading GNUCash file: %s" ex.Message)

// Extension methods for XML processing
type System.Xml.XmlNode with
    member this.SelectSingleNodeSafe(xpath: string) =
        try this.SelectSingleNode(xpath) with | _ -> null

module XmlExtensions =
    let selectNodes (xpath: string) (node: XmlNode) =
        try node.SelectNodes(xpath) with | _ -> null

    let selectSingleNode (xpath: string) (node: XmlNode) =
        try node.SelectSingleNode(xpath) with | _ -> null

open XmlExtensions

[<EntryPoint>]
let main argv =
    printfn "GNUCash File Reader"
    printfn "==================="
    
    if argv.Length = 0 then
        printfn "Please provide the path to a .gnucash file as an argument."
        printfn "Usage: GnuCashReader <path-to-gnucash-file>"
        1
    else
        let filePath = argv.[0]
        printfn "Reading file: %s" filePath
        
        match GnuCashParser.loadGnuCashFile filePath with
        | Ok data ->
            printfn "\nAccounts (%d):" data.Accounts.Length
            printfn "-----------"
            data.Accounts
            |> List.iter (fun account ->
                printfn "%-20s %-10s %s" account.Name (string account.Type) account.Id
            )
            
            printfn "\nTransactions (%d):" data.Transactions.Length
            printfn "----------------"
            data.Transactions
            |> List.take 10 // Show first 10 transactions
            |> List.iter (fun transaction ->
                printfn "%-12s %-40s %10.2f %s" 
                    (transaction.DatePosted.ToString("yyyy-MM-dd"))
                    transaction.Description
                    (transaction.Splits |> List.sumBy (fun split -> split.Value))
                    transaction.Currency
            )
            
            if data.Transactions.Length > 10 then
                printfn "... and %d more transactions" (data.Transactions.Length - 10)
            
            printfn "\nSummary:"
            printfn "--------"
            printfn "Total Accounts: %d" data.Accounts.Length
            printfn "Total Transactions: %d" data.Transactions.Length
            
            let totalValue = 
                data.Transactions 
                |> List.collect (fun t -> t.Splits) 
                |> List.sumBy (fun s -> s.Value)
            
            printfn "Total Transaction Value: %.2f" totalValue
            
        | Error errorMsg ->
            printfn "Error: %s" errorMsg
            printfn "Make sure the file exists and is a valid GNUCash XML file."
        
        0