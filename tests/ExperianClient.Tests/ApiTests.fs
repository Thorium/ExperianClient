module ApiTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Experian
open FakeExperian

/// Experian's "New application request example 1" from the Delphi Select spec: one applicant, AuthPlus and Detect requested.
let sandboxApplication () =
    DelphiSelect.Input(
        Submission =
            DelphiSelect.Submission(
                Applicants =
                    [| DelphiSelect.Applicant(
                           ApplicantIdentifier = 1,
                           Person =
                               DelphiSelect.Person(
                                   Forename = Some "ALEXANDER",
                                   Surname = Some "MAYER",
                                   DateOfBirth = Some(DateOnly(1981, 6, 19)))) |],
                Locations =
                    [| DelphiSelect.LocationItem(
                           LocationIdentifier = 1,
                           UkLocation = DelphiSelect.UKLocation(HouseNumber = Some "36", Postcode = Some "BT13 3SJ")) |],
                Residencies =
                    [| DelphiSelect.ResidencyItem(
                           ApplicantIdentifier = 1,
                           LocationIdentifier = 1,
                           ResidentFrom = Some(DateOnly(2018, 2, 18)),
                           ResidentTo = Some(DateOnly(2019, 2, 18)),
                           Type = Some "01",
                           CanBeSearched = Some "Y") |],
                Application = DelphiSelect.Application(ApplicationType = Some "EQ", SearchConsent = Some true),
                Options = DelphiSelect.Options(FullFblRequired = true, AuthenticatePlusRequired = true, DetectRequired = true),
                ClientData = DelphiSelect.ClientData(ClientAccountNumber = Some "A1234")))

let private only (handler: FakeExperianHandler) = Assert.Single handler.ApiCalls

[<Fact>]
let ``a Delphi Select application posts the input under the spec's base path with the bearer token`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, """{"Response":{"ClientData":{"ExperianReference":"0123456789"}}}"""))
        let! result = Api.DelphiSelect.applicationAsync (clientWith handler) (sandboxApplication ()) ct
        let output = expectOk result
        Assert.Equal(Some "0123456789", output.Response.ClientData.ExperianReference)

        let call = only handler
        Assert.Equal("POST", call.Method)
        Assert.Equal("https://sandbox-uk-api.experian.com/risk/consumer/v2/dataselect/application", call.Uri)
        Assert.Equal("Bearer tok-1", call.Headers["Authorization"])
        Assert.Equal("application/json", call.Headers["Content-Type"])
    }

[<Fact>]
let ``the application body uses Experian's field names and leaves unset fields out`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "{}"))
        let! _ = Api.DelphiSelect.applicationAsync (clientWith handler) (sandboxApplication ()) ct
        let body = (only handler).Body
        Assert.DoesNotContain("null", body)
        let submission = JsonDocument.Parse(body).RootElement.GetProperty "Submission"
        let options = submission.GetProperty "Options"
        Assert.True(options.GetProperty("AuthenticatePlusRequired").GetBoolean())
        Assert.True(options.GetProperty("FullFBLRequired").GetBoolean())
        let person = submission.GetProperty("Applicants").[0].GetProperty "Person"
        Assert.Equal("1981-06-19", person.GetProperty("DateOfBirth").GetString())
        Assert.Equal(JsonValueKind.Undefined, (match person.TryGetProperty "MiddleName" with | true, v -> v.ValueKind | _ -> JsonValueKind.Undefined))
        let location = submission.GetProperty("Locations").[0]
        Assert.Equal("BT13 3SJ", location.GetProperty("UKLocation").GetProperty("Postcode").GetString())
        Assert.Equal("2018-02-18", submission.GetProperty("Residencies").[0].GetProperty("ResidentFrom").GetString())
    }

[<Fact>]
let ``second phase AuthPlus puts to the application's secondphaseauthenticate`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "{}"))
        let input =
            DelphiSelect.InputSimplified(
                Submission = DelphiSelect.SubmissionSimplified(ClientData = DelphiSelect.SubmissionSimplifiedClientData(ClientAccountNumber = Some "A1234")))
        let! result = Api.DelphiSelect.authenticateAsync (clientWith handler) "0123456789" input ct
        expectOk result |> ignore
        let call = only handler
        Assert.Equal("PUT", call.Method)
        Assert.Equal("https://sandbox-uk-api.experian.com/risk/consumer/v2/dataselect/application/0123456789/secondphaseauthenticate", call.Uri)
        Assert.Equal("""{"Submission":{"ClientData":{"ClientAccountNumber":"A1234"}}}""", call.Body)
    }

