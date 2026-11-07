module DelphiTests

open System
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open Xunit
open Experian
open FakeExperian

let private casesOf<'T> () =
    FSharpType.GetUnionCases typeof<'T>
    |> Array.map (fun case -> FSharpValue.MakeUnion(case, [||]) :?> 'T)
    |> List.ofArray

let private knownSearchTypes =
    (casesOf<Quotation>() |> List.map Quotation)
    @ (casesOf<CreditProduct>() |> List.map CreditApplication)

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
    Assert.All(casesOf<Quotation>(), fun q -> Assert.Equal(Soft, SearchType.footprint (Quotation q)))
    Assert.All(casesOf<CreditProduct>(), fun p -> Assert.Equal(Hard, SearchType.footprint (CreditApplication p)))
    Assert.Equal(OtherSearch "EQ", SearchType.ofCode " eq ")
    Assert.Equal(Unclassified, SearchType.footprint (OtherSearch "EQ"))
    Assert.Equal(CreditApplication CreditProduct.PersonalLoan, SearchType.ofCode "pl")

[<Fact>]
let ``setSearchType writes the CAPS code, creating the Application when missing`` () =
    let input = ApiTests.sandboxApplication ()

    Api.DelphiSelect.setSearchType (Quotation Quotation.PersonalLoan) input
    |> ignore

    Assert.Equal(Some "QL", input.Submission.Application.ApplicationType)
    Assert.Equal(Some true, input.Submission.Application.SearchConsent)
    input.Submission.Application <- null

    let returned =
        Api.DelphiSelect.setSearchType (CreditApplication CreditProduct.DebtConsolidationLoan) input

    Assert.Same(input, returned)
    Assert.Equal(Some "CD", input.Submission.Application.ApplicationType)

[<Fact>]
let ``convertQuoteAsync puts a credit product to the quote's quotetofull`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "{}"))

        let! result =
            Api.DelphiSelect.convertQuoteAsync
                (clientWith handler)
                "0123456789"
                CreditProduct.DebtConsolidationLoan
                (ValueSome "A1234")
                ct

        expectOk result |> ignore
        let call = Assert.Single handler.ApiCalls

        Assert.Equal(
            "PUT https://sandbox-uk-api.experian.com/risk/consumer/v2/dataselect/application/0123456789/quotetofull",
            $"{call.Method} {call.Uri}"
        )

        Assert.Equal(
            """{"Submission":{"ClientData":{"ClientAccountNumber":"A1234"},"Application":{"ApplicationType":"CD"}}}""",
            call.Body
        )
    }

/// Fictitious accounts shaped like the sandbox answer: the applicant's open card, an associate's loan, the applicant's
/// settled store card, a defaulted loan under the applicant's alias, an account with no match details, and open loans of
/// a same-surname household, of address-based data and with no category, all three matched to the applicant.
let private consumerData =
    """{"Response":{"FullConsumerData":{"ConsumerData":{"CAIS":[
        {"ApplicantIndicator":"1","CAISDetails":[
          {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"1","MatchType":"I"},"CreditLimit":{"Amount":"£1,900","Caption":"C"},"Balance":{"Amount":"£1140"},
           "CAISAccStartDate":"2017-05-30","LastUpdatedDate":"2019-02-24","AccountStatus":"A","WorstStatus":"0","Payment":"£45",
           "CompanyType":"04","AccountType":"05","AccountNumber":"4929000000006","SupplyCompanyName":"EXAMPLE CARD CO","JointAccount":" "},
          {"MatchDetails":{"MatchTo":"3","BureauRefCategory":"2","MatchType":"F"},"Balance":{"Amount":"£800"},"AccountStatus":"A","SupplyCompanyName":"ASSOCIATE LENDER"},
          {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"1"},"Balance":{"Narrative":"S"},"SettlementDate":"2015-02-08","SupplyCompanyName":"EXAMPLE STORES"}]},
        {"ApplicantIndicator":"1","CAISDetails":[
          {"MatchDetails":{"MatchTo":"2","BureauRefCategory":"4"},"Balance":{"Amount":"£12256"},"AccountStatus":"F","WorstStatus":"8","SupplyCompanyName":"EXAMPLE LOANS","JointAccount":"M"},
          {"Balance":{"Amount":"£50"},"AccountStatus":"A"},
          {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"3"},"Balance":{"Amount":"£70"},"AccountStatus":"A","SupplyCompanyName":"HOUSEHOLD LOANS"},
          {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"5"},"Balance":{"Amount":"£80"},"AccountStatus":"A","SupplyCompanyName":"ADDRESS LOANS"},
          {"MatchDetails":{"MatchTo":"1"},"Balance":{"Amount":"£90"},"AccountStatus":"A","SupplyCompanyName":"UNCATEGORISED LOANS"}]}]}}}}"""

