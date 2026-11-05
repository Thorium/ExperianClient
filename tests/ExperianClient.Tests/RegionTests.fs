/// The regions beyond the UK, against a stand-in: what goes on the wire, and Serasa Experian's sign-in.
module RegionTests

open System
open System.Threading.Tasks
open Xunit
open Experian
open FakeExperian

let private credentials =
    { Username = "user"
      Password = "pass"
      ClientId = "client"
      ClientSecret = "secret" }

let private portalAnswers (body: string) (call: Call) =
    if call.Uri.EndsWith "/oauth2/v1/token" then
        200, """{"access_token":"t1","expires_in":"1800"}"""
    else
        200, body

[<Fact>]
let ``a US credit profile goes to the US host with the reference asked for`` () : Task =
    task {
        let handler = new FakeExperianHandler(portalAnswers """{"creditProfile":[]}""")
        let client = ExperianClient.create credentials Endpoints.sandboxUs handler
        let request = client.CreditProfile.Deserialize("""{"consumerPii":{},"requestor":{"subscriberCode":"123"}}""", typeof<CreditProfile.tecCreditProfileRequest>) :?> CreditProfile.tecCreditProfileRequest
        let! result = Api.CreditProfile.tecConnectAsync client request (ValueSome "ref-1") FakeExperian.ct
        expectOk result |> ignore
        let call = handler.ApiCalls |> List.exactlyOne
        Assert.Equal("https://sandbox-us-api.experian.com/consumerservices/credit-profile/v1/tec-connect-credit", call.Uri)
        Assert.Equal("ref-1", call.Headers["clientReferenceId"])
        Assert.Contains("subscriberCode", call.Body)
        Assert.Equal("https://sandbox-us-api.experian.com/oauth2/v1/token", (handler.TokenCalls |> List.exactlyOne).Uri)
    }

[<Fact>]
let ``a web data score asks the EU host about the company, and reads the score`` () : Task =
    task {
        let handler = new FakeExperianHandler(portalAnswers """{"score":0.25,"success":true}""")
        let client = ExperianClient.create credentials Endpoints.sandboxEu handler
        let! result = Api.WebData.scoreAsync client (DateOnly(2026, 1, 31)) "Café & Co" "Calle 1" "Madrid" "id-1" FakeExperian.ct
        let score = expectOk result
        Assert.Equal(0.25, score.Score)
        let call = handler.ApiCalls |> List.exactlyOne
        Assert.StartsWith("https://sandbox-eu-api.experian.com/creditrisk/webdata/gdp/v1/webscores/test-score?", call.Uri)
        Assert.Contains("date=2026-01-31", call.Uri)
        Assert.Contains("town=Madrid", call.Uri)
        Assert.DoesNotContain("Café & Co", call.Uri)
    }

[<Fact>]
let ``Serasa signs in with the client alone, and its token is used`` () : Task =
    task {
        let handler =
            new FakeExperianHandler(fun call ->
                if call.Uri.EndsWith "/security/iam/v1/client-identities/login" then
                    201, """{"accessToken":"serasa-token","tokenType":"Bearer","expiresIn":"3599"}"""
                else
                    201, "{}")

        let client = ExperianClient.create (Credentials.ofClient "id" "shh") Endpoints.productionBrazil handler
        let request = client.OpenBankingScore.Deserialize("""{"customerId":"43908445778"}""", typeof<OpenBankingScore.Score>) :?> OpenBankingScore.Score
        let! result = Api.OpenBanking.scoreAsync client request FakeExperian.ct
        Assert.True(Result.isOk result)
        let signIn = handler.Calls |> List.find (fun call -> call.Uri.EndsWith "/login")
        Assert.Equal("https://api.serasaexperian.com.br/security/iam/v1/client-identities/login", signIn.Uri)
        Assert.Equal("Basic " + Convert.ToBase64String(Text.Encoding.UTF8.GetBytes "id:shh"), signIn.Headers["Authorization"])
        Assert.DoesNotContain("password", signIn.Body)
        let call = handler.Calls |> List.find (fun call -> call.Uri.EndsWith "/scores")
        Assert.Equal("https://api.serasaexperian.com.br/open-banking/score/v1/scores", call.Uri)
        Assert.Equal("Bearer serasa-token", call.Headers["Authorization"])
    }

