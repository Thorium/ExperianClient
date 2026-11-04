/// KYB, AML Flags, date of birth, SSN and Experian Verify against a stand-in: what goes on the wire.
module BusinessAndVerifyTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Experian
open FakeExperian

let private portal (body: string) =
    new FakeExperianHandler(fun call ->
        if call.Uri.EndsWith "/oauth2/v1/token" then 200, """{"access_token":"t1","expires_in":"1800"}""" else 200, body)

let private clientOn endpoints (handler: FakeExperianHandler) = ExperianClient.create credentials endpoints handler

let private soleCall (handler: FakeExperianHandler) = handler.ApiCalls |> List.exactlyOne

[<Fact>]
let ``a KYB search sends only what was asked, with flags as true and false`` () : Task =
    task {
        let handler = portal "{}"
        let search = { Api.Kyb.BusinessSearch.Default with Name = ValueSome "Acme & Sons"; SearchCorporates = ValueSome true; SearchNonCorps = ValueSome false }
        let! result = Api.Kyb.searchBusinessesAsync (clientOn Endpoints.sandbox handler) search FakeExperian.ct
        expectOk result |> ignore
        let call = soleCall handler
        Assert.StartsWith("https://sandbox-uk-api.experian.com/risk/business/v2/businesstargeter?", call.Uri)
        Assert.Contains("searchcorporates=true", call.Uri)
        Assert.Contains("searchnoncorps=false", call.Uri)
        Assert.DoesNotContain("postcode", call.Uri)
        Assert.DoesNotContain("Acme & Sons", call.Uri)
        Assert.Equal("Bearer t1", call.Headers["Authorization"])
    }

[<Fact>]
let ``a KYB company number stays one path segment`` () : Task =
    task {
        let handler = portal "{}"
        let client = clientOn Endpoints.sandbox handler
        let! result = Api.Kyb.ultimateOwnersAsync client "12/34" FakeExperian.ct
        expectOk result |> ignore
        Assert.Equal("https://sandbox-uk-api.experian.com/compliance/business/v1/registeredcompany/12%2F34/ubo", (soleCall handler).Uri)
        let! refused = Api.Kyb.profileAsync client ".." FakeExperian.ct
        Assert.True(match refused with Error(InvalidRequest _) -> true | _ -> false)
    }

[<Fact>]
let ``AML flags ask the company call for its blocks`` () : Task =
    task {
        let handler = portal """{"Results":{}}"""
        let client = clientOn Endpoints.sandbox handler
        let request = client.Kyb.Deserialize("""{"BusinessRef":"00653331","LegalStatus":"L","DataBlocks":["AMLFlags"]}""", typeof<Kyb.KybDataRequestBody>) :?> Kyb.KybDataRequestBody
        let! result = Api.AmlFlags.companyAsync client request FakeExperian.ct
        expectOk result |> ignore
        let call = soleCall handler
        Assert.Equal("https://sandbox-uk-api.experian.com/compliance/business/v1/company/kybdata", call.Uri)
        Assert.Contains("AMLFlags", call.Body)
    }