let private accounts () =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")

    let output =
        (clientWith handler).DelphiSelect.Deserialize(consumerData, typeof<DelphiSelect.Output>) :?> DelphiSelect.Output

    (CreditAccounts.ofOutput output).Value

[<Fact>]
let ``credit accounts are read with lender, holder, status and amounts in pounds`` () =
    let all = accounts ()
    Assert.Equal(8, all.Length)
    let card = all[0]
    Assert.Equal(ValueSome "EXAMPLE CARD CO", card.Lender)
    Assert.Equal(ValueSome "4929000000006", card.AccountNumber)
    Assert.Equal(MainApplicant, card.Holder)
    Assert.Equal(SamePerson, card.Match)
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
    Assert.Equal(PotentialAlias, all[3].Match)
    Assert.Equal(Unmatched "", all[4].Holder)
    Assert.Equal(UnknownMatch "", all[4].Match)
    Assert.Equal(UnassociatedSameFamily, all[5].Match)
    Assert.Equal(AddressBasedData, all[6].Match)
    Assert.Equal(UnknownMatch "", all[7].Match)

[<Fact>]
let ``only the applicant's records as the same person or a potential alias are their own`` () =
    let own = accounts () |> List.filter CreditAccounts.isOwn

    Assert.Equal<string list>(
        [ "EXAMPLE CARD CO"; "EXAMPLE STORES"; "EXAMPLE LOANS" ],
        own |> List.map (fun a -> a.Lender.Value)
    )

[<Fact>]
let ``outstanding keeps the applicant's own unsettled balances, not other people's or settled ones`` () =
    let debts = CreditAccounts.outstanding (accounts ())
    Assert.Equal<string list>([ "EXAMPLE CARD CO"; "EXAMPLE LOANS" ], debts |> List.map (fun d -> d.Lender.Value))
    Assert.Equal(13396m, CreditAccounts.totalOwed debts)

[<Fact>]
let ``a response without consumer data is ValueNone, not an empty debt list`` () =
    Assert.Equal(ValueNone, CreditAccounts.ofOutput (DelphiSelect.Output()))
    Assert.Equal(ValueNone, CreditAccounts.ofOutput null)

let private answer (consumerData: string) =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")

    (clientWith handler)
        .DelphiSelect.Deserialize(
            $"""{{"Response":{{"FullConsumerData":{{"ConsumerData":{consumerData}}}}}}}""",
            typeof<DelphiSelect.Output>
        )
    :?> DelphiSelect.Output

let private accountRecord (matchTo: string) (category: string) =
    $$$"""{"CAIS":[{"CAISDetails":[{"MatchDetails":{"MatchTo":"{{{matchTo}}}","BureauRefCategory":"{{{category}}}"},"Balance":{"Amount":"£5"},"AccountStatus":"A"}]}]}"""

let private rollRecord (matchTo: string) (category: string) =
    $$$"""{"VotersRollPerson":[{"VRollPersonDetails":[{"MatchDetails":{"MatchTo":"{{{matchTo}}}","BureauRefCategory":"{{{category}}}"}}]}]}"""

