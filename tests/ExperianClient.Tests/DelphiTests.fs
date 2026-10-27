module DelphiTests

open System
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open Xunit
open Experian
open FakeExperian

let private casesOf<'T> () =
    FSharpType.GetUnionCases typeof<'T> |> Array.map (fun case -> FSharpValue.MakeUnion(case, [||]) :?> 'T) |> List.ofArray

let private knownSearchTypes =
    (casesOf<Quotation> () |> List.map Quotation) @ (casesOf<CreditProduct> () |> List.map CreditApplication)

[<Fact>]
let ``every typed search type has its own CAPS code and reads back from it`` () =
    let codes = knownSearchTypes |> List.map SearchType.code
    Assert.Equal(codes.Length, (List.distinct codes).Length)
    Assert.All(codes, fun code -> Assert.Matches("^[A-Z]{2}$", code))
    for searchType in knownSearchTypes do
        Assert.Equal(searchType, SearchType.ofCode (SearchType.code searchType))

[<Fact>]
let ``quotations are soft, credit applications hard, other codes unclassified`` () =
    Assert.Equal("QL", SearchType.code (Quotation Quotation.PersonalLoan))
    Assert.Equal("CD", SearchType.code (CreditApplication CreditProduct.DebtConsolidationLoan))
    Assert.All(casesOf<Quotation> (), fun q -> Assert.Equal(Soft, SearchType.footprint (Quotation q)))
    Assert.All(casesOf<CreditProduct> (), fun p -> Assert.Equal(Hard, SearchType.footprint (CreditApplication p)))
    Assert.Equal(OtherSearch "EQ", SearchType.ofCode " eq ")
    Assert.Equal(Unclassified, SearchType.footprint (OtherSearch "EQ"))
    Assert.Equal(CreditApplication CreditProduct.PersonalLoan, SearchType.ofCode "pl")

[<Fact>]
let ``setSearchType writes the CAPS code, creating the Application when missing`` () =
    let input = ApiTests.sandboxApplication ()
    Api.DelphiSelect.setSearchType (Quotation Quotation.PersonalLoan) input |> ignore
    Assert.Equal(Some "QL", input.Submission.Application.ApplicationType)
    Assert.Equal(Some true, input.Submission.Application.SearchConsent)
    input.Submission.Application <- null
    let returned = Api.DelphiSelect.setSearchType (CreditApplication CreditProduct.DebtConsolidationLoan) input
    Assert.Same(input, returned)
    Assert.Equal(Some "CD", input.Submission.Application.ApplicationType)

[<Fact>]
let ``convertQuoteAsync puts a credit product to the quote's quotetofull`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "{}"))
        let! result =
            Api.DelphiSelect.convertQuoteAsync (clientWith handler) "0123456789" CreditProduct.DebtConsolidationLoan (ValueSome "A1234") ct
        expectOk result |> ignore
        let call = Assert.Single handler.ApiCalls
        Assert.Equal("PUT https://sandbox-uk-api.experian.com/risk/consumer/v2/dataselect/application/0123456789/quotetofull", $"{call.Method} {call.Uri}")
        Assert.Equal("""{"Submission":{"ClientData":{"ClientAccountNumber":"A1234"},"Application":{"ApplicationType":"CD"}}}""", call.Body)
    }

/// Fictitious accounts shaped like the sandbox answer: the applicant's open card, an associate's loan, the applicant's
/// settled store card, a defaulted loan under the applicant's alias, and an account with no match details.
let private consumerData =
    """{"Response":{"FullConsumerData":{"ConsumerData":{"CAIS":[
        {"ApplicantIndicator":"1","CAISDetails":[
          {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"1","MatchType":"I"},"CreditLimit":{"Amount":"£1,900","Caption":"C"},"Balance":{"Amount":"£1140"},
           "CAISAccStartDate":"2017-05-30","LastUpdatedDate":"2019-02-24","AccountStatus":"A","WorstStatus":"0","Payment":"£45",
           "CompanyType":"04","AccountType":"05","AccountNumber":"4929000000006","SupplyCompanyName":"EXAMPLE CARD CO","JointAccount":" "},
          {"MatchDetails":{"MatchTo":"3","BureauRefCategory":"2","MatchType":"F"},"Balance":{"Amount":"£800"},"AccountStatus":"A","SupplyCompanyName":"ASSOCIATE LENDER"},
          {"MatchDetails":{"MatchTo":"1"},"Balance":{"Narrative":"S"},"SettlementDate":"2015-02-08","SupplyCompanyName":"EXAMPLE STORES"}]},
        {"ApplicantIndicator":"1","CAISDetails":[
          {"MatchDetails":{"MatchTo":"2"},"Balance":{"Amount":"£12256"},"AccountStatus":"F","WorstStatus":"8","SupplyCompanyName":"EXAMPLE LOANS","JointAccount":"M"},
          {"Balance":{"Amount":"£50"},"AccountStatus":"A"}]}]}}}}"""

let private accounts () =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")
    let output = (clientWith handler).DelphiSelect.Deserialize(consumerData, typeof<DelphiSelect.Output>) :?> DelphiSelect.Output
    (CreditAccounts.ofOutput output).Value

