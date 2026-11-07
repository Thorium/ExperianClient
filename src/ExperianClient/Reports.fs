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

[<Struct; RequireQualifiedAccess>]
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
      /// Holder alone doesn't make the record theirs: see CreditAccounts.isApplicantRecord.
      Match: RecordMatch
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
                let details = record.MatchDetails |> Option.ofObj
                { TypeCode = ValueOption.ofOption record.InformationType
                  Holder = CreditAccounts.holder (details |> Option.bind _.MatchTo)
                  Match = CreditAccounts.recordMatch (details |> Option.bind _.BureauRefCategory)
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

    /// The main applicant's own records (CreditAccounts.isApplicantRecord) that are not satisfied.
    let unsatisfied (records: PublicRecord list) =
        records
        |> List.filter (fun record ->
            CreditAccounts.isApplicantRecord record.Holder record.Match && record.SatisfiedOn.IsNone)

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

/// A check the KYB company and people data calls can carry out. Licensing and Experian's per-client configuration decide
/// which ones a client gets, and the answer does not say what was asked for.
[<Struct; RequireQualifiedAccess>]
type KybCheck =
    /// Lexis Nexis PEP, sanctions and adverse media screening.
    | Screening
    /// CrossCore authentication of individuals. Experian does not send business records to it.
    | Authentication
    /// AML flags on the company: adverse markers and cashflow profiles. The people data call has none.
    | AdverseMarkers

    static member All = [ Screening; Authentication; AdverseMarkers ]

/// A key party of a company data answer. An id Experian reports as "Unknown" is ValueNone.
type KybKeyParty =
    { /// A company's legal name, or an individual's names.
      Name: string voption
      UboShareholderId: string voption
      PscId: string voption
      DirectorUrn: string voption }

/// Who a hit or gap in a KYB answer concerns.
[<RequireQualifiedAccess>]
type KybSubject =
    | Company
    | KeyParty of KybKeyParty
    /// The individual in a people data answer.
    | Person
    /// A Lexis Nexis match whose ids link to no key party, with the name sent to Lexis Nexis.
    | Unlinked of inputName: string voption

/// What a person has to look at. Risks are Experian's High or Standard, which depend on the client's configuration.
[<RequireQualifiedAccess>]
type KybHitKind =
    | Pep of risk: string voption
    | Sanction
    | AdverseMedia of risk: string voption
    | Enforcement of risk: string voption
    /// Experian's SOE flag.
    | Soe
    /// Experian's RCA flag.
    | Rca of risk: string voption
    /// Lexis Nexis hit without a category, or a match row that flags no category.
    | Uncategorised
    /// CrossCore answered NotAuthenticated or Refer.
    | Authentication of AuthPlusDecision
    | Dissolved
    /// The company's UBO structure has an exception, with Experian's summary of it.
    | OwnershipException of description: string voption
    /// A set flag or non-zero count, by its name in the answer: an AML flag, PartialOwnershipFlag, FcaDisciplinaryActionTaken,
    /// DirectorNumDisqualifications or DirectorNumberConvictions.
    | AdverseMarker of field: string

type KybHit =
    { Subject: KybSubject
      Kind: KybHitKind }

/// Why a KYB answer can't be read as complete.
[<RequireQualifiedAccess>]
type KybGap =
    /// The answer has no Results.
    | NoResults
    /// An entry of Results.ErrorCodes, such as ORCHESTRATOR_API_TIMEOUT.
    | ErrorCode of code: string
    /// LnScreeningSuppress or IdVerificationSuppress is true.
    | Suppressed of field: string
    /// DataQualityFlag, or a Matching.Matched other than yes: the check concerned no known company.
    | CompanyNotFound of value: string
    /// No BusinessRef, Matching.BestMatchedId or BusinessName: nothing says which company was checked.
    | CompanyNotIdentified
    /// Matching.Matched is yes and NumberOfMatches is not 1: the record checked may not be the company asked about.
    | MatchCount of matches: double
    /// A block is absent. KeyParties and LexisNexisDetails also when they hold a null.
    | BlockMissing of subject: KybSubject * block: string
    /// A field the check rests on is absent or blank.
    | FieldMissing of subject: KybSubject * field: string
    /// A value the spec does not list.
    | UnknownValue of subject: KybSubject * field: string * value: string
    /// CrosscoreAttempted other than True, or CCAuthenticationAttempted other than TRUE, with its value.
    | CrossCoreNotCompleted of subject: KybSubject * field: string * value: string
    /// Authentication was expected, and every key party is a company.
    | NoIndividualKeyParty

/// What a KYB company or people data answer shows. A 200 alone shows nothing: Experian's answers depend on per-client
/// configuration, and errors and suppressed checks arrive in a 200.
[<RequireQualifiedAccess>]
type KybOutcome =
    /// Every expected check ran, and every flag it rests on holds its clean value. It covers the key parties the answer
    /// lists and the company in Results.BusinessRef or Matching.BestMatchedId, which the caller compares with the company
    /// it asked about.
    | Clear
    /// A person has to look at the hits. Gaps are what also went unchecked; they are empty when every expected check ran.
    | NeedsReview of hits: KybHit list * gaps: KybGap list
    /// No hit was seen, and some checks did not run or can't be read. Never to be read as clear.
    | NotScreened of gaps: KybGap list

module KybOutcome =

    type private Finding =
        | Hit of KybHit
        | Gap of KybGap

    let private hit subject kind = Hit { Subject = subject; Kind = kind }

    /// The value, without blanks.
    let private text (value: string option) =
        value |> Option.map _.Trim() |> Option.filter (String.IsNullOrEmpty >> not)

    let private equalsText (expected: string) (value: string) = value.Equals(expected, StringComparison.OrdinalIgnoreCase)

    let private items (values: 'T[]) = values |> Option.ofObj |> Option.defaultValue [||] |> List.ofArray |> List.filter (fun v -> not (isNull (box v)))

    let private missingBlock expected subject block = if expected then [ Gap(KybGap.BlockMissing(subject, block)) ] else []

    let private missingField expected subject field = if expected then [ Gap(KybGap.FieldMissing(subject, field)) ] else []

    let private isSet value = equalsText "Y" value || equalsText "Yes" value

    /// Experian words a risk only for a hit.
    let private statesRisk =
        function
        | KybHitKind.Pep(ValueSome _)
        | KybHitKind.AdverseMedia(ValueSome _)
        | KybHitKind.Enforcement(ValueSome _)
        | KybHitKind.Rca(ValueSome _) -> true
        | _ -> false

    /// Y or Yes is a hit, whichever word the field's spec uses, and so is the clean word beside a stated risk. Only the field's
    /// own clean word is clean; anything else is a gap.
    let private flags (clean: string) expected subject entries =
        entries
        |> List.collect (fun (field, value, kind) ->
            match text value with
            | None -> missingField expected subject field
            | Some value when isSet value -> [ hit subject kind ]
            | Some value when equalsText clean value && statesRisk kind -> [ hit subject kind ]
            | Some value when equalsText clean value -> []
            | Some value -> [ Gap(KybGap.UnknownValue(subject, field, value)) ])

    let private optional = text >> ValueOption.ofOption

    let private lexisNexisFlags pep pepRisk sanction enforcement enforcementRisk soe rca rcaRisk adverseMedia adverseMediaRisk uncategorised =
        [ "LNPep", pep, KybHitKind.Pep(optional pepRisk)
          "LNSanction", sanction, KybHitKind.Sanction
          "LNEnforcement", enforcement, KybHitKind.Enforcement(optional enforcementRisk)
          "LNSoe", soe, KybHitKind.Soe
          "LNRca", rca, KybHitKind.Rca(optional rcaRisk)
          "LNAdverseMedia", adverseMedia, KybHitKind.AdverseMedia(optional adverseMediaRisk)
          "LNUncategorisedHit", uncategorised, KybHitKind.Uncategorised ]

    let private summaryFlags (block: Kyb.KybDataSummaryLNFields) =
        lexisNexisFlags block.LnPep block.LnPepRisk block.LnSanction block.LnEnforcement block.LnEnforcementRisk block.LnSoe block.LnRca block.LnRcaRisk block.LnAdverseMedia block.LnAdverseMediaRisk block.LnUncategorisedHit

    let private partyFlags (block: Kyb.KybDataLNDetails) =
        lexisNexisFlags block.LnPep block.LnPepRisk block.LnSanction block.LnEnforcement block.LnEnforcementRisk block.LnSoe block.LnRca block.LnRcaRisk block.LnAdverseMedia block.LnAdverseMediaRisk block.LnUncategorisedHit

    let private summary expected subject blockName (block: Kyb.KybDataSummaryLNFields) =
        match Option.ofObj block with
        | Some lexisNexis -> flags "No" expected subject (summaryFlags lexisNexis)
        | None -> missingBlock expected subject blockName

    /// A row is a match Lexis Nexis returned, so it is a hit whatever its flags say. A null row is a match that can't be read.
    let private matchRows (owner: KybSubject) (subjectOf: Kyb.KybDataLexisNexisDetails_Item -> KybSubject) (rows: Kyb.KybDataLexisNexisDetails_Item[]) =
        let readable = items rows
        let unreadable = if isNull rows || readable.Length = rows.Length then [] else [ Gap(KybGap.BlockMissing(owner, "LexisNexisDetails")) ]
        unreadable
        @ (readable
           |> List.collect (fun row ->
               let subject = subjectOf row
               let flagged =
                   lexisNexisFlags row.Pep row.PepRisk row.Sanction row.Enforcement row.EnforcementRisk row.Soe row.Rca row.RcaRisk row.AdverseMedia row.AdverseMediaRisk row.UncategorisedHit
                   |> List.filter (fun (_, value, _) -> text value |> Option.exists isSet)
                   |> List.map (fun (_, _, kind) -> kind)
               match flagged with
               | [] -> [ hit subject KybHitKind.Uncategorised ]
               | kinds -> kinds |> List.map (hit subject)))

    let private crossCore expected subject blockName (block: Kyb.KybDataCCDetails) =
        match Option.ofObj block with
        | None -> missingBlock expected subject blockName
        | Some details ->
            let call =
                match text details.CrosscoreAttempted with
                | Some value when equalsText "True" value -> []
                | Some value -> [ Gap(KybGap.CrossCoreNotCompleted(subject, "CrosscoreAttempted", value)) ]
                | None -> missingField expected subject "CrosscoreAttempted"
            let called = expected && List.isEmpty call
            let sent =
                match text details.CcAuthenticationAttempted with
                | Some value when equalsText "TRUE" value -> []
                | Some value when equalsText "FALSE" value && not expected -> []
                | Some value -> [ Gap(KybGap.CrossCoreNotCompleted(subject, "CCAuthenticationAttempted", value)) ]
                | None -> missingField called subject "CCAuthenticationAttempted"
            let decision =
                match AuthPlus.decision details.CcDecision with
                | Authenticated level when level = 1 || level = 2 -> []
                | Authenticated _ -> [ Gap(KybGap.UnknownValue(subject, "CCDecision", details.CcDecision |> Option.defaultValue "")) ]
                | NotAuthenticated _
                | Refer _ as refused -> [ hit subject (KybHitKind.Authentication refused) ]
                | NoDecision -> missingField (called && List.isEmpty sent) subject "CCDecision"
                | OtherAuthPlusDecision code -> [ Gap(KybGap.UnknownValue(subject, "CCDecision", code)) ]
            call @ sent @ decision

    let private keyParty (party: Kyb.KybDataKeyParties_Item) =
        let known (id: string option) = text id |> Option.filter (equalsText "Unknown" >> not) |> ValueOption.ofOption
        { Name =
            match text party.LegalName, List.choose text [ party.Forename; party.MiddleName; party.Surname ] with
            | Some legalName, _ -> ValueSome legalName
            | None, [] -> ValueNone
            | None, names -> ValueSome(String.Join(" ", names))
          UboShareholderId = known party.UboShareholderId
          PscId = known party.Pscid
          DirectorUrn = known party.DirectorUrn }

    /// Types C and O are companies, which Experian does not authenticate. X, a missing type or any other is taken for an individual.
    let private isIndividual (party: Kyb.KybDataKeyParties_Item) =
        match text party.ShareholderType with
        | Some kind -> not (equalsText "C" kind || equalsText "O" kind)
        | None -> true

    /// A key party the spec gives a conviction count: a current director. A URN of Unknown is still a director's.
    let private isDirector (party: Kyb.KybDataKeyParties_Item) =
        text party.DirectorFlag |> Option.exists isSet || Option.isSome (text party.DirectorUrn)

    /// Non-zero is a hit, and only zero is clean. "" and "-" arrive here as absent.
    let private count expected subject field (value: double option) =
        match value with
        | Some number when number <> 0. -> [ hit subject (KybHitKind.AdverseMarker field) ]
        | Some _ -> []
        | None -> missingField expected subject field

    let private partyFindings (expects: KybCheck -> bool) (party: Kyb.KybDataKeyParties_Item) =
        let identity = keyParty party
        let subject = KybSubject.KeyParty identity
        let unidentified =
            match identity.Name, identity.UboShareholderId, identity.PscId, identity.DirectorUrn with
            | ValueNone, ValueNone, ValueNone, ValueNone -> missingField (expects KybCheck.Screening || expects KybCheck.Authentication) subject "Name"
            | _ -> []
        let lexisNexis =
            match Option.ofObj party.Lexisnexis with
            | Some block -> flags "No" (expects KybCheck.Screening) subject (partyFlags block)
            | None -> missingBlock (expects KybCheck.Screening) subject "Lexisnexis"
        unidentified
        @ lexisNexis
        @ crossCore (expects KybCheck.Authentication && isIndividual party) subject "Crosscore" party.Crosscore
        @ count (expects KybCheck.AdverseMarkers && isDirector party) subject "DirectorNumberConvictions" party.DirectorNumberConvictions

    let private keyParties (expects: KybCheck -> bool) (parties: Kyb.KybDataKeyParties_Item[]) =
        let expected = expects KybCheck.Screening || expects KybCheck.Authentication
        let readable = items parties
        match Option.ofObj parties with
        | Some all when all.Length > 0 ->
            (if readable.Length < all.Length then missingBlock expected KybSubject.Company "KeyParties" else [])
            @ (if expects KybCheck.Authentication && not (List.exists isIndividual readable) then [ Gap KybGap.NoIndividualKeyParty ] else [])
            @ (readable |> List.collect (partyFindings expects))
        | _ -> missingBlock expected KybSubject.Company "KeyParties"

    /// The AML flags, by their names in the answer, with the UBO structure's partial ownership flag.
    let private companyMarkers (company: Kyb.KybDataCompanyInformation) =
        [ "PartialOwnershipFlag", company.PartialOwnershipFlag
          "FormationAgent", company.FormationAgent
          "SlpLlpOwned", company.SlpLlpOwned
          "NoPsc", company.NoPsc
          "PscDodging", company.PscDodging
          "Phoenix", company.Phoenix
          "CHDefaultAddress", company.ChDefaultAddress
          "ResidentialAddress", company.ResidentialAddress
          "ResidentialAddressHighVol", company.ResidentialAddressHighVol
          "MailboxAddress", company.MailboxAddress
          "RegisteredAddressChanges", company.RegisteredAddressChanges
          "DirectorNationalityChanges", company.DirectorNationalityChanges
          "NoUKDirIncorp", company.NoUkDirIncorp
          "HighRiskDirIncorp", company.HighRiskDirIncorp
          "NoUKDir", company.NoUkDir
          "HighRiskDir", company.HighRiskDir
          "NoUKPsc", company.NoUkPsc
          "HighRiskPsc", company.HighRiskPsc
          "NoUKUbo", company.NoUkUbo
          "HighRiskUbo", company.HighRiskUbo
          "NoUKKp", company.NoUkKp
          "HighRiskKp", company.HighRiskKp
          "MultiJurDirIncorp", company.MultiJurDirIncorp
          "MultiJurDir", company.MultiJurDir
          "DirHighVolCos", company.DirHighVolCos
          "ConflictingDirCoR", company.ConflictingDirCoR
          "ConflictingDirNat", company.ConflictingDirNat
          "ConflictingDirDoB", company.ConflictingDirDoB
          "ConsistentOverPerformance", company.ConsistentOverPerformance
          "FastMoneyMovement", company.FastMoneyMovement
          "LowAverageBalance", company.LowAverageBalance
          "RoundTransactionsLatestMonth", company.RoundTransactionsLatestMonth
          "RoundTransactionsLatestYear", company.RoundTransactionsLatestYear
          "RoundTransactionsFull", company.RoundTransactionsFull
          "MisalignedInflows", company.MisalignedInflows
          "MultipleCurrentAccounts", company.MultipleCurrentAccounts
          "MultipleBanks", company.MultipleBanks
          "MultipleBanksNewIncorp", company.MultipleBanksNewIncorp
          "TradingWhilstDormant", company.TradingWhilstDormant ]
        |> List.map (fun (field, value) -> field, value, KybHitKind.AdverseMarker field)

    let private companyFindings (expects: KybCheck -> bool) (block: Kyb.KybDataCompanyInformation) =
        let screening = expects KybCheck.Screening
        let markers = expects KybCheck.AdverseMarkers
        let subject = KybSubject.Company
        match Option.ofObj block with
        | None -> missingBlock (screening || markers) subject "CompanyInformation"
        | Some company ->
            // DataQualityFlag excuses what a company not found leaves absent, never a hit the block still holds.
            let quality = text company.DataQualityFlag
            let screening = screening && Option.isNone quality
            let markers = markers && Option.isNone quality
            (quality |> Option.map (fun value -> Gap(KybGap.CompanyNotFound value)) |> Option.toList)
            @ summary screening subject "Lexisnexis" company.Lexisnexis
            @ flags "N" (screening || markers) subject [ "DissolvedFlag", company.DissolvedFlag, KybHitKind.Dissolved ]
            @ (text company.DissolutionDate |> Option.map (fun _ -> hit subject KybHitKind.Dissolved) |> Option.toList)
            @ flags "N" markers subject [ "ExceptionFlag", company.ExceptionFlag, KybHitKind.OwnershipException(optional company.ExceptionDescription) ]
            @ flags "N" markers subject (companyMarkers company)
            @ count markers subject "DirectorNumDisqualifications" company.DirectorNumDisqualifications

    let private matching (block: Kyb.KybDataMatching) =
        match Option.ofObj block with
        | None -> []
        | Some found ->
            let matched =
                match text found.Matched, found.NumberOfMatches with
                | Some value, Some matches when equalsText "yes" value && matches <> 1. -> [ Gap(KybGap.MatchCount matches) ]
                | Some value, _ when equalsText "yes" value -> []
                | Some value, _ -> [ Gap(KybGap.CompanyNotFound value) ]
                | None, _ -> missingField true KybSubject.Company "Matched"
            matched
            @ (items found.MatchingDetails
               |> List.collect (fun source ->
                   flags "N" false KybSubject.Company [ "FcaDisciplinaryActionTaken", source.FcaDisciplinaryActionTaken, KybHitKind.AdverseMarker "FcaDisciplinaryActionTaken" ]))

    let private errorCodes (codes: string[]) =
        codes
        |> Option.ofObj
        |> Option.defaultValue [||]
        |> List.ofArray
        |> List.map (fun code -> Gap(KybGap.ErrorCode(if isNull code then "" else code.Trim())))

    /// A JSON boolean arrives here as its text. Absent says nothing: the check's own block still has to be there.
    let private suppressed expected subject field (value: string option) =
        match text value with
        | Some value when equalsText "true" value -> if expected then [ Gap(KybGap.Suppressed field) ] else []
        | Some value when equalsText "false" value -> []
        | Some value -> [ Gap(KybGap.UnknownValue(subject, field, value)) ]
        | None -> []

    /// An empty list asks for every check, so that it can't make a check-free clear.
    let private expecting (requested: KybCheck list) =
        match requested with
        | [] -> fun (_: KybCheck) -> true
        | requested -> fun check -> List.contains check requested

    let private outcome findings =
        let hits = findings |> List.choose (function Hit found -> Some found | Gap _ -> None) |> List.distinct
        let gaps = findings |> List.choose (function Gap gap -> Some gap | Hit _ -> None) |> List.distinct
        match hits, gaps with
        | [], [] -> KybOutcome.Clear
        | [], gaps -> KybOutcome.NotScreened gaps
        | hits, gaps -> KybOutcome.NeedsReview(hits, gaps)

    /// The outcome of a company data answer, for a client that asked for only these checks. Hits, error codes and failed
    /// calls count whatever was asked for; only an absent or suppressed result of a check not asked for is let go.
    let ofResponseFor (requested: KybCheck list) (response: Kyb.KybDataResponse) =
        let expects = expecting requested
        match response |> Option.ofObj |> Option.bind (fun r -> Option.ofObj r.Results) with
        | None -> KybOutcome.NotScreened [ KybGap.NoResults ]
        | Some results ->
            let parties = items results.KeyParties |> List.map keyParty
            let subjectOf (row: Kyb.KybDataLexisNexisDetails_Item) =
                let links (id: string option) (partyId: string voption) =
                    match text id, partyId with
                    | Some id, ValueSome partyId -> id = partyId
                    | _ -> false
                parties
                |> List.tryFind (fun party -> links row.UboShareholderId party.UboShareholderId || links row.Pscid party.PscId || links row.DirectorUrn party.DirectorUrn)
                |> Option.map KybSubject.KeyParty
                |> Option.defaultValue (KybSubject.Unlinked(optional row.InputName))
            let identified =
                [ text results.BusinessRef
                  results.Matching |> Option.ofObj |> Option.bind (fun found -> text found.BestMatchedId)
                  results.CompanyInformation |> Option.ofObj |> Option.bind (fun block -> text block.BusinessName) ]
                |> List.exists Option.isSome
            errorCodes results.ErrorCodes
            @ (if identified then [] else [ Gap KybGap.CompanyNotIdentified ])
            @ suppressed (expects KybCheck.Screening) KybSubject.Company "LnScreeningSuppress" results.LnScreeningSuppress
            @ suppressed (expects KybCheck.Authentication) KybSubject.Company "IdVerificationSuppress" results.IdVerificationSuppress
            @ matching results.Matching
            @ companyFindings expects results.CompanyInformation
            @ keyParties expects results.KeyParties
            @ matchRows KybSubject.Company subjectOf results.LexisNexisDetails
            |> outcome

    /// The outcome of a company data answer with every check expected. A client not licensed for one, or a request that
    /// leaves a block out, reads NotScreened naming it: see ofResponseFor.
    let ofResponse (response: Kyb.KybDataResponse) = ofResponseFor KybCheck.All response

    /// The outcome of a people data answer, for a client that asked for only these checks. Hits are the Person's.
    let ofPeopleResponseFor (requested: KybCheck list) (response: Kyb.KybDataPeopleResponse) =
        let expects = expecting requested
        match response |> Option.ofObj |> Option.bind (fun r -> Option.ofObj r.Results) with
        | None -> KybOutcome.NotScreened [ KybGap.NoResults ]
        | Some results ->
            let person = KybSubject.Person
            errorCodes results.ErrorCodes
            @ suppressed (expects KybCheck.Screening) person "LnScreeningSuppress" results.LnScreeningSuppress
            @ suppressed (expects KybCheck.Authentication) person "IdVerificationSuppress" results.IdVerificationSuppress
            @ summary (expects KybCheck.Screening) person "ClientPeopleScreeningLexisNexis" results.ClientPeopleScreeningLexisNexis
            @ crossCore (expects KybCheck.Authentication) person "ClientPeopleScreeningCrosscore" results.ClientPeopleScreeningCrosscore
            @ matchRows person (fun _ -> person) results.LexisNexisDetails
            |> outcome

    /// The outcome of a people data answer with screening and authentication expected.
    let ofPeopleResponse (response: Kyb.KybDataPeopleResponse) = ofPeopleResponseFor KybCheck.All response