[<Fact>]
let ``a retrieved application sends only the view flags that are set`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "{}"))
        let view = { Api.DelphiSelect.ApplicationView.Default with DisplayAuthenticate = ValueSome true; ClientAccountNumber = ValueSome "A1234" }
        let! result = Api.DelphiSelect.getApplicationAsync (clientWith handler) "0123456789" view ct
        expectOk result |> ignore
        let call = only handler
        Assert.Equal("GET", call.Method)
        Assert.Equal("https://sandbox-uk-api.experian.com/risk/consumer/v2/dataselect/application/0123456789?clientaccnum=A1234&displayauthenticate=true", call.Uri)
    }

[<Fact>]
let ``stand-alone AuthPlus posts to submission/authenticate`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "{}"))
        let! result = Api.DelphiSelect.authenticateOnlyAsync (clientWith handler) (DelphiSelect.InputAuthOnly()) ct
        expectOk result |> ignore
        Assert.Equal("https://sandbox-uk-api.experian.com/risk/consumer/v2/dataselect/submission/authenticate", (only handler).Uri)
    }

[<Fact>]
let ``each product resolves against its own base address`` () : Task =
    task {
        use handler =
            new FakeExperianHandler(respondWith (fun call -> if call.Uri.EndsWith "/webhooks" || call.Uri.Contains "/ned/v1/employers?" then 200, "[]" else 200, "{}"))
        let client = clientWith handler
        let! kyc = Api.Kyc.checkAsync client (Kyc.RequestMessage()) ValueNone ct
        let! pep = Api.Kyc.pepSanctionsAsync client (Kyc.RequestMessage()) (ValueSome "en-GB") ct
        let! employers = Api.Ned.searchEmployersAsync client "Acme & Sons" (ValueSome 5) ValueNone ct
        let! person = Api.WorkReport.createPersonAsync client (WorkReport.CreatePersonRequestBody()) ct
        let! webhooks = Api.Idp.listWebhooksAsync client ct
        let! decision = Api.PowerCurve.applicationAsync client (PowerCurve.Submission()) ct
        expectOk kyc |> ignore
        expectOk pep |> ignore
        expectOk employers |> ignore
        expectOk person |> ignore
        Assert.Empty(expectOk webhooks)
        expectOk decision |> ignore

        List.iter2
            (fun (expected: string) (call: Call) -> Assert.Equal(expected, $"{call.Method} {call.Uri}"))
            [ "POST https://sandbox-uk-api.experian.com/da/ccis-devportalapis/v1/KYC"
              "POST https://sandbox-uk-api.experian.com/da/ccis-devportalapis/v1/PEPSANCTIONS"
              "GET https://sandbox-uk-api.experian.com/cis/verifications/ned/v1/employers?name=Acme%20%26%20Sons&%24top=5"
              "POST https://sandbox-uk-api.experian.com/cis/verifications/work-report/v1/persons"
              "GET https://sandbox-uk-api.experian.com/cis/verifications/idp/v1/api/webhooks"
              "POST https://sandbox-uk-api.experian.com/decisioning/experianone/customeracquisition/v1/applications" ]
            handler.ApiCalls
        Assert.Equal("en-GB", handler.ApiCalls[1].Headers["AcceptLanguage"])
        Assert.True(handler.ApiCalls |> List.forall (fun c -> c.Headers["Authorization"] = "Bearer tok-1"))
    }

