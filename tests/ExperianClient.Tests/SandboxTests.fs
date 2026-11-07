/// Calls the real Experian sandboxes. Skipped unless EXPERIAN_USERNAME, EXPERIAN_PASSWORD, EXPERIAN_CLIENT_ID and EXPERIAN_CLIENT_SECRET are set.
module SandboxTests

open System
open System.Threading.Tasks
open Xunit
open Experian

let private credentials () =
    let read name =
        Environment.GetEnvironmentVariable name
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    match
        read "EXPERIAN_USERNAME", read "EXPERIAN_PASSWORD", read "EXPERIAN_CLIENT_ID", read "EXPERIAN_CLIENT_SECRET"
    with
    | Some username, Some password, Some clientId, Some clientSecret ->
        ValueSome
            {
                Username = username
                Password = password
                ClientId = clientId
                ClientSecret = clientSecret
            }
    | _ -> ValueNone

let private sandboxClient () =
    match credentials () with
    | ValueSome c -> ExperianClient.Create(c, Endpoints.sandbox)
    | ValueNone ->
        Assert.Skip
            "Set EXPERIAN_USERNAME, EXPERIAN_PASSWORD, EXPERIAN_CLIENT_ID and EXPERIAN_CLIENT_SECRET to call the sandbox."

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

        let! result =
            Api.DelphiSelect.applicationAsync client (ApiTests.sandboxApplication ()) FakeExperian.ct

        let output = FakeExperian.expectOk result
        let reference = output.Response.ClientData.ExperianReference
        Assert.True(reference.IsSome, "no ExperianReference in the response")

        printfn
            $"ExperianReference %s{reference.Value}; AuthPlus results: %d{output.Response.AuthPlusResults
                                                                          |> Option.ofObj
                                                                          |> Option.map Array.length
                                                                          |> Option.defaultValue 0}"

        let input =
            DelphiSelect.InputSimplified(
                Submission =
                    DelphiSelect.SubmissionSimplified(
                        ClientData = DelphiSelect.SubmissionSimplifiedClientData(ClientAccountNumber = Some "A1234")
                    )
            )

        let! second =
            Api.DelphiSelect.authenticateAsync client reference.Value input FakeExperian.ct

        FakeExperian.expectOk second |> ignore
    }

[<Fact>]
let ``sandbox: stand-alone AuthPlus without a credit search`` () : Task =
    task {
        let client = sandboxClient ()
        let application = ApiTests.sandboxApplication ()

        let input =
            client.DelphiSelect.Deserialize(
                client.DelphiSelect.Serialize application,
                typeof<DelphiSelect.InputAuthOnly>
            )
            :?> DelphiSelect.InputAuthOnly

        let! result = Api.DelphiSelect.authenticateOnlyAsync client input FakeExperian.ct
        let output = FakeExperian.expectOk result
        Assert.NotEmpty(output.Response.AuthPlusResults)
    }

[<Fact>]
let ``sandbox: NED search and IDP event types read`` () : Task =
    task {
        let client = sandboxClient ()

        let! employers =
            Api.Ned.searchEmployersAsync client "Tester" (ValueSome 3) ValueNone FakeExperian.ct

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

        let reference =
            (FakeExperian.expectOk quoted).Response.ClientData.ExperianReference.Value

        let toFull =
            DelphiSelect.InputQuoteToFull(
                Submission =
                    DelphiSelect.SubmissionQuoteToFull(
                        ClientData = DelphiSelect.SubmissionSimplifiedClientData(ClientAccountNumber = Some "A1234"),
                        Application = DelphiSelect.SubmissionSimplifiedApplication(ApplicationType = Some "PL")
                    )
            )

        let! full = Api.DelphiSelect.quoteToFullAsync client reference toFull FakeExperian.ct
        FakeExperian.expectOk full |> ignore
    }

[<Fact>]
let ``sandbox: Work Report person, connect and PDF`` () : Task =
    task {
        let client = sandboxClient ()

        let person =
            WorkReport.CreatePersonRequestBody(
                FirstName = "Sharon",
                LastName = "Rajapaksa",
                Dob = DateTimeOffset(1973, 8, 31, 0, 0, 0, TimeSpan.Zero),
                Identifiers =
                    [|
                        WorkReport.CreatePersonRequestBody_Identifiers_Item(
                            Scheme = Some "payrollReference",
                            Level = Some "TEST12345",
                            Value = Some "consent00"
                        )
                    |]
            )

        let! created = Api.WorkReport.createPersonAsync client person FakeExperian.ct
        let id = (FakeExperian.expectOk created).Id.Value

        let connect =
            WorkReport.ConnectWorkReportRequestBody(
                Channel = Some "Employment",
                Behaviour = Some "Once",
                Parameters =
                    WorkReport.parameters (EmployerId = "TEST12345", DataOption = "M3+", ConsentVersion = Some "1.2")
            )

        let! connected = Api.WorkReport.connectAsync client id connect FakeExperian.ct
        FakeExperian.expectOk connected |> ignore

        let! pdf =
            Api.WorkReport.requestReportAsync
                client
                id
                (WorkReport.ReportRequestBody(DataOption = Some "M3+"))
                FakeExperian.ct

        Assert.Equal("%PDF", Text.Encoding.ASCII.GetString((FakeExperian.expectOk pdf)[0..3]))
    }

