module TokenTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Experian
open FakeExperian

let private employers (handler: FakeExperianHandler) (client: ExperianClient) =
    Api.Ned.searchEmployersAsync client "Acme" ValueNone ValueNone ct

[<Fact>]
let ``the token request posts the four credentials as JSON with the Grant_type header`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "[]"))
        let! result = employers handler (clientWith handler)
        expectOk result |> ignore

        let token = Assert.Single handler.TokenCalls
        Assert.Equal("POST", token.Method)
        Assert.Equal("https://sandbox-uk-api.experian.com/oauth2/v1/token", token.Uri)
        Assert.Equal("password", token.Headers["Grant_type"])
        Assert.Equal("application/json", token.Headers["Accept"])
        Assert.Equal("application/json", token.Headers["Content-Type"])
        let body = JsonDocument.Parse(token.Body).RootElement
        Assert.Equal("portal-user@example.com", body.GetProperty("username").GetString())
        Assert.Equal("portal-password", body.GetProperty("password").GetString())
        Assert.Equal("client-id", body.GetProperty("client_id").GetString())
        Assert.Equal("client-secret", body.GetProperty("client_secret").GetString())

        let api = Assert.Single handler.ApiCalls
        Assert.Equal("Bearer tok-1", api.Headers["Authorization"])
    }

[<Fact>]
let ``one token serves later calls until a minute before it expires`` () : Task =
    task {
        let clock = ref (DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero))
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "[]"))
        let client = clientAt clock handler

        let! _ = employers handler client
        clock.Value <- clock.Value.AddMinutes 28.
        let! _ = employers handler client
        Assert.Equal(1, handler.TokenCalls.Length)

        clock.Value <- clock.Value.AddMinutes 1.5
        let! _ = employers handler client
        Assert.Equal(2, handler.TokenCalls.Length)

        Assert.Equal<string list>(
            [ "Bearer tok-1"; "Bearer tok-1"; "Bearer tok-2" ],
            handler.ApiCalls |> List.map (fun c -> c.Headers["Authorization"])
        )
    }

let private refusedSignIn (tokenStatus: int) =
    fun (call: Call) ->
        if isToken call then
            tokenStatus, """{"error":"invalid_grant"}"""
        else
            200, "[]"

[<Theory; InlineData(400); InlineData(401); InlineData(403)>]
let ``refused credentials are not sent again for an hour`` (status: int) : Task =
    task {
        let clock = ref (DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero))
        use handler = new FakeExperianHandler(refusedSignIn status)
        let client = clientAt clock handler

        for _ in 1..5 do
            match! employers handler client with
            | Error failure -> Assert.True(ExperianFailure.isCredentialRefusal failure)
            | Ok _ -> failwith "It should have been refused."

        Assert.Equal(1, handler.TokenCalls.Length)
        Assert.Empty handler.ApiCalls

        clock.Value <- clock.Value.AddMinutes 59.
        let! _ = employers handler client
        Assert.Equal(1, handler.TokenCalls.Length)

        clock.Value <- clock.Value.AddMinutes 1.
        let! _ = employers handler client
        Assert.Equal(2, handler.TokenCalls.Length)
    }

[<Fact>]
let ``concurrent calls with refused credentials send them once`` () : Task =
    task {
        use handler = new FakeExperianHandler(refusedSignIn 401)
        let client = clientWith handler
        let! results = Task.WhenAll [ for _ in 1..20 -> employers handler client ]
        Assert.All(results, fun result -> Assert.True(result.IsError))
        Assert.Equal(1, handler.TokenCalls.Length)
    }

[<Theory>]
[<InlineData(408)>]
[<InlineData(429)>]
[<InlineData(500)>]
[<InlineData(503)>]
let ``a busy or failing server is not a refusal, so the next call asks again`` (status: int) : Task =
    task {
        use handler = new FakeExperianHandler(refusedSignIn status)
        let client = clientWith handler

        for _ in 1..3 do
            match! employers handler client with
            | Error failure -> Assert.False(ExperianFailure.isCredentialRefusal failure)
            | Ok _ -> failwith "It should have failed."

        Assert.Equal(3, handler.TokenCalls.Length)
    }

[<Fact>]
let ``a sign-in that works after the pause clears the refusal`` () : Task =
    task {
        let clock = ref (DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero))
        let accepted = ref false

        use handler =
            new FakeExperianHandler(fun call ->
                if not (isToken call) then 200, "[]"
                elif accepted.Value then 200, tokenJson "tok-1"
                else 401, "{}")

        let client = clientAt clock handler
        let! _ = employers handler client
        accepted.Value <- true
        clock.Value <- clock.Value.AddHours 1.
        let! result = employers handler client
        expectOk result |> ignore
        Assert.Equal(2, handler.TokenCalls.Length)
    }

[<Fact>]
let ``concurrent first calls share one token fetch`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "[]"))
        let client = clientWith handler
        let! results = Task.WhenAll [ for _ in 1..20 -> employers handler client ]
        results |> Array.iter (expectOk >> ignore)
        Assert.Equal(1, handler.TokenCalls.Length)
        Assert.Equal(20, handler.ApiCalls.Length)
    }