[<Fact>]
let ``production endpoints use uk-api.experian.com`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "{}"))
        let client = ExperianClient.create credentials Endpoints.production handler
        let! _ = Api.DelphiSelect.authenticateOnlyAsync client (DelphiSelect.InputAuthOnly()) ct
        Assert.Equal<string list>(
            [ "https://uk-api.experian.com/oauth2/v1/token"
              "https://uk-api.experian.com/risk/consumer/v2/dataselect/submission/authenticate" ],
            handler.Calls |> List.map (fun c -> c.Uri))
    }

[<Fact>]
let ``a refused request is ApiError with the status and body`` () : Task =
    task {
        let body = """{"errors":[{"errorType":"Validation","message":"Postcode is invalid"}]}"""
        use handler = new FakeExperianHandler(respondWith (fun _ -> 400, body))
        match! Api.DelphiSelect.applicationAsync (clientWith handler) (sandboxApplication ()) ct with
        | Error(ApiError(400, text)) -> Assert.Equal(body, text)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``a refused PowerCurve request is ApiError too, though its spec lists no error codes`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 422, """{"message":"bad"}"""))
        match! Api.PowerCurve.queryAsync (clientWith handler) "FA00000000008227" ct with
        | Error(ApiError(422, text)) -> Assert.Contains("bad", text)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``an unreachable host is TransportError`` () : Task =
    task {
        use handler = new FakeExperianHandler(fun call -> if isToken call then 200, tokenJson "t" else raise (Net.Http.HttpRequestException "no route"))
        match! Api.Ned.getEmployerAsync (clientWith handler) "E1" ValueNone ct with
        | Error(TransportError e) -> Assert.Contains("no route", e.Message)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``amounts are doubles, not float32, so pence survive`` () =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")
    let client = clientWith handler
    let json = client.PowerCurve.Serialize(PowerCurve.MortgageSpecific(LoanToValue = Some 1234567.89))
    Assert.Contains("\"LoanToValue\":1234567.89", json)

[<Fact>]
let ``numeric string enums go on the wire as their values`` () =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")
    let client = clientWith handler
    Assert.Equal("\"1\"", client.DelphiSelect.Serialize DelphiSelect.Dependants.V1)
    Assert.Equal(box DelphiSelect.Dependants.V9, client.DelphiSelect.Deserialize("\"9\"", typeof<DelphiSelect.Dependants>))

[<Fact>]
let ``string fields Experian sends unquoted read as their text`` () : Task =
    task {
        let answer = """{"Response":{"ConsumerSummary":{"PremiumValueData":{"Scoring":{"E5S041":"01","E5S042":0,"E5S051":499},"AdditDelphiBlocks":{"Utilisationblock":{"SPAC24":2}}}}}}"""
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, answer))
        let! result = Api.DelphiSelect.applicationAsync (clientWith handler) (sandboxApplication ()) ct
        let data = (expectOk result).Response.ConsumerSummary.PremiumValueData
        Assert.Equal(Some "01", data.Scoring.E5s041)
        Assert.Equal(Some "0", data.Scoring.E5s042)
        Assert.Equal(Some 499, data.Scoring.E5s051)
        Assert.Equal(Some "2", data.AdditDelphiBlocks.Utilisationblock.Spac24)
    }

[<Fact>]
let ``numbers sent as strings are read, and requests still write numbers and strings as typed`` () =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")
    let client = clientWith handler
    let scoring = client.DelphiSelect.Deserialize("""{"E5S051":"-999","E5S042":true}""", typeof<DelphiSelect.Scoring>) :?> DelphiSelect.Scoring
    Assert.Equal(Some -999, scoring.E5s051)
    Assert.Equal(Some "true", scoring.E5s042)
    Assert.Equal("""{"E5S051":-999,"E5S042":"0"}""", client.DelphiSelect.Serialize(DelphiSelect.Scoring(E5s042 = Some "0", E5s051 = Some -999)))

[<Fact>]
let ``an employer search reads the list NED returns, whatever the case of its names`` () : Task =
    task {
        let answer = """[{"name":"TESTERWORLD LTD","employerid":"TSTR84893","highlight":"0-2","address":{"line1":"20 Kensington High Street","postcode":"W8 5JH"}},{"name":"TESTERMANS LTD LTD","employerid":"TSTR98430","highlight":"3-8"}]"""
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, answer))
        let! result = Api.Ned.searchEmployersAsync (clientWith handler) "Tester" ValueNone ValueNone ct
        let employers = expectOk result
        Assert.Equal(2, employers.Length)
        Assert.Equal(Some "TSTR84893", employers[0].EmployerId)
        Assert.Equal(Some "W8 5JH", employers[0].Address.Postcode)
    }

[<Fact>]
let ``a body that is one JSON string holding the object is read as that object`` () : Task =
    task {
        let inner = """{"responseHeader":{"overallResponse":{"decision":"CONTINUE"}}}"""
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, JsonSerializer.Serialize inner))
        let! result = Api.Kyc.checkAsync (clientWith handler) (Kyc.RequestMessage()) ValueNone ct
        Assert.Equal(Some "CONTINUE", (expectOk result).ResponseHeader.OverallResponse.Decision)
    }

