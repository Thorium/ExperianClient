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

type private HangingHandler() =
    inherit Net.Http.HttpMessageHandler()
    override _.SendAsync(request, ct) =
        task {
            if request.RequestUri.AbsoluteUri.EndsWith "/oauth2/v1/token" then
                return new Net.Http.HttpResponseMessage(Net.HttpStatusCode.OK, Content = new Net.Http.StringContent(tokenJson "t", Text.Encoding.UTF8, "application/json"))
            else
                do! Task.Delay(Threading.Timeout.Infinite, ct)
                return null
        }

/// A success answer whose body stops with an error part way through.
type private BrokenBodyHandler() =
    inherit Net.Http.HttpMessageHandler()
    override _.SendAsync(request, _) =
        task {
            if request.RequestUri.AbsoluteUri.EndsWith "/oauth2/v1/token" then
                return new Net.Http.HttpResponseMessage(Net.HttpStatusCode.OK, Content = new Net.Http.StringContent(tokenJson "t", Text.Encoding.UTF8, "application/json"))
            else
                let stream =
                    { new IO.MemoryStream() with
                        override _.ReadAsync(_: Memory<byte>, _: Threading.CancellationToken) =
                            ValueTask<int>(Task.FromException<int>(IO.IOException "connection lost")) }
                return new Net.Http.HttpResponseMessage(Net.HttpStatusCode.OK, Content = new Net.Http.StreamContent(stream))
        }

let private placeholderAnswer = """{"Response":{"ConsumerSummary":{"Summary":{"PublicInfo":{"E1A01":"VALUE-42"}}}}}"""

let private expectUnreadable result =
    match result with
    | Error(InvalidResponse answer) -> answer
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a number field holding a placeholder is InvalidResponse with the status and body, and a retry is the caller's choice`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, placeholderAnswer))
        let! result = Api.DelphiSelect.applicationAsync (clientWith handler) (sandboxApplication ()) ct
        let answer = expectUnreadable result
        Assert.Equal(200, answer.HttpStatus)
        Assert.Equal(placeholderAnswer, answer.Body)
        Assert.StartsWith("Invalid at $", answer.Reason)
        Assert.Single handler.ApiCalls |> ignore
    }

[<Fact>]
let ``a page that isn't JSON on a 200 is InvalidResponse`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "<html>proxy</html>"))
        let! result = Api.DelphiSelect.applicationAsync (clientWith handler) (sandboxApplication ()) ct
        Assert.Equal("<html>proxy</html>", (expectUnreadable result).Body)
    }

[<Fact>]
let ``an unreadable answer from Aperture is InvalidResponse too`` () : Task =
    task {
        use handler = new FakeExperianHandler(fun _ -> 200, "<html>proxy</html>")
        let client = ApertureClient("token", handler)
        let! result = Api.AddressValidation.formatAsync client "key" Api.AddressValidation.Options.Default ct
        Assert.Equal("<html>proxy</html>", (expectUnreadable result).Body)
    }

[<Fact>]
let ``concurrent calls report their own unreadable answers`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun call -> 200, $"<{call.Uri}>"))
        let client = clientWith handler
        let! results = [ "E1"; "E2"; "E3"; "E4" ] |> List.map (fun id -> Api.Ned.getEmployerAsync client id ValueNone ct) |> Task.WhenAll
        Assert.Equal<string list>(
            [ "E1>"; "E2>"; "E3>"; "E4>" ],
            results |> List.ofArray |> List.map (fun result -> let body = (expectUnreadable result).Body in body.Substring(body.Length - 3)))
    }

[<Fact>]
let ``printing an unreadable answer or describing it leaves the body and its values out`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, placeholderAnswer))
        let! result = Api.DelphiSelect.applicationAsync (clientWith handler) (sandboxApplication ()) ct
        let failure = match result with Error failure -> failure | Ok _ -> failwith "read"
        for text in [ ExperianFailure.describe failure; $"%A{failure}"; string (expectUnreadable result); $"%A{result |> Result.map ignore}" ] do
            Assert.DoesNotContain("VALUE-42", text)
            Assert.Contains("200", text)
            Assert.Contains("Invalid at $", text)
    }

