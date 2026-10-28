/// Calls the real Experian UK sandbox. Skipped unless EXPERIAN_USERNAME, EXPERIAN_PASSWORD, EXPERIAN_CLIENT_ID and EXPERIAN_CLIENT_SECRET are set.
module SandboxTests

open System
open System.Threading.Tasks
open Xunit
open Experian

let private credentials () =
    let read name = Environment.GetEnvironmentVariable name |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)
    match read "EXPERIAN_USERNAME", read "EXPERIAN_PASSWORD", read "EXPERIAN_CLIENT_ID", read "EXPERIAN_CLIENT_SECRET" with
    | Some username, Some password, Some clientId, Some clientSecret ->
        ValueSome { Username = username; Password = password; ClientId = clientId; ClientSecret = clientSecret }
    | _ -> ValueNone

let private sandboxClient () =
    match credentials () with
    | ValueSome c -> ExperianClient.Create(c, Endpoints.sandbox)
    | ValueNone ->
        Assert.Skip "Set EXPERIAN_USERNAME, EXPERIAN_PASSWORD, EXPERIAN_CLIENT_ID and EXPERIAN_CLIENT_SECRET to call the sandbox."
        Unchecked.defaultof<_>

[<Fact>]
let ``sandbox: a token is issued`` () : Task =
    task {
        let client = sandboxClient ()
        let! token = client.Tokens.GetAsync FakeExperian.ct
        Assert.False(String.IsNullOrEmpty token.Value)
        Assert.True(token.ExpiresAt > DateTimeOffset.UtcNow)
    }

[<Fact>]
let ``sandbox: Delphi Select application with AuthPlus, then second phase AuthPlus`` () : Task =
    task {
        let client = sandboxClient ()
        let! result = Api.DelphiSelect.applicationAsync client (ApiTests.sandboxApplication ()) FakeExperian.ct
        let output = FakeExperian.expectOk result
        let reference = output.Response.ClientData.ExperianReference
        Assert.True(reference.IsSome, "no ExperianReference in the response")
        printfn $"ExperianReference %s{reference.Value}; AuthPlus results: %d{output.Response.AuthPlusResults |> Option.ofObj |> Option.map Array.length |> Option.defaultValue 0}"

        let input =
            DelphiSelect.InputSimplified(
                Submission = DelphiSelect.SubmissionSimplified(ClientData = DelphiSelect.SubmissionSimplifiedClientData(ClientAccountNumber = Some "A1234")))
        let! second = Api.DelphiSelect.authenticateAsync client reference.Value input FakeExperian.ct
        FakeExperian.expectOk second |> ignore
    }

[<Fact>]
let ``sandbox: stand-alone AuthPlus without a credit search`` () : Task =
    task {
        let client = sandboxClient ()
        let application = ApiTests.sandboxApplication ()
        let input = client.DelphiSelect.Deserialize(client.DelphiSelect.Serialize application, typeof<DelphiSelect.InputAuthOnly>) :?> DelphiSelect.InputAuthOnly
        let! result = Api.DelphiSelect.authenticateOnlyAsync client input FakeExperian.ct
        let output = FakeExperian.expectOk result
        Assert.NotEmpty(output.Response.AuthPlusResults)
    }

[<Fact>]
let ``sandbox: NED search and IDP event types read`` () : Task =
    task {
        let client = sandboxClient ()
        let! employers = Api.Ned.searchEmployersAsync client "Tester" (ValueSome 3) ValueNone FakeExperian.ct
        Assert.NotEmpty(FakeExperian.expectOk employers)
        let! events = Api.Idp.listEventTypesAsync client FakeExperian.ct
        Assert.NotEmpty((FakeExperian.expectOk events).EventNames)
    }

[<Fact>]
let ``sandbox: Delphi quotation converted to a full application`` () : Task =
    task {
        let client = sandboxClient ()
        let quote = ApiTests.sandboxApplication ()
        quote.Submission.Application.ApplicationType <- Some "QL"
        let! quoted = Api.DelphiSelect.applicationAsync client quote FakeExperian.ct
        let reference = (FakeExperian.expectOk quoted).Response.ClientData.ExperianReference.Value
        let toFull =
            DelphiSelect.InputQuoteToFull(
                Submission =
                    DelphiSelect.SubmissionQuoteToFull(
                        ClientData = DelphiSelect.SubmissionSimplifiedClientData(ClientAccountNumber = Some "A1234"),
                        Application = DelphiSelect.SubmissionSimplifiedApplication(ApplicationType = Some "PL")))
        let! full = Api.DelphiSelect.quoteToFullAsync client reference toFull FakeExperian.ct
        FakeExperian.expectOk full |> ignore
    }