let private publicRecord (matchTo: string) (category: string) =
    $$$"""{"PublicInformation":[{"PublicInfoDetails":[{"MatchDetails":{"MatchTo":"{{{matchTo}}}","BureauRefCategory":"{{{category}}}"},"InformationType":"02","AmountPounds":"£716"}]}]}"""

[<Theory; InlineData("1", "1"); InlineData("2", "4")>]
let ``an account, electoral roll or public record of the applicant is a file`` (matchTo: string, category: string) =
    Assert.True(CreditAccounts.fileFound (answer (accountRecord matchTo category)))
    Assert.True(CreditAccounts.fileFound (answer (rollRecord matchTo category)))
    Assert.True(CreditAccounts.fileFound (answer (publicRecord matchTo category)))

[<Theory>]
[<InlineData("9", "5")>] // no match, address-based data
[<InlineData("3", "2")>] // an associate
[<InlineData("1", "3")>] // an unassociated same-surname household
[<InlineData("1", "5")>] // address-based data
let ``records of other people are no file`` (matchTo: string, category: string) =
    Assert.False(CreditAccounts.fileFound (answer (accountRecord matchTo category)))
    Assert.False(CreditAccounts.fileFound (answer (rollRecord matchTo category)))
    Assert.False(CreditAccounts.fileFound (answer (publicRecord matchTo category)))

[<Fact>]
let ``an empty answer is no file`` () =
    Assert.False(CreditAccounts.fileFound (answer "{}"))
    Assert.False(CreditAccounts.fileFound (DelphiSelect.Output()))
    Assert.False(CreditAccounts.fileFound null)

[<Fact>]
let ``only a found file has accounts, and a file can have none`` () =
    Assert.Equal(ValueNone, CreditAccounts.ofOutput (answer "{}"))
    Assert.Equal(ValueNone, CreditAccounts.ofOutput (answer (accountRecord "3" "2")))
    Assert.Equal(ValueNone, CreditAccounts.ofOutput (answer (accountRecord "1" "5")))
    Assert.Equal(ValueSome [], CreditAccounts.ofOutput (answer (rollRecord "1" "1")))
    Assert.Equal(ValueSome [], CreditAccounts.ofOutput (answer (publicRecord "1" "1")))
    Assert.Equal(ValueNone, CreditAccounts.ofOutput (answer (publicRecord "3" "2")))
    Assert.Equal(1, (CreditAccounts.ofOutput (answer (accountRecord "1" "1"))).Value.Length)

[<Theory>]
[<InlineData("Rose & Crown (old) £", "Rose   Crown  old")>]
[<InlineData("  12/3-A, O'Neil's *.  ", "12/3-A, O'Neil's *.")>]
let ``plain turns what address patterns refuse into spaces`` (text: string, expected: string) =
    Assert.Equal(expected, DelphiText.plain text)

[<Fact>]
let ``each field is cut to its limit, without a space left at its end`` () =
    let long = String('a', 70)
    Assert.Equal(30, (DelphiText.flat long).Length)
    Assert.Equal(50, (DelphiText.houseName long).Length)
    Assert.Equal(10, (DelphiText.houseNumber long).Length)
    Assert.Equal(60, (DelphiText.street long).Length)
    Assert.Equal(35, (DelphiText.district long).Length)
    Assert.Equal(30, (DelphiText.postTown long).Length)
    Assert.Equal(15, (DelphiText.forename long).Length)
    Assert.Equal(15, (DelphiText.middleName long).Length)
    Assert.Equal(30, (DelphiText.surname long).Length)
    Assert.Equal("Rose Court", DelphiText.limited 11 "Rose Court Hall")
    Assert.Equal("Rose Court", DelphiText.limited 11 " Rose Court ")

