module ReportsTests

open System
open Xunit
open Experian
open FakeExperian

let private delphi (json: string) =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")
    (clientWith handler).DelphiSelect.Deserialize(json, typeof<DelphiSelect.Output>) :?> DelphiSelect.Output

let private kyc (json: string) =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")
    (clientWith handler).Kyc.Deserialize(json, typeof<Kyc.ResponseMessage>) :?> Kyc.ResponseMessage

/// Fictitious: applicant 1 referred with a high-risk rule, applicant 2 authenticated with the index not used.
let private authPlus =
    """{"Response":{"AuthPlusResults":[
        {"ApplicantIdentifier":1,"AuthConsumer":{"AuthPlusRef":"REF0000001",
          "DecisionData":{"AuthPlusDecision":"RA00","AuthPlusDecText":"Caution. One or more high-risk conditions applies","AuthenticationIndex":"45","AuthIndexText":"Index text"},
          "IDConfirmedFlag":{"IDConfirmedLevel":"1","IDConfirmLevelText":"Confirmed at Level 1"}},
         "HighRiskPolRuleCnt":1,"HighRiskPolicyRules":[{"HighRiskPolRuleID":"U003","HighRiskPolRuleText":"CIFAS data present at a supplied Address"}]},
        {"ApplicantIdentifier":2,"AuthConsumer":{"DecisionData":{"AuthPlusDecision":"AU05","AuthenticationIndex":"-98"}}}]}}"""

[<Fact>]
let ``AuthPlus results read the decision, index, confirmation and high-risk rules`` () =
    let results = AuthPlus.ofOutput (delphi authPlus)
    Assert.Equal(2, results.Length)
    let first = results[0]
    Assert.Equal(ValueSome 1, first.ApplicantIdentifier)
    Assert.Equal(ValueSome "REF0000001", first.Reference)
    Assert.Equal(Refer 0, first.Decision)
    Assert.Equal(ValueSome "Caution. One or more high-risk conditions applies", first.DecisionText)
    Assert.Equal(ValueSome(AuthenticationIndex.Index 45), first.Index)
    Assert.Equal(ValueSome "1", first.ConfirmedLevel)
    Assert.Equal<HighRiskRule list>([ { Id = ValueSome "U003"; Text = ValueSome "CIFAS data present at a supplied Address" } ], first.HighRiskRules)
    Assert.Equal(Authenticated 5, results[1].Decision)
    Assert.Equal(ValueSome AuthenticationIndex.NotUsed, results[1].Index)
    Assert.Empty(results[1].HighRiskRules)

[<Fact>]
let ``AuthPlus decision codes`` () =
    Assert.Equal(Authenticated 12, AuthPlus.decision (Some "AU12"))
    Assert.Equal(NotAuthenticated 3, AuthPlus.decision (Some "NA03"))
    Assert.Equal(Refer 0, AuthPlus.decision (Some "RA00"))
    Assert.Equal(NoDecision, AuthPlus.decision (Some "    "))
    Assert.Equal(NoDecision, AuthPlus.decision None)
    Assert.Equal(OtherAuthPlusDecision "XX99", AuthPlus.decision (Some "XX99"))
    Assert.Equal(OtherAuthPlusDecision "AU", AuthPlus.decision (Some "AU"))
    Assert.Empty(AuthPlus.ofOutput (DelphiSelect.Output()))

/// Fictitious: the main applicant has an IVA, secured arrears and CCJs up to £700; the client is not set up for the
/// joint applicant's ICH.
let private impaired =
    """{"Response":{"ConsumerSummary":{"PremiumValueData":{"ImpairedCH":{
        "NDMAICH":"Y","NDMASECARR":"A","NDMAUNSECARR":"N","NDMACCJ":14,"NDMAIVA":"V","NDMABankrupt":"N",
        "NDJAICH":"Z","NDJASECARR":"Z","NDJACCJ":-9999,"NDJAIVA":"Z","NDJABankrupt":"Z"}}}}}"""

[<Fact>]
let ``impaired credit history reads per applicant, ValueNone when not configured`` () =
    let history = (PublicRecords.impairedCredit (delphi impaired)).Value
    let main = history.MainApplicant.Value
    Assert.True main.Impaired
    Assert.True main.Iva
    Assert.False main.Bankruptcy
    Assert.True main.SecuredArrears
    Assert.False main.UnsecuredArrears
    Assert.Equal(ValueSome(CcjTotal.UpTo 700), main.Ccj)
    Assert.Equal(ValueNone, history.JointApplicant)
    Assert.Equal(ValueNone, PublicRecords.impairedCredit (DelphiSelect.Output()))

