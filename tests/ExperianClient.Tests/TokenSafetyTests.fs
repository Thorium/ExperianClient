/// Tokens and bodies stay on the hosts the client was given.
module TokenSafetyTests

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Xunit
open Experian
open FakeExperian

/// Answers as respond says, and records each request as it arrives, before respond can change it.
type private Recording(respond: HttpRequestMessage -> HttpStatusCode) =
    inherit HttpMessageHandler()
    let seen = Collections.Generic.List<string * string>()

    /// Host and Authorization header of each request that wasn't a sign-in.
    member _.Seen = List.ofSeq seen

    override _.SendAsync(request, _) =
        if request.RequestUri.AbsolutePath.EndsWith "/oauth2/v1/token" then
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent(tokenJson "portal-token")))
        else
            let authorization = if isNull request.Headers.Authorization then "" else string request.Headers.Authorization
            seen.Add((request.RequestUri.Host, authorization))
            let status = respond request
            Task.FromResult(new HttpResponseMessage(status, Content = new StringContent "{}"))

let private isToOtherHost (result: Result<'T, ExperianFailure>) =
    match result with
    | Error(InvalidRequest reason) -> reason.Contains "evil.example"
    | _ -> false

[<Fact>]
let ``the handler made here doesn't follow redirects`` () =
    Assert.False((ExperianClient.CreateHandler() :?> SocketsHttpHandler).AllowAutoRedirect)

/// A caller's handler that follows redirects clears the token and moves the request; a 401 from there must not bring the token back.
[<Fact>]
let ``a 401 after a redirect doesn't send a new token to the other host`` () : Task =
    task {
        let handler =
            new Recording(fun request ->
                request.RequestUri <- Uri "https://evil.example/x"
                request.Headers.Authorization <- null
                HttpStatusCode.Unauthorized)
        let client = ExperianClient.create credentials Endpoints.sandbox handler
        let! result = Api.Kyb.profileAsync client "99999999" FakeExperian.ct
        Assert.True(isToOtherHost result, $"Got {result}")
        Assert.Equal<(string * string) list>([ "sandbox-uk-api.experian.com", "Bearer portal-token" ], handler.Seen)
    }

/// A path value inserted as it is, on a generated client, can leave the host.
[<Fact>]
let ``a Client Gateway path value can't carry the token to another host`` () : Task =
    task {
        let handler = new Recording(fun _ -> HttpStatusCode.OK)
        let client = ExperianClient.create credentials Endpoints.production handler
        let call = client.ClientGateway.GetHybridDeploymentDetails(useCase = @"\/evil.example/x", authorization = null, xCorrelationId = Guid.Empty, auditIds = [| "a" |], cancellationToken = FakeExperian.ct)
        let! result = Call.attempt FakeExperian.ct (fun () -> call)
        Assert.True(isToOtherHost result, $"Got {result}")
        Assert.Empty handler.Seen
    }

[<Fact>]
let ``a token the caller passes is the one sent, with no sign-in`` () : Task =
    task {
        let signIns = ref 0
        let handler =
            new FakeExperianHandler(fun call ->
                if isToken call then
                    signIns.Value <- signIns.Value + 1
                    401, "{}"
                else
                    401, "{}")
        let client = ExperianClient.create credentials Endpoints.production handler
        let call = client.ClientGateway.GetHybridDeploymentDetails(useCase = "pc", authorization = "Bearer caller-jwt", xCorrelationId = Guid.Empty, auditIds = [| "a" |], cancellationToken = FakeExperian.ct)
        let! result = Call.attempt FakeExperian.ct (fun () -> call)
        Assert.True((match result with Error(ApiError(401, _)) -> true | _ -> false), $"Got {result}")
        Assert.Equal(0, signIns.Value)
        let sent = Assert.Single handler.ApiCalls
        Assert.Equal("Bearer caller-jwt", sent.Headers["Authorization"])
    }

[<Fact>]
let ``an endpoint set by hand is one the token may go to`` () : Task =
    task {
        let handler = new Recording(fun _ -> HttpStatusCode.OK)
        let endpoints = { Endpoints.production with Kyb = Uri "https://kyb.example.com/" }
        let client = ExperianClient.create credentials endpoints handler
        let! result = Api.Kyb.profileAsync client "99999999" FakeExperian.ct
        expectOk result |> ignore
        Assert.Equal<(string * string) list>([ "kyb.example.com", "Bearer portal-token" ], handler.Seen)
    }

/// Every product in every region reaches its own address with the token.
[<Fact>]
let ``no region refuses its own hosts`` () : Task =
    task {
        for endpoints in [ Endpoints.sandbox; Endpoints.production; Endpoints.sandboxUs; Endpoints.productionUs; Endpoints.sandboxEu; Endpoints.productionEu ] do
            let handler = new Recording(fun _ -> HttpStatusCode.OK)
            let client = ExperianClient.create credentials endpoints handler
            let! verify = Api.VerifyCpd.plusStatusAsync client "r" (VerifyCpdPlus.CPDStatusRequestParams()) FakeExperian.ct
            expectOk verify |> ignore
            let! kyb = Api.Kyb.profileAsync client "1" FakeExperian.ct
            expectOk kyb |> ignore
    }