[<Fact>]
let ``names keep characters an address would lose`` () =
    Assert.Equal("Seán & Co (Jr)", DelphiText.forename " Seán & Co (Jr) ")
    Assert.Equal("Rose Court", DelphiText.flat "Rose Court£")

/// A default still owing £640 (Balance shows the £900 at default), and a satisfied default.
let private defaults =
    """{"Response":{"FullConsumerData":{"ConsumerData":{"CAIS":[{"CAISDetails":[
        {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"1"},"AccountStatus":"F","Balance":{"Amount":"£900"},"CurrentDefBalance":{"Amount":"£640"},
         "DelinquentBalance":{"Amount":"£640"},"SupplyCompanyName":"DEFAULTED LOANS"},
        {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"1"},"AccountStatus":"F","Balance":{"Amount":"£300"},"CurrentDefBalance":{"Amount":"£0","Narrative":"T"},
         "DefaultSatisfyDate":"2021-03-01","SupplyCompanyName":"PAID DEFAULT CO"}]}]}}}}"""

[<Fact>]
let ``a default owes its current default balance, and a satisfied default is settled`` () =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")

    let output =
        (clientWith handler).DelphiSelect.Deserialize(defaults, typeof<DelphiSelect.Output>) :?> DelphiSelect.Output

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

    let input =
        Api.DelphiSelect.setSearchType (Quotation Quotation.PersonalLoan) (DelphiSelect.Input())

    Assert.Equal(Some "QL", input.Submission.Application.ApplicationType)

[<Fact>]
let ``amounts read with or without the pound sign and thousands separators`` () =
    Assert.Equal(ValueSome 12256m, CreditAccounts.pounds (Some "£12256"))
    Assert.Equal(ValueSome 1140m, CreditAccounts.pounds (Some "1,140"))
    Assert.Equal(ValueSome -20m, CreditAccounts.pounds (Some "-£20"))
    Assert.Equal(ValueNone, CreditAccounts.pounds (Some ""))
    Assert.Equal(ValueNone, CreditAccounts.pounds (Some "N/A"))
    Assert.Equal(ValueNone, CreditAccounts.pounds None)

/// Fictitious file: summary flags, the applicant's current debt management programme and ended arrangement, an
/// associate's deceased marker, a same-surname household's marker matched to the applicant, a two-line notice, and CIFAS
/// markers for the applicant, an alias, an associate and address-based data matched to the applicant.
let private signalsData =
    """{"Response":{"FullConsumerData":{
        "ConsumerDataSummary":{"SummaryDetails":{"SummaryFlags":{"Arrangement":"Y","DebtManagement":"N","CIFAS":"Y","ReportedDeceased":"N","AccountQuery":" "}}},
        "ConsumerData":{
          "CAIS":[{"CAISDetails":[
            {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"1"},"AccountStatus":"A","Balance":{"Amount":"£2400"},"SupplyCompanyName":"PLAN LENDER","SpecialInstrInd":"M","SpecialInstrDet":{"StartDate":"2024-01-01"}},
            {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"1"},"AccountStatus":"A","Balance":{"Amount":"£900"},"SupplyCompanyName":"OLD ARRANGEMENT","SpecialInstrInd":"A","SpecialInstrDet":{"StartDate":"2018-02-02","EndDate":"2019-01-01"}},
            {"MatchDetails":{"MatchTo":"3","BureauRefCategory":"2"},"AccountStatus":"A","SupplyCompanyName":"ASSOCIATE BANK","SpecialInstrInd":"D"},
            {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"3"},"AccountStatus":"A","SupplyCompanyName":"HOUSEHOLD BANK","SpecialInstrInd":"D"},
            {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"1"},"AccountStatus":"A","SupplyCompanyName":"PLAIN CARD","SpecialInstrInd":" "}]}],
          "NOC":[{"NoCDetails":[{"Reference":"NOC0001","NumberOfLines":2,"TextLine":["I was unable to pay while in hospital.","Please contact me before any decision."]}]}],
          "CIFAS":[{"CIFASDetails":[
            {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"1"},"FraudCategory":"00"},
            {"MatchDetails":{"MatchTo":"2","BureauRefCategory":"4"},"FraudCategory":"01"},
            {"MatchDetails":{"MatchTo":"3","BureauRefCategory":"2"},"FraudCategory":"02"},
            {"MatchDetails":{"MatchTo":"1","BureauRefCategory":"5"},"FraudCategory":"03"}]}]}}}}"""

