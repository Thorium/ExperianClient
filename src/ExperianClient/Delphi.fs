namespace Experian

open System
open System.Globalization

/// A quotation search: leaves a soft footprint, which other lenders do not see.
[<RequireQualifiedAccess>]
type Quotation =
    | PersonalLoan
    | CreditCard
    | StoreCard
    | Mortgage
    | Insurance
    | Telco
    | Utility
    | MediaServices
    | MultipleProducts

/// A credit application search: leaves a hard footprint, which other lenders see.
[<RequireQualifiedAccess>]
type CreditProduct =
    | PersonalLoan
    | DebtConsolidationLoan
    | CreditCard
    | StoreCard
    | ChargeCard
    | CurrentAccount
    | OverdraftAccount
    | RevolvingCredit
    | HirePurchase
    | SecuredLoan
    | FirstMortgage

/// The footprint a search leaves on the applicant's credit file.
type Footprint =
    | Soft
    | Hard
    /// A code this library does not classify; Experian's documentation for it applies.
    | Unclassified

/// The search type (CAPS type) written to the applicant's credit file. Writing the correct one is a legal obligation.
type SearchType =
    | Quotation of Quotation
    | CreditApplication of CreditProduct
    /// Any other two-letter CAPS code from Experian's list, sent as given (for example "EQ").
    | OtherSearch of code: string

module SearchType =

    /// The CAPS code sent as Application.ApplicationType.
    let code searchType =
        match searchType with
        | Quotation Quotation.PersonalLoan -> "QL"
        | Quotation Quotation.CreditCard -> "QC"
        | Quotation Quotation.StoreCard -> "QS"
        | Quotation Quotation.Mortgage -> "QM"
        | Quotation Quotation.Insurance -> "QI"
        | Quotation Quotation.Telco -> "QT"
        | Quotation Quotation.Utility -> "QU"
        | Quotation Quotation.MediaServices -> "QP"
        | Quotation Quotation.MultipleProducts -> "QX"
        | CreditApplication CreditProduct.PersonalLoan -> "PL"
        | CreditApplication CreditProduct.DebtConsolidationLoan -> "CD"
        | CreditApplication CreditProduct.CreditCard -> "CC"
        | CreditApplication CreditProduct.StoreCard -> "SC"
        | CreditApplication CreditProduct.ChargeCard -> "CH"
        | CreditApplication CreditProduct.CurrentAccount -> "CA"
        | CreditApplication CreditProduct.OverdraftAccount -> "OA"
        | CreditApplication CreditProduct.RevolvingCredit -> "RC"
        | CreditApplication CreditProduct.HirePurchase -> "HP"
        | CreditApplication CreditProduct.SecuredLoan -> "SL"
        | CreditApplication CreditProduct.FirstMortgage -> "MG"
        | OtherSearch code -> code

    let private all =
        [ yield! [ Quotation.PersonalLoan; Quotation.CreditCard; Quotation.StoreCard; Quotation.Mortgage; Quotation.Insurance
                   Quotation.Telco; Quotation.Utility; Quotation.MediaServices; Quotation.MultipleProducts ]
                 |> List.map Quotation
          yield! [ CreditProduct.PersonalLoan; CreditProduct.DebtConsolidationLoan; CreditProduct.CreditCard; CreditProduct.StoreCard
                   CreditProduct.ChargeCard; CreditProduct.CurrentAccount; CreditProduct.OverdraftAccount; CreditProduct.RevolvingCredit
                   CreditProduct.HirePurchase; CreditProduct.SecuredLoan; CreditProduct.FirstMortgage ]
                 |> List.map CreditApplication ]

    /// The search type for a CAPS code, typed when this library knows it.
    let ofCode (caps: string) =
        let normalised = if isNull caps then "" else caps.Trim().ToUpperInvariant()
        all |> List.tryFind (fun searchType -> code searchType = normalised) |> Option.defaultValue (OtherSearch normalised)

    let footprint searchType =
        match searchType with
        | Quotation _ -> Soft
        | CreditApplication _ -> Hard
        | OtherSearch _ -> Unclassified

/// Whose credit file an account was matched to.
type AccountHolder =
    | MainApplicant
    | AliasOfMainApplicant
    | AssociateOfMainApplicant
    | JointApplicant
    | AliasOfJointApplicant
    | AssociateOfJointApplicant
    /// "9" (no match), a missing value, or a code Experian may add.
    | Unmatched of code: string

type AccountStatus =
    | Active
    | Delinquent
    | Defaulted
    | Settled
    | UnknownStatus of code: string

/// One credit account (CAIS record) from a Delphi Select response. Amounts are whole pounds as the lender last reported
/// them, not a settlement figure. AccountNumber is shown only for your own data unless Experian agrees otherwise.
type CreditAccount =
    { /// Absent for some lenders: Experian withholds the name (seen for company type 10).
      Lender: string voption
      AccountNumber: string voption
      /// Experian Standard Data Code for the account type.
      AccountTypeCode: string voption
      /// Experian Standard Data Code for the lender's company type.
      CompanyTypeCode: string voption
      Holder: AccountHolder
      Joint: bool
      Status: AccountStatus
      Balance: decimal voption
      /// The current balance of a defaulted account; Experian reports it apart from Balance.
      DefaultBalance: decimal voption
      /// The part of the balance in arrears.
      DelinquentBalance: decimal voption
      CreditLimit: decimal voption
      Payment: decimal voption
      StartDate: DateOnly voption
      SettlementDate: DateOnly voption
      LastUpdated: DateOnly voption
      /// Worst CAIS status code: 0 satisfactory, 1-6 months in arrears, 8 default, 9 bad debt, S slow payer, U, D, ?.
      WorstStatus: string voption }