[<Fact>]
let ``credit accounts are read with lender, holder, status and amounts in pounds`` () =
    let all = accounts ()
    Assert.Equal(5, all.Length)
    let card = all[0]
    Assert.Equal(ValueSome "EXAMPLE CARD CO", card.Lender)
    Assert.Equal(ValueSome "4929000000006", card.AccountNumber)
    Assert.Equal(MainApplicant, card.Holder)
    Assert.Equal(Active, card.Status)
    Assert.Equal(ValueSome 1140m, card.Balance)
    Assert.Equal(ValueSome 1900m, card.CreditLimit)
    Assert.Equal(ValueSome 45m, card.Payment)
    Assert.Equal(ValueSome(DateOnly(2017, 5, 30)), card.StartDate)
    Assert.Equal(ValueSome "05", card.AccountTypeCode)
    Assert.False card.Joint
    Assert.Equal(AssociateOfMainApplicant, all[1].Holder)
    Assert.Equal(Settled, all[2].Status)
    Assert.Equal(ValueNone, all[2].Balance)
    Assert.Equal(ValueSome(DateOnly(2015, 2, 8)), all[2].SettlementDate)
    Assert.Equal(AliasOfMainApplicant, all[3].Holder)
    Assert.Equal(Defaulted, all[3].Status)
    Assert.True all[3].Joint
    Assert.Equal(Unmatched "", all[4].Holder)

[<Fact>]
let ``outstanding keeps the applicant's own unsettled balances, not associates' or settled ones`` () =
    let debts = CreditAccounts.outstanding (accounts ())
    Assert.Equal<string list>([ "EXAMPLE CARD CO"; "EXAMPLE LOANS" ], debts |> List.map (fun d -> d.Lender.Value))
    Assert.Equal(13396m, CreditAccounts.totalOwed debts)

[<Fact>]
let ``a response without consumer data is ValueNone, not an empty debt list`` () =
    Assert.Equal(ValueNone, CreditAccounts.ofOutput (DelphiSelect.Output()))
    Assert.Equal(ValueNone, CreditAccounts.ofOutput null)
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")
    let noAccounts =
        (clientWith handler).DelphiSelect.Deserialize("""{"Response":{"FullConsumerData":{"ConsumerData":{}}}}""", typeof<DelphiSelect.Output>)
        :?> DelphiSelect.Output
    Assert.Equal(ValueSome [], CreditAccounts.ofOutput noAccounts)

/// A default still owing £640 (Balance shows the £900 at default), and a satisfied default.
let private defaults =
    """{"Response":{"FullConsumerData":{"ConsumerData":{"CAIS":[{"CAISDetails":[
        {"MatchDetails":{"MatchTo":"1"},"AccountStatus":"F","Balance":{"Amount":"£900"},"CurrentDefBalance":{"Amount":"£640"},
         "DelinquentBalance":{"Amount":"£640"},"SupplyCompanyName":"DEFAULTED LOANS"},
        {"MatchDetails":{"MatchTo":"1"},"AccountStatus":"F","Balance":{"Amount":"£300"},"CurrentDefBalance":{"Amount":"£0","Narrative":"T"},
         "DefaultSatisfyDate":"2021-03-01","SupplyCompanyName":"PAID DEFAULT CO"}]}]}}}}"""

[<Fact>]
let ``a default owes its current default balance, and a satisfied default is settled`` () =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")
    let output = (clientWith handler).DelphiSelect.Deserialize(defaults, typeof<DelphiSelect.Output>) :?> DelphiSelect.Output
    let all = (CreditAccounts.ofOutput output).Value
    Assert.Equal(Defaulted, all[0].Status)
    Assert.Equal(ValueSome 900m, all[0].Balance)
    Assert.Equal(ValueSome 640m, all[0].DefaultBalance)
    Assert.Equal(ValueSome 640m, all[0].DelinquentBalance)
    Assert.Equal(ValueSome 640m, CreditAccounts.amountOwed all[0])
    Assert.Equal(Settled, all[1].Status)
    let debts = CreditAccounts.outstanding all
    Assert.Equal<string list>([ "DEFAULTED LOANS" ], debts |> List.map (fun d -> d.Lender.Value))
    Assert.Equal(640m, CreditAccounts.totalOwed debts)

[<Fact>]
let ``null inputs do not throw`` () =
    Assert.Equal(OtherSearch "", SearchType.ofCode null)
    let input = Api.DelphiSelect.setSearchType (Quotation Quotation.PersonalLoan) (DelphiSelect.Input())
    Assert.Equal(Some "QL", input.Submission.Application.ApplicationType)

[<Fact>]
let ``amounts read with or without the pound sign and thousands separators`` () =
    Assert.Equal(ValueSome 12256m, CreditAccounts.pounds (Some "£12256"))
    Assert.Equal(ValueSome 1140m, CreditAccounts.pounds (Some "1,140"))
    Assert.Equal(ValueSome -20m, CreditAccounts.pounds (Some "-£20"))
    Assert.Equal(ValueNone, CreditAccounts.pounds (Some ""))
    Assert.Equal(ValueNone, CreditAccounts.pounds (Some "N/A"))
    Assert.Equal(ValueNone, CreditAccounts.pounds None)