[<Fact>]
let ``CCJ totals by band`` () =
    let ccj code =
        let json = $"""{{"Response":{{"ConsumerSummary":{{"PremiumValueData":{{"ImpairedCH":{{"NDMAICH":"N","NDMACCJ":{code}}}}}}}}}}}"""
        (PublicRecords.impairedCredit (delphi json)).Value.MainApplicant.Value.Ccj
    Assert.Equal(ValueSome CcjTotal.NoCcj, ccj 0)
    Assert.Equal(ValueSome CcjTotal.UpTo500, ccj 1)
    Assert.Equal(ValueSome(CcjTotal.UpTo 550), ccj 11)
    Assert.Equal(ValueSome CcjTotal.NoTrace, ccj -1)
    Assert.Equal(ValueNone, ccj -9999)
    Assert.Equal(ValueSome(CcjTotal.OtherCcjCode 5), ccj 5)

/// Fictitious: the applicant's unsatisfied judgment, an associate's judgment, and the applicant's satisfied one.
let private publicInfo =
    """{"Response":{"FullConsumerData":{"ConsumerData":{"PublicInformation":[{"ApplicantIndicator":"1","PublicInfoDetails":[
        {"MatchDetails":{"MatchTo":"1"},"InformationDate":"2024-03-01","InformationType":"02","AmountPounds":"£716","AmountPence":50,"CourtPlaintiff":"EXAMPLE COUNTY COURT","DataSource":"LCH"},
        {"MatchDetails":{"MatchTo":"3"},"InformationDate":"2018-09-03","InformationType":"02","AmountPounds":"£300"},
        {"MatchDetails":{"MatchTo":"1"},"InformationDate":"2019-01-10","InformationType":"02","AmountPounds":"£120","SatisfactionDate":"2020-05-01"}]}]}}}}"""

[<Fact>]
let ``public records read type, holder, amount in pounds and pence, and satisfaction`` () =
    let records = (PublicRecords.ofOutput (delphi publicInfo)).Value
    Assert.Equal(3, records.Length)
    let judgment = records[0]
    Assert.Equal(ValueSome "02", judgment.TypeCode)
    Assert.Equal(MainApplicant, judgment.Holder)
    Assert.Equal(ValueSome(DateOnly(2024, 3, 1)), judgment.Date)
    Assert.Equal(ValueSome 716.5m, judgment.Amount)
    Assert.Equal(ValueSome "EXAMPLE COUNTY COURT", judgment.Plaintiff)
    Assert.Equal(ValueNone, judgment.SatisfiedOn)
    Assert.Equal(AssociateOfMainApplicant, records[1].Holder)
    Assert.Equal(ValueSome(DateOnly(2020, 5, 1)), records[2].SatisfiedOn)
    Assert.Equal<PublicRecord list>([ judgment ], PublicRecords.unsatisfied records)
    Assert.Equal(ValueNone, PublicRecords.ofOutput (DelphiSelect.Output()))

/// Fictitious: income estimates for the main applicant, no CATO data for the joint applicant, verification bands and
/// debt ratios.
let private affordability =
    """{"Response":{"ConsumerSummary":{"PremiumValueData":{
        "AffordabilityIQ":{"AFFV01":90,"AFFV02":30,"AFFE01":30000,"AFFE02":24000,"AFFE03":2000,"AFFE04":-2,"AFFE07":45000},
        "AffordabilityIQ2":{"DTI2":{"SP_I_56":45,"SP_I_57":-1,"SP_I_59":12}}}}}}"""

[<Fact>]
let ``affordability reads income estimates, verification and debt ratios`` () =
    let a = (Affordability.ofOutput (delphi affordability)).Value
    Assert.Equal(ValueSome(IncomeEstimate.Pounds 30000), a.MainApplicant.GrossAnnual)
    Assert.Equal(ValueSome(IncomeEstimate.Pounds 24000), a.MainApplicant.NetAnnual)
    Assert.Equal(ValueSome(IncomeEstimate.Pounds 2000), a.MainApplicant.NetMonthly)
    Assert.Equal(ValueSome IncomeEstimate.NoCatoData, a.JointApplicant.GrossAnnual)
    Assert.Equal(ValueNone, a.JointApplicant.NetMonthly)
    Assert.Equal(ValueSome(IncomeEstimate.Pounds 45000), a.Household.GrossAnnual)
    Assert.Equal(ValueSome IncomeVerification.MatchesEstimate, a.MainApplicantVerification)
    Assert.Equal(ValueSome(IncomeVerification.AboveEstimate 161), a.HouseholdVerification)
    Assert.Equal(ValueSome(Percentage.Percent 45), a.UnsecuredDebtToIncome)
    Assert.Equal(ValueSome(Percentage.Unavailable -1), a.SecuredDebtToIncome)
    Assert.Equal(ValueSome(Percentage.Percent 12), a.RevolvingCommitmentsToIncome)
    Assert.Equal(ValueNone, a.NonRevolvingCommitmentsToIncome)

[<Fact>]
let ``income verification bands follow the spec`` () =
    let band code =
        let json = $"""{{"Response":{{"ConsumerSummary":{{"PremiumValueData":{{"AffordabilityIQ":{{"AFFV01":{code}}}}}}}}}}}"""
        (Affordability.ofOutput (delphi json)).Value.MainApplicantVerification.Value
    Assert.Equal(IncomeVerification.AboveEstimate 201, band 0)
    Assert.Equal(IncomeVerification.AboveEstimate 181, band 10)
    Assert.Equal(IncomeVerification.AboveEstimate 171, band 20)
    Assert.Equal(IncomeVerification.AboveEstimate 111, band 80)
    Assert.Equal(IncomeVerification.BelowEstimate, band 99)
    Assert.Equal(IncomeVerification.MissingSuppliedIncome, band -5)
    Assert.Equal(IncomeVerification.OtherVerificationCode 55, band 55)