let private signals () =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")

    let output =
        (clientWith handler).DelphiSelect.Deserialize(signalsData, typeof<DelphiSelect.Output>) :?> DelphiSelect.Output

    (CreditFileSignals.ofOutput output).Value

[<Fact>]
let ``summary flags read as a set of file flags`` () =
    Assert.Equal<Set<FileFlag>>(set [ ArrangementFound; CifasFound ], (signals ()).Flags)

[<Fact>]
let ``special instructions are the applicant's own, with their dates`` () =
    let s = signals ()

    Assert.Equal<string list>(
        [ "PLAN LENDER"; "OLD ARRANGEMENT" ],
        s.Instructions |> List.map (fun a -> a.Lender.Value)
    )

    let plan = s.Instructions[0].SpecialInstruction.Value
    Assert.Equal(DebtManagementProgramme, plan.Instruction)
    Assert.Equal(ValueSome(DateOnly(2024, 1, 1)), plan.From)
    Assert.Equal(ValueNone, plan.Until)
    let arrangement = s.Instructions[1].SpecialInstruction.Value
    Assert.Equal(Arrangement, arrangement.Instruction)
    Assert.Equal(ValueSome(DateOnly(2019, 1, 1)), arrangement.Until)

[<Fact>]
let ``only instructions in force today are current`` () =
    let s = signals ()
    let current = CreditFileSignals.currentInstructions (DateOnly(2026, 10, 28)) s
    Assert.Equal<string list>([ "PLAN LENDER" ], current |> List.map (fun a -> a.Lender.Value))

    Assert.Equal<string list>(
        [ "OLD ARRANGEMENT" ],
        CreditFileSignals.currentInstructions (DateOnly(2018, 6, 1)) s
        |> List.map (fun a -> a.Lender.Value)
    )

    Assert.Empty(CreditFileSignals.currentInstructions (DateOnly(2017, 1, 1)) s)

[<Fact>]
let ``notices of correction keep their reference and text lines`` () =
    let notice = Assert.Single (signals ()).Notices
    Assert.Equal(ValueSome "NOC0001", notice.Reference)

    Assert.Equal<string list>(
        [
            "I was unable to pay while in hospital."
            "Please contact me before any decision."
        ],
        notice.Lines
    )

[<Fact>]
let ``CIFAS markers are the applicant's own, with protective registration recognised`` () =
    Assert.Equal<CifasMarker list>([ ProtectiveRegistration; FraudCategory "01" ], (signals ()).Cifas)

[<Fact>]
let ``a response without consumer data has no signals`` () =
    Assert.Equal(ValueNone, CreditFileSignals.ofOutput (DelphiSelect.Output()))
    Assert.Equal(ValueNone, CreditFileSignals.ofOutput null)

[<Fact>]
let ``empty notices of correction are not notices`` () =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")

    let json =
        """{"Response":{"FullConsumerData":{"ConsumerData":{"NOC":[{"NoCDetails":[{"TextLine":null},{"Reference":" ","TextLine":["  "]},{"TextLine":["Only text"]}]}]}}}}"""

    let output =
        (clientWith handler).DelphiSelect.Deserialize(json, typeof<DelphiSelect.Output>) :?> DelphiSelect.Output

    let notice = Assert.Single (CreditFileSignals.ofOutput output).Value.Notices
    Assert.Equal(ValueNone, notice.Reference)
    Assert.Equal<string list>([ "Only text" ], notice.Lines)