[<Fact>]
let ``sandbox: PowerCurve application, query and decision detail`` () : Task =
    task {
        let client = sandboxClient ()

        let! application =
            Api.PowerCurve.applicationAsync client (PowerCurve.Submission()) FakeExperian.ct

        let id =
            (FakeExperian.expectOk application).Response.ApplicationDetails.PowerCurveId.Value

        let! queried = Api.PowerCurve.queryAsync client id FakeExperian.ct
        FakeExperian.expectOk queried |> ignore

        let decision =
            PowerCurve.SubmissionDecDetails(
                DecisionDetails =
                    PowerCurve.DecisionDetailsInput(
                        Decision = "A",
                        DecisionMakerOrUserId = "CustomerID12345",
                        OverrideJustification = Some "Accept"
                    )
            )

        let! decided = Api.PowerCurve.decisionDetailAsync client id decision FakeExperian.ct
        FakeExperian.expectOk decided |> ignore
    }

[<Fact>]
let ``sandbox: debt list from the application, and a typed quotation converted for consolidation`` () : Task =
    task {
        let client = sandboxClient ()

        let! application =
            Api.DelphiSelect.applicationAsync client (ApiTests.sandboxApplication ()) FakeExperian.ct

        let accounts = (CreditAccounts.ofOutput (FakeExperian.expectOk application)).Value
        Assert.NotEmpty accounts
        let debts = CreditAccounts.outstanding accounts
        Assert.NotEmpty debts

        Assert.All(
            debts,
            fun debt ->
                Assert.True(debt.Holder = MainApplicant || debt.Holder = AliasOfMainApplicant)
                Assert.True(debt.Balance.Value > 0m)
        )

        printfn $"%d{accounts.Length} accounts, %d{debts.Length} outstanding, total £%M{CreditAccounts.totalOwed debts}"

        let quote =
            ApiTests.sandboxApplication ()
            |> Api.DelphiSelect.setSearchType (Quotation Quotation.PersonalLoan)

        let! quoted = Api.DelphiSelect.applicationAsync client quote FakeExperian.ct

        let reference =
            (FakeExperian.expectOk quoted).Response.ClientData.ExperianReference.Value

        let! converted =
            Api.DelphiSelect.convertQuoteAsync
                client
                reference
                CreditProduct.DebtConsolidationLoan
                (ValueSome "A1234")
                FakeExperian.ct

        FakeExperian.expectOk converted |> ignore
    }

[<Fact>]
let ``sandbox: credit-file signals carry the applicant's arrangement`` () : Task =
    task {
        let client = sandboxClient ()

        let! application =
            Api.DelphiSelect.applicationAsync client (ApiTests.sandboxApplication ()) FakeExperian.ct

        let signals = (CreditFileSignals.ofOutput (FakeExperian.expectOk application)).Value
        Assert.Contains(ArrangementFound, signals.Flags)
        Assert.Contains(signals.Instructions, fun account -> account.SpecialInstruction.Value.Instruction = Arrangement)
    }

[<Fact>]
let ``sandbox: AuthPlus outcome and impaired credit history read from the application`` () : Task =
    task {
        let client = sandboxClient ()

        let! application =
            Api.DelphiSelect.applicationAsync client (ApiTests.sandboxApplication ()) FakeExperian.ct

        let output = FakeExperian.expectOk application
        let authPlus = Assert.Single(AuthPlus.ofOutput output)
        Assert.NotEqual(NoDecision, authPlus.Decision)
        Assert.True((PublicRecords.impairedCredit output).Value.MainApplicant.IsSome)
        Assert.True((PublicRecords.ofOutput output).IsSome)
    }