[<Fact>]
let ``a 401 fetches a new token and sends the request once more`` () : Task =
    task {
        let attempts = ref 0

        let api (call: Call) =
            attempts.Value <- attempts.Value + 1

            if call.Headers["Authorization"] = "Bearer tok-1" then
                401, """{"error":"invalid_token"}"""
            else
                200, "[]"

        use handler = new FakeExperianHandler(respondWith api)
        let! result = employers handler (clientWith handler)
        expectOk result |> ignore
        Assert.Equal(2, handler.TokenCalls.Length)

        Assert.Equal<string list>(
            [ "Bearer tok-1"; "Bearer tok-2" ],
            handler.ApiCalls |> List.map (fun c -> c.Headers["Authorization"])
        )
    }

[<Fact>]
let ``a second 401 is returned as ApiError`` () : Task =
    task {
        use handler =
            new FakeExperianHandler(respondWith (fun _ -> 401, """{"error":"invalid_token"}"""))

        let! result = employers handler (clientWith handler)

        match result with
        | Error(ApiError(401, body)) -> Assert.Contains("invalid_token", body)
        | other -> failwith $"%A{other}"

        Assert.Equal(2, handler.ApiCalls.Length)
    }

[<Fact>]
let ``refused credentials are TokenRefused and no API call is made`` () : Task =
    task {
        let respond (call: Call) =
            if isToken call then
                401, """{"errors":[{"message":"bad credentials"}]}"""
            else
                200, "[]"

        use handler = new FakeExperianHandler(respond)
        let! result = employers handler (clientWith handler)

        match result with
        | Error(TokenRefused(401, body)) -> Assert.Contains("bad credentials", body)
        | other -> failwith $"%A{other}"

        Assert.Empty handler.ApiCalls
    }

[<Fact>]
let ``a token answer without access_token is TokenRefused`` () : Task =
    task {
        let respond (call: Call) =
            if isToken call then
                200, """{"token_type":"Bearer"}"""
            else
                200, "[]"

        use handler = new FakeExperianHandler(respond)

        match! employers handler (clientWith handler) with
        | Error(TokenRefused(200, _)) -> ()
        | other -> failwith $"%A{other}"
    }

[<Fact>]
let ``expires_in is read as a number or a string, and defaults to 30 minutes`` () =
    let now = DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero)

    let expiry json =
        (Token.parse now json).Value.ExpiresAt - now

    Assert.Equal(TimeSpan.FromSeconds 600., expiry """{"access_token":"a","expires_in":600}""")
    Assert.Equal(TimeSpan.FromSeconds 900., expiry """{"access_token":"a","expires_in":"900"}""")
    Assert.Equal(TimeSpan.FromMinutes 30., expiry """{"access_token":"a"}""")
    Assert.Equal(TimeSpan.FromMinutes 30., expiry """{"access_token":"a","expires_in":"soon"}""")
    Assert.True((Token.parse now """{"access_token":""}""").IsNone)
    Assert.True((Token.parse now "not json").IsNone)
    Assert.True((Token.parse now "[]").IsNone)
    Assert.True((Token.parse now "\"access_token\"").IsNone)
    Assert.True((Token.parse now "5").IsNone)

[<Fact>]
let ``cancellation propagates instead of becoming an Error`` () : Task =
    task {
        use handler = new FakeExperianHandler(respondWith (fun _ -> 200, "[]"))
        use cts = new Threading.CancellationTokenSource()
        do! cts.CancelAsync()

        let! error =
            Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                Api.Ned.searchEmployersAsync (clientWith handler) "Acme" ValueNone ValueNone cts.Token :> Task)

        Assert.NotNull error
    }