[<Fact>]
let ``a success answer cut off part way is TransportError`` () : Task =
    task {
        let client = ExperianClient.create credentials Endpoints.sandbox (new BrokenBodyHandler())
        match! Api.DelphiSelect.applicationAsync client (sandboxApplication ()) ct with
        | Error(TransportError _) -> ()
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``a readable Work Report PDF still comes back whole`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "%PDF-1.7"))
        let! result = Api.WorkReport.requestReportAsync (clientWith handler) 7 (WorkReport.ReportRequestBody()) ct
        Assert.Equal("%PDF-1.7", Text.Encoding.UTF8.GetString(expectOk result))
    }

[<Fact>]
let ``Create without a timeout keeps the default and Create with one sets it on every product`` () =
    let plain = ExperianClient.Create(credentials, Endpoints.sandbox)
    Assert.Equal(TimeSpan.FromSeconds 100., plain.DelphiSelect.HttpClient.Timeout)
    let limited = ExperianClient.Create(credentials, Endpoints.sandbox, TimeSpan.FromSeconds 20.)
    Assert.Equal(TimeSpan.FromSeconds 20., limited.DelphiSelect.HttpClient.Timeout)
    Assert.Equal(TimeSpan.FromSeconds 20., limited.Kyc.HttpClient.Timeout)
    Assert.Equal(TimeSpan.FromSeconds 20., ApertureClient.Create("token", TimeSpan.FromSeconds 20.).Address.HttpClient.Timeout)
    Assert.Equal(TimeSpan.FromSeconds 100., ApertureClient.Create("token").Address.HttpClient.Timeout)

[<Theory>]
[<InlineData(0.)>]
[<InlineData(-5.)>]
let ``Create refuses a timeout that isn't positive`` (seconds: float) =
    Assert.Throws<ArgumentOutOfRangeException>(fun () ->
        ExperianClient.Create(credentials, Endpoints.sandbox, TimeSpan.FromSeconds seconds) |> ignore)
    |> ignore

[<Fact>]
let ``a stuck call past the timeout is TransportError, not an exception`` () : Task =
    task {
        let client = ExperianClient(credentials, Endpoints.sandbox, new HangingHandler(), (fun () -> DateTimeOffset.UtcNow), ValueSome(TimeSpan.FromMilliseconds 200.))
        let call = Api.DelphiSelect.applicationAsync client (sandboxApplication ()) ct
        let! winner = Task.WhenAny(call :> Task, Task.Delay 10000)
        Assert.Same(call, winner)
        match! call with
        | Error(TransportError(:? OperationCanceledException)) -> ()
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``the caller's cancellation still throws with a timeout set`` () : Task =
    task {
        use cancel = new Threading.CancellationTokenSource(TimeSpan.FromMilliseconds 100.)
        let client = ExperianClient(credentials, Endpoints.sandbox, new HangingHandler(), (fun () -> DateTimeOffset.UtcNow), ValueSome(TimeSpan.FromSeconds 30.))
        let! _ = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> Api.DelphiSelect.applicationAsync client (sandboxApplication ()) cancel.Token :> Task)
        ()
    }

[<Theory>]
[<InlineData("""{"Response":{"x":nul Jane Doe of 1 High Street London}}""")>]
[<InlineData("""Jane Doe of 1 High Street London""")>]
[<InlineData("""{"Jane Doe of 1 High Street London":nul}""")>]
let ``a malformed answer's text stays out of describe and printing`` (body: string) : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, body))
        let! result = Api.DelphiSelect.applicationAsync (clientWith handler) (sandboxApplication ()) ct
        let answer = expectUnreadable result
        Assert.Equal(body, answer.Body)
        let failure = InvalidResponse answer
        for text in [ ExperianFailure.describe failure; $"%A{failure}"; string failure; string answer; answer.Reason ] do
            Assert.DoesNotContain("Jane", text)
            Assert.DoesNotContain("High Street", text)
            Assert.DoesNotContain("London", text)
    }

[<Fact>]
let ``Reason is the JSON position of the failure`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "Jane"))
        let! result = Api.DelphiSelect.applicationAsync (clientWith handler) (sandboxApplication ()) ct
        Assert.Equal("Invalid at $, line 0, byte 0", (expectUnreadable result).Reason)
    }