/// A client of another region's sandbox: the portal login, and that region's app.
let private regionClient (region: string) endpoints =
    let read name =
        Environment.GetEnvironmentVariable name
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    match
        read "EXPERIAN_USERNAME",
        read "EXPERIAN_PASSWORD",
        read $"EXPERIAN_{region}_CLIENT_ID",
        read $"EXPERIAN_{region}_CLIENT_SECRET"
    with
    | Some username, Some password, Some clientId, Some clientSecret ->
        ExperianClient.Create(
            {
                Username = username
                Password = password
                ClientId = clientId
                ClientSecret = clientSecret
            },
            endpoints
        )
    | _ ->
        Assert.Skip
            $"Set EXPERIAN_USERNAME, EXPERIAN_PASSWORD, EXPERIAN_{region}_CLIENT_ID and EXPERIAN_{region}_CLIENT_SECRET to run this."

        Unchecked.defaultof<_>

[<Fact>]
let ``sandbox US: TEC Connect answers with a credit profile`` () : Task =
    task {
        let client = regionClient "US" Endpoints.sandboxUs

        let! sample =
            IO.File.ReadAllTextAsync(IO.Path.Combine(__SOURCE_DIRECTORY__, "Samples", "us-tec-connect.json"))

        let request =
            client.CreditProfile.Deserialize(sample, typeof<CreditProfile.tecCreditProfileRequest>)
            :?> CreditProfile.tecCreditProfileRequest

        let! result =
            Api.CreditProfile.tecConnectAsync client request (ValueSome "SBMYSQL") FakeExperian.ct

        let answer = FakeExperian.expectOk result
        Assert.NotEmpty answer.CreditProfile
    }

[<Fact>]
let ``sandbox NL and ES: a web data score is between 0 and 1`` () : Task =
    task {
        let client = regionClient "EU" Endpoints.sandboxEu

        let! result =
            Api.WebData.scoreAsync
                client
                (DateOnly(2021, 1, 1))
                "Test Company"
                "Main Street 1"
                "Madrid"
                "gc-1"
                FakeExperian.ct

        let answer = FakeExperian.expectOk result
        Assert.InRange(answer.Score, 0.0, 1.0)
    }

let private sampleOf (name: string) =
    IO.File.ReadAllText(IO.Path.Combine(__SOURCE_DIRECTORY__, "Samples", name))

[<Fact>]
let ``sandbox: Bank Account Verify answers`` () : Task =
    task {
        let client = sandboxClient ()

        let request =
            client.BankAccountVerify.Deserialize(
                sampleOf "uk-bank-account-verify.json",
                typeof<BankAccountVerify.RequestMessage>
            )
            :?> BankAccountVerify.RequestMessage

        let! result =
            Api.BankAccountVerify.verifyAsync client request ValueNone FakeExperian.ct

        FakeExperian.expectOk result |> ignore
    }

[<Fact>]
let ``sandbox: International Payments Verification answers`` () : Task =
    task {
        let client = sandboxClient ()

        let request =
            client.InternationalPayments.Deserialize(
                sampleOf "intl-payments.json",
                typeof<InternationalPayments.RequestMessage>
            )
            :?> InternationalPayments.RequestMessage

        let! result = Api.InternationalPayments.verifyAsync client request FakeExperian.ct
        FakeExperian.expectOk result |> ignore
    }

[<Fact>]
let ``sandbox US: Instant Credit answers with a credit profile`` () : Task =
    task {
        let client = regionClient "US" Endpoints.sandboxUs

        let request =
            client.InstantCredit.Deserialize(sampleOf "us-instant-credit.json", typeof<InstantCredit.attributesRequest>)
            :?> InstantCredit.attributesRequest

        let! result =
            Api.InstantCredit.advancedAsync client request (ValueSome "SBMYSQL") FakeExperian.ct

        FakeExperian.expectOk result |> ignore
    }