module CreditAccounts =

    /// Pounds from Experian's text amounts ("£12256", "1,140"); ValueNone when absent or unreadable.
    let internal pounds (text: string option) =
        match text with
        | Some text ->
            let digits = text.Replace("£", "").Replace(",", "").Trim()
            match Decimal.TryParse(digits, NumberStyles.AllowLeadingSign ||| NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) with
            | true, value -> ValueSome value
            | _ -> ValueNone
        | None -> ValueNone

    let private holder (matchTo: string option) =
        match matchTo |> Option.map _.Trim() with
        | Some "1" -> MainApplicant
        | Some "2" -> AliasOfMainApplicant
        | Some "3" -> AssociateOfMainApplicant
        | Some "5" -> JointApplicant
        | Some "6" -> AliasOfJointApplicant
        | Some "7" -> AssociateOfJointApplicant
        | other -> Unmatched(other |> Option.defaultValue "")

    /// Narrative S (settled) or T (satisfied).
    let private paid (narrative: string option) =
        match narrative |> Option.map _.Trim() with
        | Some("S" | "T") -> true
        | _ -> false

    /// AccountStatus when present; a default with a satisfied date or a paid default narrative is Settled; without
    /// AccountStatus, a paid balance narrative means Settled.
    let private status accountStatus balanceNarrative defaultNarrative (defaultSatisfied: DateOnly option) =
        match accountStatus |> Option.map (fun (code: string) -> code.Trim()) with
        | Some "A" -> Active
        | Some "D" -> Delinquent
        | Some "F" when defaultSatisfied.IsSome || paid defaultNarrative -> Settled
        | Some "F" -> Defaulted
        | Some "S" -> Settled
        | (None | Some "") when paid balanceNarrative -> Settled
        | Some code -> UnknownStatus code
        | None -> UnknownStatus ""

    let private ofDetail (detail: DelphiSelect.CAISDetails_Item) =
        let matchTo = detail.MatchDetails |> Option.ofObj |> Option.bind _.MatchTo
        let balance = detail.Balance |> Option.ofObj
        let defaultBalance = detail.CurrentDefBalance |> Option.ofObj
        { Lender = ValueOption.ofOption detail.SupplyCompanyName
          AccountNumber = ValueOption.ofOption detail.AccountNumber
          AccountTypeCode = ValueOption.ofOption detail.AccountType
          CompanyTypeCode = ValueOption.ofOption detail.CompanyType
          Holder = holder matchTo
          Joint = (match detail.JointAccount |> Option.map _.Trim() with Some("M" | "J") -> true | _ -> false)
          Status =
            status detail.AccountStatus (balance |> Option.bind _.Narrative) (defaultBalance |> Option.bind _.Narrative) detail.DefaultSatisfyDate
          Balance = pounds (balance |> Option.bind _.Amount)
          DefaultBalance = pounds (defaultBalance |> Option.bind _.Amount)
          DelinquentBalance = pounds (detail.DelinquentBalance |> Option.ofObj |> Option.bind _.Amount)
          CreditLimit = pounds (detail.CreditLimit |> Option.ofObj |> Option.bind _.Amount)
          Payment = pounds detail.Payment
          StartDate = ValueOption.ofOption detail.CaisAccStartDate
          SettlementDate = ValueOption.ofOption detail.SettlementDate
          LastUpdated = ValueOption.ofOption detail.LastUpdatedDate
          WorstStatus = ValueOption.ofOption detail.WorstStatus }

    /// Every credit account in a Delphi Select response, whoever it was matched to. ValueNone when the response carries
    /// no consumer data, so a missing credit file is never read as "no debts"; ValueSome [] when it has no accounts.
    let ofOutput (output: DelphiSelect.Output) =
        output
        |> Option.ofObj
        |> Option.bind (fun o -> Option.ofObj o.Response)
        |> Option.bind (fun r -> Option.ofObj r.FullConsumerData)
        |> Option.bind (fun f -> Option.ofObj f.ConsumerData)
        |> Option.map (fun consumer ->
            consumer.Cais
            |> Option.ofObj
            |> Option.defaultValue [||]
            |> Seq.filter (isNull >> not)
            |> Seq.collect (fun block -> block.CaisDetails |> Option.ofObj |> Option.defaultValue [||])
            |> Seq.filter (isNull >> not)
            |> Seq.map ofDetail
            |> List.ofSeq)
        |> ValueOption.ofOption

    /// What the account owes now: a defaulted account's default balance when Experian reports one, otherwise the balance.
    let amountOwed (account: CreditAccount) =
        match account.Status, account.DefaultBalance with
        | Defaulted, ValueSome owed -> ValueSome owed
        | _ -> account.Balance

    /// The main applicant's own accounts (including under an alias) that are not settled and still owe something.
    /// Associates' accounts are someone else's credit and are left out. This includes mortgages (type 03) and phone or
    /// utility contracts (type 18): choose what a consolidation loan pays by AccountTypeCode, from Experian's Standard Data Codes.
    let outstanding (accounts: CreditAccount list) =
        accounts
        |> List.filter (fun account ->
            match account.Holder, account.Status, amountOwed account with
            | (MainApplicant | AliasOfMainApplicant), (Active | Delinquent | Defaulted | UnknownStatus _), ValueSome owed -> owed > 0m
            | _ -> false)

    /// The sum of what the accounts owe (amountOwed).
    let totalOwed (accounts: CreditAccount list) =
        accounts |> List.sumBy (amountOwed >> ValueOption.defaultValue 0m)