/// The paths of the values in JSON, without nulls.
let private fieldsOf (json: string) =
    let rec walk (prefix: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object -> [ for property in element.EnumerateObject() do yield! walk $"{prefix}.{property.Name}" property.Value ]
        | JsonValueKind.Array -> [ for item in element.EnumerateArray() do yield! walk prefix item ]
        | JsonValueKind.Null -> []
        | _ -> [ prefix ]
    use document = JsonDocument.Parse json
    walk "" document.RootElement |> Set.ofList

/// Every request example in the age threshold spec; date of birth is the same call.
[<Fact>]
let ``the age threshold examples keep their fields`` () =
    let client = clientOn Endpoints.sandbox (portal "{}")
    let text = IO.File.ReadAllText(IO.Path.Combine(__SOURCE_DIRECTORY__, "Samples", "uk-age-threshold.json"))
    use examples = JsonDocument.Parse text
    let lost =
        [ for example in examples.RootElement.EnumerateArray() do
              let sample = example.GetRawText()
              let written = client.AgeAndDateOfBirth.Serialize(client.AgeAndDateOfBirth.Deserialize(sample, typeof<AgeAndDateOfBirth.RequestMessage>))
              yield! Set.difference (fieldsOf sample) (fieldsOf written) ]
        |> List.distinct
    Assert.True(examples.RootElement.GetArrayLength() > 10)
    Assert.Equal<string>("", String.Join(" ", lost))

[<Fact>]
let ``a date of birth check goes to CrossCore with its request type`` () : Task =
    task {
        let handler = portal """{"responseHeader":{"responseCode":"R0201","responseType":"INFO"},"clientResponsePayload":{"orchestrationDecisions":[{"decision":"CONTINUE"}]}}"""
        let client = clientOn Endpoints.sandbox handler
        let request = client.AgeAndDateOfBirth.Deserialize("""{"header":{"tenantId":"t","requestType":"DoBVerify-Standalone","clientReferenceId":"r","messageTime":"2026-11-04T10:00:00Z","options":{}},"payload":{}}""", typeof<AgeAndDateOfBirth.RequestMessage>) :?> AgeAndDateOfBirth.RequestMessage
        let! result = Api.AgeAndDateOfBirth.checkAsync client request ValueNone FakeExperian.ct
        expectOk result |> ignore
        let call = soleCall handler
        Assert.Equal("https://sandbox-uk-api.experian.com/da/ccis-devportalapis/v1/api", call.Uri)
        Assert.Contains("DoBVerify-Standalone", call.Body)
    }

[<Fact>]
let ``an SSN check sends its onboarding headers`` () : Task =
    task {
        let handler = portal """{"globalTransactionID":"g1","cvsResponseList":[{"ssnVerificationCode":"Y"}]}"""
        let client = clientOn Endpoints.sandboxUs handler
        let request = client.SsnVerification.Deserialize("""{"ein":"324658978","cvsRequestList":[{"ssn":"258886506","lastName":"MOUSE"}]}""", typeof<SsnVerification.verifyRequest>) :?> SsnVerification.verifyRequest
        let! result = Api.SsnVerification.verifyAsync client "0000000" "1234" request (ValueSome "ABC123") FakeExperian.ct
        let answer = expectOk result
        Assert.Equal(Some "g1", answer.GlobalTransactionId)
        let call = soleCall handler
        Assert.Equal("https://sandbox-us-api.experian.com/consumerservices/ecbsv/v1/eden/verify", call.Uri)
        Assert.Equal("0000000", call.Headers["subcode"])
        Assert.Equal("1234", call.Headers["companyId"])
        Assert.Equal("ABC123", call.Headers["externalTransactionId"])
        Assert.Contains("258886506", call.Body)
    }

/// The sandbox answers on v1, live on v2.
[<Fact>]
let ``every Instant report reaches the one Verify call`` () : Task =
    task {
        let handler = portal "{}"
        let client = clientOn Endpoints.sandboxUs handler
        let ct = FakeExperian.ct
        let! core = Api.Verify.coreAsync client "s" "r" (VerifyCore.RequestParams()) ct
        let! plus = Api.Verify.plusAsync client "s" "r" (VerifyPlus.RequestParams()) ct
        let! premium = Api.Verify.premiumAsync client "s" "r" (VerifyPremium.voieRequestParams()) ct
        let! prequal = Api.Verify.premiumPrequalAsync client "s" "r" (VerifyPremiumPrequal.voieRequestParams()) ct
        let! employment = Api.Verify.employmentScreeningAsync client "s" "r" (VerifyEmployment.RequestParams()) ct
        let! government = Api.Verify.governmentAsync client "s" "r" (VerifyGovernment.RequestParams()) ct
        let! voe = Api.Verify.reverifyEmploymentAsync client "s" "r" (ReverifyVoe.RequestParams()) ct
        let! voie = Api.Verify.reverifyIncomeAndEmploymentAsync client "s" "r" (ReverifyVoie.voieRequestParams()) ct
        for ok in [ Result.isOk core; Result.isOk plus; Result.isOk premium; Result.isOk prequal; Result.isOk employment; Result.isOk government; Result.isOk voe; Result.isOk voie ] do
            Assert.True ok
        Assert.Equal(8, handler.ApiCalls.Length)
        for call in handler.ApiCalls do
            Assert.Equal("https://sandbox-us-api.experian.com/consumerservices/verification/v1/experianverify", call.Uri)
            Assert.Equal("s", call.Headers["subcode"])
            Assert.Equal("r", call.Headers["clientReferenceId"])
    }

[<Fact>]
let ``the permissioned Verify flow goes live on its own host`` () : Task =
    task {
        let handler = portal "{}"
        let client = clientOn Endpoints.productionUs handler
        let ct = FakeExperian.ct
        let! widget = Api.VerifyCpd.plusWidgetAsync client "r" (VerifyCpdPlus.CPDRequestParams()) ct
        let! status = Api.VerifyCpd.plusStatusAsync client "r" (VerifyCpdPlus.CPDStatusRequestParams()) ct
        let! report = Api.VerifyCpd.plusReportAsync client "r" (VerifyCpdPlus.CPDStatusRequestParams()) ct
        for ok in [ Result.isOk widget; Result.isOk status; Result.isOk report ] do
            Assert.True ok
        Assert.Equal<string list>(
            [ "https://us-ev-api.experian.com/consumerservices/evcpd/v1/widget"
              "https://us-ev-api.experian.com/consumerservices/evcpd/v1/status"
              "https://us-ev-api.experian.com/consumerservices/evcpd/v1/evreport" ],
            handler.ApiCalls |> List.map (fun call -> call.Uri))
        Assert.Equal("https://us-api.experian.com/oauth2/v1/token", (handler.TokenCalls |> List.exactlyOne).Uri)
    }

[<Fact>]
let ``live Verify asks for v2`` () : Task =
    task {
        let handler = portal "{}"
        let! result = Api.Verify.coreAsync (clientOn Endpoints.productionUs handler) "s" "r" (VerifyCore.RequestParams()) FakeExperian.ct
        expectOk result |> ignore
        Assert.Equal("https://us-api.experian.com/consumerservices/verification/v2/experianverify", (soleCall handler).Uri)
    }

[<Fact>]
let ``a director name search asks for directors and secretaries unless told otherwise`` () : Task =
    task {
        let handler = portal "{}"
        let search = { Api.Kyb.DirectorSearch.Default with Forename = ValueSome "John"; Surname = ValueSome "Smith" }
        let! result = Api.Kyb.searchDirectorsAsync (clientOn Endpoints.sandbox handler) search FakeExperian.ct
        expectOk result |> ignore
        let call = soleCall handler
        Assert.Contains("directorsflag=true", call.Uri)
        Assert.Contains("secretariesflag=true", call.Uri)
    }

/// The answer's own field names, in any case: Experian writes Ruleid where the spec has RuleId, and reading ignores case.
let private lostFrom (sample: string) (written: string) =
    let folded (fields: Set<string>) = fields |> Set.map (fun field -> field.ToLowerInvariant())
    String.Join(" ", Set.difference (folded (fieldsOf sample)) (folded (fieldsOf written)))

let private answerOf (name: string) =
    IO.File.ReadAllText(IO.Path.Combine(__SOURCE_DIRECTORY__, "Samples", name))

/// Answers Experian's sandbox gave, read and written back.
[<Fact>]
let ``real answers keep their fields`` () =
    let client = clientOn Endpoints.sandbox (portal "{}")
    let onKyb (name: string) (target: Type) =
        let sample = answerOf name
        lostFrom sample (client.Kyb.Serialize(client.Kyb.Deserialize(sample, target)))
    Assert.Equal<string>("", onKyb "uk-aml-answer.json" typeof<Kyb.KybDataResponse>)
    Assert.Equal<string>("", onKyb "uk-aml-flags-answer.json" typeof<Kyb.KybDataResponse>)
    Assert.Equal<string>("", onKyb "uk-kyb-ubo-answer.json" typeof<Kyb.UBOResponse>)
    Assert.Equal<string>("", onKyb "uk-kyb-search-answer.json" typeof<Kyb.BusinessTargeterResponse>)
    // The sandbox's one report is a Plus report; other reports name their fields differently
    let verify = answerOf "us-verify-answer.json"
    Assert.Equal<string>("", lostFrom verify (client.VerifyPlus.Serialize(client.VerifyPlus.Deserialize(verify, typeof<VerifyPlus.Response>))))
