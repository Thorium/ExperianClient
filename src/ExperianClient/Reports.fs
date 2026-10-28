namespace Experian

open System
open System.Globalization

/// The Identity Authenticate (AuthPlus) decision: AUnn, NAnn or RAnn with a level 00-99, or none.
type AuthPlusDecision =
    | Authenticated of level: int
    | NotAuthenticated of level: int
    | Refer of level: int
    | NoDecision
    | OtherAuthPlusDecision of code: string

[<RequireQualifiedAccess>]
type AuthenticationIndex =
    /// 00-90.
    | Index of int
    | ProcessingError
    | NotUsed

/// A high-risk policy rule that fired (Unnn).
type HighRiskRule = { Id: string voption; Text: string voption }

/// One applicant's Identity Authenticate (AuthPlus) result.
type AuthPlusResult =
    { ApplicantIdentifier: int voption
      Reference: string voption
      Decision: AuthPlusDecision
      /// Text the client configured for the decision.
      DecisionText: string voption
      Index: AuthenticationIndex voption
      IndexText: string voption
      /// The level the identity was confirmed at, and its text.
      ConfirmedLevel: string voption
      ConfirmedLevelText: string voption
      HighRiskRules: HighRiskRule list }

module AuthPlus =

    let internal decision (code: string option) =
        match code |> Option.map _.Trim() with
        | None
        | Some "" -> NoDecision
        | Some code when code.Length = 4 ->
            match code.Substring(0, 2), Int32.TryParse(code.Substring 2, NumberStyles.None, CultureInfo.InvariantCulture) with
            | "AU", (true, level) -> Authenticated level
            | "NA", (true, level) -> NotAuthenticated level
            | "RA", (true, level) -> Refer level
            | _ -> OtherAuthPlusDecision code
        | Some code -> OtherAuthPlusDecision code

    let private index (text: string option) =
        match text |> Option.map _.Trim() with
        | Some "-97" -> ValueSome AuthenticationIndex.ProcessingError
        | Some "-98" -> ValueSome AuthenticationIndex.NotUsed
        | Some text ->
            match Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, value -> ValueSome(AuthenticationIndex.Index value)
            | _ -> ValueNone
        | None -> ValueNone

    let private present (value: 'T) = not (isNull (box value))

    /// AuthPlus results from any Delphi Select answer that carries them (application, second phase or stand-alone).
    let ofResults (results: DelphiSelect.AuthPlusResults_Item[]) =
        results
        |> Option.ofObj
        |> Option.defaultValue [||]
        |> Seq.filter present
        |> Seq.map (fun result ->
            let consumer = result.AuthConsumer |> Option.ofObj
            let data = consumer |> Option.bind (fun c -> Option.ofObj c.DecisionData)
            let confirmed = consumer |> Option.bind (fun c -> Option.ofObj c.IdConfirmedFlag)
            { ApplicantIdentifier = ValueOption.ofOption result.ApplicantIdentifier
              Reference = consumer |> Option.bind _.AuthPlusRef |> ValueOption.ofOption
              Decision = decision (data |> Option.bind _.AuthPlusDecision)
              DecisionText = data |> Option.bind _.AuthPlusDecText |> ValueOption.ofOption
              Index = index (data |> Option.bind _.AuthenticationIndex)
              IndexText = data |> Option.bind _.AuthIndexText |> ValueOption.ofOption
              ConfirmedLevel = confirmed |> Option.bind _.IdConfirmedLevel |> ValueOption.ofOption
              ConfirmedLevelText = confirmed |> Option.bind _.IdConfirmLevelText |> ValueOption.ofOption
              HighRiskRules =
                result.HighRiskPolicyRules
                |> Option.ofObj
                |> Option.defaultValue [||]
                |> Seq.filter present
                |> Seq.map (fun rule ->
                    { Id = ValueOption.ofOption rule.HighRiskPolRuleId
                      Text = ValueOption.ofOption rule.HighRiskPolRuleText })
                |> List.ofSeq })
        |> List.ofSeq

    /// AuthPlus results in an application answer.
    let ofOutput (output: DelphiSelect.Output) =
        output |> Option.ofObj |> Option.bind (fun o -> Option.ofObj o.Response) |> Option.map (fun r -> ofResults r.AuthPlusResults)
        |> Option.defaultValue []

/// The total value of county court judgments in the last three years, as Experian bands it.
[<RequireQualifiedAccess>]
type CcjTotal =
    | NoCcj
    | UpTo500
    /// Over £500, up to this many pounds (bands of £50).
    | UpTo of pounds: int
    | NoTrace
    | OtherCcjCode of code: int

/// One applicant's impaired credit history (ICH). Bankruptcy and IVA cover the last three years, arrears the last two.
type ImpairedCredit =
    { /// Any impaired credit history reference is set.
      Impaired: bool
      Bankruptcy: bool
      Iva: bool
      Ccj: CcjTotal voption
      /// Three or more monthly payments in arrears on secured credit.
      SecuredArrears: bool
      UnsecuredArrears: bool }

/// Impaired credit history per applicant; ValueNone when absent, when the client is not configured for ICH ('Z'), and
/// for the joint applicant when the answer lists only one applicant (Experian still sends 'N' for that slot).
type ImpairedCreditHistory =
    { MainApplicant: ImpairedCredit voption
      JointApplicant: ImpairedCredit voption }

/// A public record (court judgment, insolvency and similar) from a Delphi Select answer. TypeCode is Experian's
/// Standard Data Code; Experian's ImpairedCredit flags give the classification by applicant.
type PublicRecord =
    { TypeCode: string voption
      Holder: AccountHolder
      Date: DateOnly voption
      Amount: decimal voption
      Plaintiff: string voption
      SatisfiedOn: DateOnly voption
      Source: string voption }

module PublicRecords =

    let private flag (expected: string) (value: string option) =
        match value |> Option.map _.Trim() with
        | Some code -> code = expected
        | None -> false

    let private ccj (value: int option) =
        match value with
        | Some -9999
        | None -> ValueNone
        | Some -1 -> ValueSome CcjTotal.NoTrace
        | Some 0 -> ValueSome CcjTotal.NoCcj
        | Some 1 -> ValueSome CcjTotal.UpTo500
        | Some band when band >= 11 -> ValueSome(CcjTotal.UpTo(band * 50))
        | Some other -> ValueSome(CcjTotal.OtherCcjCode other)

    let private applicant ich bankrupt iva ccjBand secured unsecured =
        match ich |> Option.map (fun (code: string) -> code.Trim()) with
        | None
        | Some ""
        | Some "Z" -> ValueNone
        | Some code ->
            ValueSome
                { Impaired = (code = "Y")
                  Bankruptcy = flag "B" bankrupt
                  Iva = flag "V" iva
                  Ccj = ccj ccjBand
                  SecuredArrears = flag "A" secured
                  UnsecuredArrears = flag "A" unsecured }

    /// The impaired credit history in an application answer; ValueNone when the answer has none.
    let impairedCredit (output: DelphiSelect.Output) =
        let response = output |> Option.ofObj |> Option.bind (fun o -> Option.ofObj o.Response)
        let hasJointApplicant =
            response |> Option.exists (fun r -> not (isNull r.Applicants) && r.Applicants.Length >= 2)
        response
        |> Option.bind (fun r -> Option.ofObj r.ConsumerSummary)
        |> Option.bind (fun c -> Option.ofObj c.PremiumValueData)
        |> Option.bind (fun p -> Option.ofObj p.ImpairedCh)
        |> Option.map (fun ich ->
            { MainApplicant = applicant ich.Ndmaich ich.NdmaBankrupt ich.Ndmaiva ich.Ndmaccj ich.Ndmasecarr ich.Ndmaunsecarr
              JointApplicant =
                if hasJointApplicant then
                    applicant ich.Ndjaich ich.NdjaBankrupt ich.Ndjaiva ich.Ndjaccj ich.Ndjasecarr ich.Ndjaunsecarr
                else
                    ValueNone })
        |> ValueOption.ofOption

    /// Every public record in an application answer, whoever it was matched to; ValueNone when the answer carries no
    /// consumer data.
    let ofOutput (output: DelphiSelect.Output) =
        output
        |> Option.ofObj
        |> Option.bind (fun o -> Option.ofObj o.Response)
        |> Option.bind (fun r -> Option.ofObj r.FullConsumerData)
        |> Option.bind (fun f -> Option.ofObj f.ConsumerData)
        |> Option.map (fun consumer ->
            consumer.PublicInformation
            |> Option.ofObj
            |> Option.defaultValue [||]
            |> Seq.filter (isNull >> not)
            |> Seq.collect (fun block -> block.PublicInfoDetails |> Option.ofObj |> Option.defaultValue [||])
            |> Seq.filter (isNull >> not)
            |> Seq.map (fun record ->
                let pounds = CreditAccounts.pounds record.AmountPounds
                { TypeCode = ValueOption.ofOption record.InformationType
                  Holder = CreditAccounts.holder (record.MatchDetails |> Option.ofObj |> Option.bind _.MatchTo)
                  Date = ValueOption.ofOption record.InformationDate
                  Amount = pounds |> ValueOption.map (fun p -> p + decimal (defaultArg record.AmountPence 0) / 100m)
                  Plaintiff = ValueOption.ofOption record.CourtPlaintiff
                  SatisfiedOn =
                    match record.SatisfactionDate |> Option.map _.Trim() with
                    | Some text ->
                        match DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
                        | true, date -> ValueSome date
                        | _ -> ValueNone
                    | None -> ValueNone
                  Source = ValueOption.ofOption record.DataSource })
            |> List.ofSeq)
        |> ValueOption.ofOption

    /// The records matched to the main applicant (including under an alias) that are not satisfied.
    let unsatisfied (records: PublicRecord list) =
        records
        |> List.filter (fun record ->
            (record.Holder = MainApplicant || record.Holder = AliasOfMainApplicant) && record.SatisfiedOn.IsNone)

/// An income figure from current account turnover (CATO), or why there is none.
[<RequireQualifiedAccess>]
type IncomeEstimate =
    | Pounds of int
    | NoTrace
    | NoCatoData
    | NoRelevantCatoData
    | Suppressed
    | OtherIncomeCode of code: int

/// How the income the applicant supplied compares with Experian's 12-month CATO estimate. Clients configured for the
/// CATO warning indicator get codes 1-3 instead, whose meaning depends on that configuration: OtherVerificationCode.
[<RequireQualifiedAccess>]
type IncomeVerification =
    /// Supplied income is 91-110% of the estimate.
    | MatchesEstimate
    /// Supplied income is 90% of the estimate or less.
    | BelowEstimate
    /// Supplied income is above 110% of the estimate, from this percentage upwards (201 means 201% or more).
    | AboveEstimate of fromPercent: int
    | MissingSuppliedIncome
    | InsufficientData
    | NoCatoData
    | NoTrace
    | OtherVerificationCode of code: int

/// A ratio Experian reports as a whole percentage, or the negative code it gives instead.
[<RequireQualifiedAccess>]
type Percentage =
    | Percent of int
    | Unavailable of code: int

type IncomeEstimates =
    { GrossAnnual: IncomeEstimate voption
      NetAnnual: IncomeEstimate voption
      NetMonthly: IncomeEstimate voption }

/// Affordability figures in a Delphi Select answer (Affordability / Income Estimation packages); fields are ValueNone when
/// the package that fills them was not requested.
type Affordability =
    { MainApplicant: IncomeEstimates
      JointApplicant: IncomeEstimates
      Household: IncomeEstimates
      MainApplicantVerification: IncomeVerification voption
      HouseholdVerification: IncomeVerification voption
      /// Unsecured balances as a % of gross annual income.
      UnsecuredDebtToIncome: Percentage voption
      /// Secured balances as a % of gross annual income.
      SecuredDebtToIncome: Percentage voption
      /// Revolving credit commitments as a % of net monthly income.
      RevolvingCommitmentsToIncome: Percentage voption
      /// Non-revolving credit commitments as a % of net monthly income.
      NonRevolvingCommitmentsToIncome: Percentage voption }

module Affordability =

    let private income (value: int option) =
        match value with
        | None -> ValueNone
        | Some -1 -> ValueSome IncomeEstimate.NoTrace
        | Some -2 -> ValueSome IncomeEstimate.NoCatoData
        | Some -3 -> ValueSome IncomeEstimate.NoRelevantCatoData
        | Some -4 -> ValueSome IncomeEstimate.Suppressed
        | Some pounds when pounds >= 0 -> ValueSome(IncomeEstimate.Pounds pounds)
        | Some other -> ValueSome(IncomeEstimate.OtherIncomeCode other)

    let private verification (value: int option) =
        match value with
        | None -> ValueNone
        | Some 90 -> ValueSome IncomeVerification.MatchesEstimate
        | Some 99 -> ValueSome IncomeVerification.BelowEstimate
        | Some 0 -> ValueSome(IncomeVerification.AboveEstimate 201)
        | Some 10 -> ValueSome(IncomeVerification.AboveEstimate 181)
        | Some band when band >= 20 && band <= 80 && band % 10 = 0 -> ValueSome(IncomeVerification.AboveEstimate(191 - band))
        | Some -1 -> ValueSome IncomeVerification.NoTrace
        | Some -2 -> ValueSome IncomeVerification.NoCatoData
        | Some -3 -> ValueSome IncomeVerification.InsufficientData
        | Some -5 -> ValueSome IncomeVerification.MissingSuppliedIncome
        | Some other -> ValueSome(IncomeVerification.OtherVerificationCode other)

    let private percentage (value: int option) =
        match value with
        | None -> ValueNone
        | Some percent when percent >= 0 -> ValueSome(Percentage.Percent percent)
        | Some code -> ValueSome(Percentage.Unavailable code)

    /// The affordability figures in an application answer; ValueNone when the answer has no premium value data.
    let ofOutput (output: DelphiSelect.Output) =
        output
        |> Option.ofObj
        |> Option.bind (fun o -> Option.ofObj o.Response)
        |> Option.bind (fun r -> Option.ofObj r.ConsumerSummary)
        |> Option.bind (fun c -> Option.ofObj c.PremiumValueData)
        |> Option.map (fun premium ->
            let iq = premium.AffordabilityIq |> Option.ofObj
            let dti = premium.AffordabilityIq2 |> Option.ofObj |> Option.bind (fun a -> Option.ofObj a.Dti2)
            let estimates gross net monthly =
                { GrossAnnual = iq |> Option.bind gross |> income
                  NetAnnual = iq |> Option.bind net |> income
                  NetMonthly = iq |> Option.bind monthly |> income }
            { MainApplicant = estimates _.Affe01 _.Affe02 _.Affe03
              JointApplicant = estimates _.Affe04 _.Affe05 _.Affe06
              Household = estimates _.Affe07 _.Affe08 _.Affe09
              MainApplicantVerification = iq |> Option.bind _.Affv01 |> verification
              HouseholdVerification = iq |> Option.bind _.Affv02 |> verification
              UnsecuredDebtToIncome = dti |> Option.bind _.SpI56 |> percentage
              SecuredDebtToIncome = dti |> Option.bind _.SpI57 |> percentage
              RevolvingCommitmentsToIncome = dti |> Option.bind _.SpI59 |> percentage
              NonRevolvingCommitmentsToIncome = dti |> Option.bind _.SpI60 |> percentage })
        |> ValueOption.ofOption

/// The overall KYC or PEP/sanctions decision.
[<RequireQualifiedAccess>]
type KycDecision =
    | Continue
    | Refer
    | Stop
    | NoDecision
    | OtherDecision of code: string

type KycRule =
    { Id: string voption
      Name: string voption
      Score: double voption
      Text: string voption }

/// One service's part of a KYC answer (identity verification, contra-indicators, PEPs and sanctions, …).
type KycService =
    { Name: string voption
      Decision: string voption
      Reason: string voption
      Score: double voption
      /// Rules that fired (score above zero), without the housekeeping rules 9990 ("no other rule has fired") and 9991
      /// ("always true"). For identity verification they confirm; for contra-indicator and PEP/sanctions services they alert.
      FiredRules: KycRule list }

type KycOutcome =
    { Decision: KycDecision
      Text: string voption
      Reasons: string list
      NextActions: string list
      Services: KycService list }

module KycOutcome =

    let private decision (code: string option) =
        match code |> Option.map (fun c -> c.Trim().ToUpperInvariant()) with
        | Some "CONTINUE" -> KycDecision.Continue
        | Some "REFER" -> KycDecision.Refer
        | Some "STOP" -> KycDecision.Stop
        | None
        | Some ""
        | Some "NODECISION"
        | Some "NO DECISION" -> KycDecision.NoDecision
        | Some other -> KycDecision.OtherDecision other

    let private items (values: 'T[]) = values |> Option.ofObj |> Option.defaultValue [||] |> Seq.filter (fun v -> not (isNull (box v)))

    /// The outcome of a KYC check or PEP/sanctions screen.
    let ofResponse (response: Kyc.ResponseMessage) =
        let overall =
            response
            |> Option.ofObj
            |> Option.bind (fun r -> Option.ofObj r.ResponseHeader)
            |> Option.bind (fun h -> Option.ofObj h.OverallResponse)
        { Decision = decision (overall |> Option.bind _.Decision)
          Text = overall |> Option.bind _.DecisionText |> ValueOption.ofOption
          Reasons = overall |> Option.map (fun o -> items o.DecisionReasons |> List.ofSeq) |> Option.defaultValue []
          NextActions = overall |> Option.map (fun o -> items o.RecommendedNextActions |> List.ofSeq) |> Option.defaultValue []
          Services =
            response
            |> Option.ofObj
            |> Option.bind (fun r -> Option.ofObj r.ClientResponsePayload)
            |> Option.map (fun p -> items p.DecisionElements |> List.ofSeq)
            |> Option.defaultValue []
            |> List.map (fun element ->
                { Name = ValueOption.ofOption element.ServiceName
                  Decision = ValueOption.ofOption element.Decision
                  Reason = ValueOption.ofOption element.DecisionReason
                  Score = ValueOption.ofOption element.Score
                  FiredRules =
                    items element.Rules
                    |> Seq.filter (fun rule ->
                        rule.RuleScore |> Option.exists (fun score -> score > 0.)
                        && not (rule.RuleId |> Option.exists (fun id -> id.Trim() = "9990" || id.Trim() = "9991")))
                    |> Seq.map (fun rule ->
                        { Id = ValueOption.ofOption rule.RuleId
                          Name = ValueOption.ofOption rule.RuleName
                          Score = ValueOption.ofOption rule.RuleScore
                          Text = ValueOption.ofOption rule.RuleText })
                    |> List.ofSeq }) }