[<Fact>]
let ``every Brazilian product has its own address`` () =
    let brazil = Endpoints.productionBrazil
    Assert.Equal("/open-banking/income-verification/v1/", brazil.IncomeVerification.AbsolutePath)
    Assert.Equal("/open-banking/categorization/v1/", brazil.TransactionCategorization.AbsolutePath)
    Assert.Equal("/open-banking/affordability-check/v1/", brazil.AffordabilityCheck.AbsolutePath)
    Assert.Equal(SerasaClient, brazil.SignIn)
    Assert.Equal(PortalPassword, Endpoints.productionUs.SignIn)

[<Fact>]
let ``a credential for the client alone prints nothing secret`` () =
    Assert.DoesNotContain("shh", string (Credentials.ofClient "id" "shh"))

[<Fact>]
let ``the US sample keeps its fields when read and written`` () =
    let handler = new FakeExperianHandler(portalAnswers "{}")
    let client = ExperianClient.create credentials Endpoints.sandboxUs handler
    let sample = IO.File.ReadAllText(IO.Path.Combine(__SOURCE_DIRECTORY__, "Samples", "us-tec-connect.json"))
    let request = client.CreditProfile.Deserialize(sample, typeof<CreditProfile.tecCreditProfileRequest>)
    let written = client.CreditProfile.Serialize request
    let fields (json: string) =
        let rec walk (prefix: string) (element: Text.Json.JsonElement) =
            match element.ValueKind with
            | Text.Json.JsonValueKind.Object ->
                [ for property in element.EnumerateObject() do yield! walk $"{prefix}.{property.Name}" property.Value ]
            | Text.Json.JsonValueKind.Array -> [ for item in element.EnumerateArray() do yield! walk prefix item ]
            | _ -> [ prefix ]
        walk "" (Text.Json.JsonDocument.Parse json).RootElement |> Set.ofList
    Assert.Equal<string>("", String.Join(" ", Set.difference (fields sample) (fields written)))
    Assert.Equal<string>("", String.Join(" ", Set.difference (fields written) (fields sample)))

/// The fields of a request, read from JSON, in the paths of its values.
let private fieldsOf (json: string) =
    let rec walk (prefix: string) (element: Text.Json.JsonElement) =
        match element.ValueKind with
        | Text.Json.JsonValueKind.Object ->
            [ for property in element.EnumerateObject() do yield! walk $"{prefix}.{property.Name}" property.Value ]
        | Text.Json.JsonValueKind.Array -> [ for item in element.EnumerateArray() do yield! walk prefix item ]
        | Text.Json.JsonValueKind.Null -> []
        | _ -> [ prefix ]

    walk "" (Text.Json.JsonDocument.Parse json).RootElement |> Set.ofList

let private sampleOf (name: string) =
    IO.File.ReadAllText(IO.Path.Combine(__SOURCE_DIRECTORY__, "Samples", name))

/// Experian's examples send fields its specs leave out, which generated types would drop without a word.
[<Fact>]
let ``Experian's samples of the newer products keep every field`` () =
    let handler = new FakeExperianHandler(portalAnswers "{}")
    let client = ExperianClient.create credentials Endpoints.sandbox handler
    let bank = sampleOf "uk-bank-account-verify.json"
    let intl = sampleOf "intl-payments.json"
    let instant = sampleOf "us-instant-credit.json"
    Assert.Equal<string>("", String.Join(" ", Set.difference (fieldsOf bank) (fieldsOf (client.BankAccountVerify.Serialize(client.BankAccountVerify.Deserialize(bank, typeof<BankAccountVerify.RequestMessage>))))))
    Assert.Equal<string>("", String.Join(" ", Set.difference (fieldsOf intl) (fieldsOf (client.InternationalPayments.Serialize(client.InternationalPayments.Deserialize(intl, typeof<InternationalPayments.RequestMessage>))))))
    Assert.Equal<string>("", String.Join(" ", Set.difference (fieldsOf instant) (fieldsOf (client.InstantCredit.Serialize(client.InstantCredit.Deserialize(instant, typeof<InstantCredit.attributesRequest>))))))

[<Theory>]
[<InlineData("{}")>]
[<InlineData("""{"score":0.0,"success":false}""")>]
[<InlineData("null")>]
let ``a web data answer that isn't a score is an error, not the lowest risk`` (body: string) : Task =
    task {
        let handler = new FakeExperianHandler(portalAnswers body)
        let client = ExperianClient.create credentials Endpoints.sandboxEu handler
        let! result = Api.WebData.scoreAsync client (DateOnly(2026, 1, 31)) "Co" "Calle 1" "Madrid" "id-1" FakeExperian.ct

        match result with
        | Error _ -> ()
        | Ok score -> failwith $"%A{score}"
    }