/// An answer, or a skip when Experian says the app doesn't have the product: each is ticked per app under My Apps.
let private expectEnabled (product: string) (result: Result<'T, ExperianFailure>) =
    match result with
    | Error(ApiError(401, body)) ->
        Assert.Skip $"Tick {product} on the app under My Apps to run this: {body}"
        Unchecked.defaultof<'T>
    | other -> FakeExperian.expectOk other

[<Fact>]
let ``sandbox: the age threshold check answers`` () : Task =
    task {
        let client = sandboxClient ()

        let request =
            client.AgeAndDateOfBirth.Deserialize(
                sampleOf "uk-age-threshold-continue.json",
                typeof<AgeAndDateOfBirth.RequestMessage>
            )
            :?> AgeAndDateOfBirth.RequestMessage

        let! result =
            Api.AgeAndDateOfBirth.checkAsync client request ValueNone FakeExperian.ct

        FakeExperian.expectOk result |> ignore
    }

[<Fact>]
let ``sandbox: KYB finds a company and its owners`` () : Task =
    task {
        let client = sandboxClient ()

        let search =
            { Api.Kyb.BusinessSearch.Default with
                Name = ValueSome "TEST"
            }

        let! found = Api.Kyb.searchBusinessesAsync client search FakeExperian.ct
        Assert.NotEmpty (expectEnabled "KYB Sandbox" found).SearchResults
        let! owners = Api.Kyb.ultimateOwnersAsync client "99999999" FakeExperian.ct
        expectEnabled "KYB Sandbox" owners |> ignore
        let! profile = Api.Kyb.profileAsync client "99999999" FakeExperian.ct
        expectEnabled "KYB Sandbox" profile |> ignore
        let! locations = Api.Kyb.locationsAsync client "99999999" FakeExperian.ct
        expectEnabled "KYB Sandbox" locations |> ignore

        let! directors =
            Api.Kyb.searchDirectorsAsync
                client
                { Api.Kyb.DirectorSearch.Default with
                    Forename = ValueSome "John"
                    Surname = ValueSome "Smith"
                }
                FakeExperian.ct

        expectEnabled "KYB Sandbox" directors |> ignore
        let! visit = Api.Kyb.lastVisitAsync client "1234" FakeExperian.ct
        expectEnabled "KYB Sandbox" visit |> ignore
    }

[<Fact>]
let ``sandbox: AML flags answer for a company`` () : Task =
    task {
        let client = sandboxClient ()

        let request =
            client.Kyb.Deserialize(sampleOf "uk-aml-flags.json", typeof<Kyb.KybDataRequestBody>)
            :?> Kyb.KybDataRequestBody

        let! result = Api.AmlFlags.companyAsync client request FakeExperian.ct
        Assert.NotNull (expectEnabled "KYB Sandbox" result).Results
    }

[<Fact>]
let ``sandbox US: an SSN check answers`` () : Task =
    task {
        let client = regionClient "US" Endpoints.sandboxUs

        let request =
            client.SsnVerification.Deserialize(sampleOf "us-ssn.json", typeof<SsnVerification.verifyRequest>)
            :?> SsnVerification.verifyRequest

        match! Api.SsnVerification.verifyAsync client "0000000" "1234" request (ValueSome "ABC123") FakeExperian.ct with
        | Error(ApiError(500, body)) when body.Contains "RaiseFault.notFound" ->
            Assert.Skip $"Experian's sandbox has no working eCBSV path: {body}"
        | other -> expectEnabled "Verification Ecbsv Sandbox" other |> ignore
    }

[<Fact>]
let ``sandbox US: an Experian Verify report answers`` () : Task =
    task {
        let client = regionClient "US" Endpoints.sandboxUs

        let request =
            client.VerifyCore.Deserialize(sampleOf "us-verify-core.json", typeof<VerifyCore.RequestParams>)
            :?> VerifyCore.RequestParams

        let! result = Api.Verify.coreAsync client "5991764" "SBMYSQL" request FakeExperian.ct
        Assert.NotNull (expectEnabled "Experian Verify Sandbox" result).IncomeReport
    }

/// Address, Email and Phone Validation have no sandbox: a token from Experian's Self Service Portal, and each call may use up credit.
let private apertureClient () =
    match
        Environment.GetEnvironmentVariable "EXPERIAN_APERTURE_TOKEN"
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
    with
    | Some token -> ApertureClient.Create token
    | None ->
        Assert.Skip "Set EXPERIAN_APERTURE_TOKEN to run this; each call may use up credit."
        Unchecked.defaultof<_>

[<Fact>]
let ``live: an email check answers with a confidence`` () : Task =
    task {
        let client = apertureClient ()

        let! result =
            Api.EmailValidation.validateAsync client "someone@example.com" Api.ValidationOptions.Default FakeExperian.ct

        Assert.True((FakeExperian.expectOk result).Result.Confidence.IsSome)
    }

[<Fact>]
let ``live: a phone check answers with a confidence`` () : Task =
    task {
        let client = apertureClient ()

        let request =
            PhoneValidation.PhoneValidationV2Request("+442079460000", None, None, Some "GBR", None, None, null)

        let! result =
            Api.PhoneValidation.validateAsync client request Api.ValidationOptions.Default FakeExperian.ct

        Assert.True((FakeExperian.expectOk result).Result.Confidence.IsSome)
    }

[<Fact>]
let ``live: an address search answers`` () : Task =
    task {
        let client = apertureClient ()

        let components =
            AddressValidation.AddressInputComponents(Unspecified = [| "10 Downing Street, London" |])

        let request =
            AddressValidation.AddressSearchV1Request(
                CountryIso = "GBR",
                Components = components,
                Datasets = [| "gb-address" |]
            )

        let! result =
            Api.AddressValidation.searchAsync client request Api.AddressValidation.Options.Default FakeExperian.ct

        FakeExperian.expectOk result |> ignore
    }