[<Fact>]
let ``blank or dash optional dates and numbers read as None`` () =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")
    let client = clientWith handler
    let person = client.DelphiSelect.Deserialize("""{"DateOfBirth":"","Forename":""}""", typeof<DelphiSelect.Person>) :?> DelphiSelect.Person
    Assert.Equal(None, person.DateOfBirth)
    Assert.Equal(Some "", person.Forename)
    let scoring = client.DelphiSelect.Deserialize("""{"E5S051":" ","E5S052":"-"}""", typeof<DelphiSelect.Scoring>) :?> DelphiSelect.Scoring
    Assert.Equal(None, scoring.E5s051)
    Assert.Equal(None, scoring.E5s052)
    Assert.Equal("""{"DateOfBirth":"1981-06-19"}""", client.DelphiSelect.Serialize(DelphiSelect.Person(DateOfBirth = Some(DateOnly(1981, 6, 19)))))

[<Fact>]
let ``the Work Report PDF comes back as bytes, untouched`` () : Task =
    task {
        let pdf = "%PDF-1.7\n%âã\n"
        let respond (call: Call) =
            if isToken call then 200, tokenJson "tok-1" else 200, pdf
        use handler = new FakeExperianHandler(respond)
        let! result = Api.WorkReport.requestReportAsync (clientWith handler) 9903039 (WorkReport.ReportRequestBody()) ct
        let bytes = expectOk result
        Assert.Equal(pdf, Text.Encoding.UTF8.GetString bytes)
        let call = only handler
        Assert.Equal("POST https://sandbox-uk-api.experian.com/cis/verifications/work-report/v1/persons/9903039/reports", $"{call.Method} {call.Uri}")
        Assert.Equal("Bearer tok-1", call.Headers["Authorization"])
        Assert.Equal("application/json", call.Headers["Content-Type"])
    }

[<Fact>]
let ``a refused Work Report PDF is ApiError with the body`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 404, """{"errors":[{"message":"No report"}]}"""))
        match! Api.WorkReport.requestReportAsync (clientWith handler) 1 (WorkReport.ReportRequestBody()) ct with
        | Error(ApiError(404, body)) -> Assert.Contains("No report", body)
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``a path parameter stays one segment, so it cannot reach another endpoint`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "{}"))
        let client = clientWith handler
        let! _ = Api.DelphiSelect.getApplicationAsync client "../../../cis/verifications/idp/v1/api/webhooks/secrets?x=" Api.DelphiSelect.ApplicationView.Default ct
        let! _ = Api.Idp.getDocumentsAsync client "../webhooks/secrets#" 1 ct
        let! _ = Api.Ned.getEmployerAsync client "a b/c" ValueNone ct
        Assert.Equal<string list>(
            [ "https://sandbox-uk-api.experian.com/risk/consumer/v2/dataselect/application/..%2F..%2F..%2Fcis%2Fverifications%2Fidp%2Fv1%2Fapi%2Fwebhooks%2Fsecrets%3Fx%3D"
              "https://sandbox-uk-api.experian.com/cis/verifications/idp/v1/api/application/..%2Fwebhooks%2Fsecrets%23/1"
              "https://sandbox-uk-api.experian.com/cis/verifications/ned/v1/employers/a%20b%2Fc" ],
            handler.ApiCalls |> List.map (fun c -> c.Uri))
    }

[<Fact>]
let ``an empty or dot path parameter is InvalidRequest and nothing is sent`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "{}"))
        let client = clientWith handler
        for value in [ ""; "."; ".."; null ] do
            match! Api.PowerCurve.queryAsync client value ct with
            | Error(InvalidRequest reason) -> Assert.Contains("powerCurveId", reason)
            | other -> failwith $"%A{other}"
        Assert.Empty handler.Calls
    }

[<Fact>]
let ``printing credentials or a token hides the secrets`` () =
    let token = { Value = "eyJ-secret-token"; ExpiresAt = DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero) }
    for text in [ $"%A{credentials}"; string credentials; $"%A{token}"; string token; sprintf "%A" [ token ] ] do
        Assert.DoesNotContain("portal-password", text)
        Assert.DoesNotContain("client-secret", text)
        Assert.DoesNotContain("eyJ-secret-token", text)
    Assert.Contains("portal-user@example.com", string credentials)
    Assert.Contains("2026-10-27", string token)