[<Fact>]
let ``a body of null is InvalidResponse, not Ok null`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "null"))
        let! result = Api.DelphiSelect.applicationAsync (clientWith handler) (sandboxApplication ()) ct
        let answer = expectUnreadable result
        Assert.Equal("null", answer.Body)
        Assert.Equal(200, answer.HttpStatus)
    }

[<Fact>]
let ``a call that returns nothing is still Ok`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 204, ""))
        let! result = Api.Idp.deleteWebhookAsync (clientWith handler) Guid.Empty ct
        Assert.True(Result.isOk result, $"%A{result}")
    }

[<Fact>]
let ``Create refuses a timeout HttpClient can't hold, and takes the longest it can`` () =
    let tooLong = TimeSpan.FromDays 30.
    Assert.Throws<ArgumentOutOfRangeException>(fun () -> ExperianClient.Create(credentials, Endpoints.sandbox, tooLong) |> ignore) |> ignore
    Assert.Throws<ArgumentOutOfRangeException>(fun () -> ApertureClient.Create("token", tooLong) |> ignore) |> ignore
    let longest = TimeSpan.FromMilliseconds(float Int32.MaxValue)
    Assert.Equal(longest, ExperianClient.Create(credentials, Endpoints.sandbox, longest).DelphiSelect.HttpClient.Timeout)
    let endless = Threading.Timeout.InfiniteTimeSpan
    Assert.Equal(endless, ExperianClient.Create(credentials, Endpoints.sandbox, endless).DelphiSelect.HttpClient.Timeout)

let private kept () =
    new Net.Http.HttpResponseMessage(Net.HttpStatusCode.OK, Content = new Net.Http.StringContent("{}"))

[<Fact>]
let ``transport exceptions stay TransportError even after an answer was kept`` () : Task =
    task {
        let errors: exn list =
            [ Net.Http.HttpRequestException "lost"; IO.IOException "lost"; OperationCanceledException "lost" ]
        for error in errors do
            let! result =
                Call.guarded ct (fun () ->
                    task {
                        do! LastAnswer.keep (kept ()) ct
                        return raise error
                    })
            match result with
            | Error(TransportError e) -> Assert.Same(error, e)
            | other -> failwith $"%A{(other: Result<unit, ExperianFailure>)}"
    }

[<Fact>]
let ``any other exception after an answer gives only its type name as Reason`` () : Task =
    task {
        let! result =
            Call.guarded ct (fun () ->
                task {
                    do! LastAnswer.keep (kept ()) ct
                    return raise (InvalidOperationException "Jane Doe")
                })
        let answer = expectUnreadable (result: Result<unit, ExperianFailure>)
        Assert.Equal("InvalidOperationException", answer.Reason)
    }

[<Fact>]
let ``quoted numbers read, and a quoted non-number is a JsonException with a path and no value`` () =
    Assert.Equal(42, JsonSerializer.Deserialize<int>("\"42\"", Reading.options))
    Assert.Equal(42L, JsonSerializer.Deserialize<int64>("\"42\"", Reading.options))
    Assert.Equal(1.5, JsonSerializer.Deserialize<double>("\"1.5\"", Reading.options))
    let refused (read: unit -> unit) =
        let e = Assert.Throws<JsonException>(read)
        Assert.Equal("$.a", e.Path)
        Assert.DoesNotContain("VALUE-42", e.Message)
    let body = """{"a":"VALUE-42"}"""
    refused (fun () -> JsonSerializer.Deserialize<Collections.Generic.Dictionary<string, int>>(body, Reading.options) |> ignore)
    refused (fun () -> JsonSerializer.Deserialize<Collections.Generic.Dictionary<string, int64>>(body, Reading.options) |> ignore)
    refused (fun () -> JsonSerializer.Deserialize<Collections.Generic.Dictionary<string, double>>(body, Reading.options) |> ignore)