[<Fact>]
let ``sandbox: Work Report person, connect and PDF`` () : Task =
    task {
        let client = sandboxClient ()
        let person =
            WorkReport.CreatePersonRequestBody(
                FirstName = "Sharon", LastName = "Rajapaksa", Dob = DateTimeOffset(1973, 8, 31, 0, 0, 0, TimeSpan.Zero),
                Identifiers = [| WorkReport.CreatePersonRequestBody_Identifiers_Item(Scheme = Some "payrollReference", Level = Some "TEST12345", Value = Some "consent00") |])
        let! created = Api.WorkReport.createPersonAsync client person FakeExperian.ct
        let id = (FakeExperian.expectOk created).Id.Value
        let connect =
            WorkReport.ConnectWorkReportRequestBody(
                Channel = Some "Employment", Behaviour = Some "Once",
                Parameters = WorkReport.parameters(EmployerId = "TEST12345", DataOption = "M3+", ConsentVersion = Some "1.2"))
        let! connected = Api.WorkReport.connectAsync client id connect FakeExperian.ct
        FakeExperian.expectOk connected |> ignore
        let! pdf = Api.WorkReport.requestReportAsync client id (WorkReport.ReportRequestBody(DataOption = Some "M3+")) FakeExperian.ct
        Assert.Equal("%PDF", Text.Encoding.ASCII.GetString((FakeExperian.expectOk pdf)[0..3]))
    }

[<Fact>]
let ``sandbox: PowerCurve application, query and decision detail`` () : Task =
    task {
        let client = sandboxClient ()
        let! application = Api.PowerCurve.applicationAsync client (PowerCurve.Submission()) FakeExperian.ct
        let id = (FakeExperian.expectOk application).Response.ApplicationDetails.PowerCurveId.Value
        let! queried = Api.PowerCurve.queryAsync client id FakeExperian.ct
        FakeExperian.expectOk queried |> ignore
        let decision =
            PowerCurve.SubmissionDecDetails(
                DecisionDetails = PowerCurve.DecisionDetailsInput(Decision = "A", DecisionMakerOrUserId = "CustomerID12345", OverrideJustification = Some "Accept"))
        let! decided = Api.PowerCurve.decisionDetailAsync client id decision FakeExperian.ct
        FakeExperian.expectOk decided |> ignore
    }

[<Fact>]
let ``sandbox: debt list from the application, and a typed quotation converted for consolidation`` () : Task =
    task {
        let client = sandboxClient ()
        let! application = Api.DelphiSelect.applicationAsync client (ApiTests.sandboxApplication ()) FakeExperian.ct
        let accounts = (CreditAccounts.ofOutput (FakeExperian.expectOk application)).Value
        Assert.NotEmpty accounts
        let debts = CreditAccounts.outstanding accounts
        Assert.NotEmpty debts
        Assert.All(debts, fun debt ->
            Assert.True(debt.Holder = MainApplicant || debt.Holder = AliasOfMainApplicant)
            Assert.True(debt.Balance.Value > 0m))
        printfn $"%d{accounts.Length} accounts, %d{debts.Length} outstanding, total £%M{CreditAccounts.totalOwed debts}"

        let quote = ApiTests.sandboxApplication () |> Api.DelphiSelect.setSearchType (Quotation Quotation.PersonalLoan)
        let! quoted = Api.DelphiSelect.applicationAsync client quote FakeExperian.ct
        let reference = (FakeExperian.expectOk quoted).Response.ClientData.ExperianReference.Value
        let! converted = Api.DelphiSelect.convertQuoteAsync client reference CreditProduct.DebtConsolidationLoan (ValueSome "A1234") FakeExperian.ct
        FakeExperian.expectOk converted |> ignore
    }

[<Fact>]
let ``sandbox: credit-file signals carry the applicant's arrangement`` () : Task =
    task {
        let client = sandboxClient ()
        let! application = Api.DelphiSelect.applicationAsync client (ApiTests.sandboxApplication ()) FakeExperian.ct
        let signals = (CreditFileSignals.ofOutput (FakeExperian.expectOk application)).Value
        Assert.Contains(ArrangementFound, signals.Flags)
        Assert.Contains(signals.Instructions, fun account -> account.SpecialInstruction.Value.Instruction = Arrangement)
    }

[<Fact>]
let ``sandbox: AuthPlus outcome and impaired credit history read from the application`` () : Task =
    task {
        let client = sandboxClient ()
        let! application = Api.DelphiSelect.applicationAsync client (ApiTests.sandboxApplication ()) FakeExperian.ct
        let output = FakeExperian.expectOk application
        let authPlus = Assert.Single(AuthPlus.ofOutput output)
        Assert.NotEqual(NoDecision, authPlus.Decision)
        Assert.True((PublicRecords.impairedCredit output).Value.MainApplicant.IsSome)
        Assert.True((PublicRecords.ofOutput output).IsSome)
    }