/// Fictitious KYC answer: referred, with a matched identity rule, an unmatched one, and a matched PEP rule.
let private kycAnswer =
    """{"responseHeader":{"overallResponse":{"decision":"REFER","decisionText":"Refer","decisionReasons":["PEP match found"],"recommendedNextActions":["Review the PEP match"]}},
        "clientResponsePayload":{"decisionElements":[
          {"serviceName":"uk-idv","decisionReason":"basic-rules-matrix","score":1,"rules":[
            {"ruleId":"5200","ruleName":"V_eID_2ScNAD","ruleScore":1,"ruleText":"Two or more data sources with name, address and DoB"},
            {"ruleId":"7100","ruleName":"V_EId_2PNA","ruleScore":0,"ruleText":"Two or more primary sources without DoB"}]},
          {"serviceName":"uk-pepsanc","decision":"REFER","rules":[{"ruleId":"PSE9200","ruleName":"LN_PEP_EN_FULLDOB_INT","ruleScore":1,"ruleText":"Exact name and full DoB, international PEP"}]}]}}"""

[<Fact>]
let ``KYC outcome reads the decision, reasons and fired rules per service`` () =
    let outcome = KycOutcome.ofResponse (kyc kycAnswer)
    Assert.Equal(KycDecision.Refer, outcome.Decision)
    Assert.Equal(ValueSome "Refer", outcome.Text)
    Assert.Equal<string list>([ "PEP match found" ], outcome.Reasons)
    Assert.Equal<string list>([ "Review the PEP match" ], outcome.NextActions)
    Assert.Equal<string voption list>([ ValueSome "uk-idv"; ValueSome "uk-pepsanc" ], outcome.Services |> List.map _.Name)
    Assert.Equal<string voption list>([ ValueSome "5200" ], outcome.Services[0].FiredRules |> List.map _.Id)
    Assert.Equal(ValueSome "REFER", outcome.Services[1].Decision)
    Assert.Equal<string voption list>([ ValueSome "PSE9200" ], outcome.Services[1].FiredRules |> List.map _.Id)

[<Fact>]
let ``KYC decision codes and an empty answer`` () =
    let decision code = (KycOutcome.ofResponse (kyc $"""{{"responseHeader":{{"overallResponse":{{"decision":"{code}"}}}}}}""")).Decision
    Assert.Equal(KycDecision.Continue, decision "CONTINUE")
    Assert.Equal(KycDecision.Stop, decision "Stop")
    Assert.Equal(KycDecision.NoDecision, decision "")
    Assert.Equal(KycDecision.OtherDecision "ACCEPT", decision "ACCEPT")
    let empty = KycOutcome.ofResponse null
    Assert.Equal(KycDecision.NoDecision, empty.Decision)
    Assert.Empty empty.Services

[<Fact>]
let ``the joint applicant's history is read only when the answer lists two applicants`` () =
    let ich = """"ImpairedCH":{"NDMAICH":"N","NDJAICH":"Y","NDJABankrupt":"B","NDJACCJ":0}"""
    let single = delphi ("""{"Response":{"Applicants":[{"ApplicantIdentifier":1}],"ConsumerSummary":{"PremiumValueData":{""" + ich + "}}}}")
    let joint = delphi ("""{"Response":{"Applicants":[{"ApplicantIdentifier":1},{"ApplicantIdentifier":2}],"ConsumerSummary":{"PremiumValueData":{""" + ich + "}}}}")
    Assert.Equal(ValueNone, (PublicRecords.impairedCredit single).Value.JointApplicant)
    let jointHistory = (PublicRecords.impairedCredit joint).Value.JointApplicant.Value
    Assert.True jointHistory.Bankruptcy
    Assert.True jointHistory.Impaired

[<Fact>]
let ``housekeeping rules 9990 and 9991 are not fired rules`` () =
    let answer =
        """{"responseHeader":{"overallResponse":{"decision":"CONTINUE"}},"clientResponsePayload":{"decisionElements":[
            {"serviceName":"uk-contras","rules":[{"ruleId":"9990","ruleName":"NRF9990","ruleScore":1,"ruleText":"No other rule has fired"}]},
            {"serviceName":"uk-idv","rules":[{"ruleId":"9991","ruleName":"NRF9991","ruleScore":1},{"ruleId":"5200","ruleScore":1}]}]}}"""
    let outcome = KycOutcome.ofResponse (kyc answer)
    Assert.Empty(outcome.Services[0].FiredRules)
    Assert.Equal<string voption list>([ ValueSome "5200" ], outcome.Services[1].FiredRules |> List.map _.Id)
