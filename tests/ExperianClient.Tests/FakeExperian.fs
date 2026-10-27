module FakeExperian

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Experian

type Call =
    { Method: string
      /// Absolute URI as sent.
      Uri: string
      Body: string
      Headers: Map<string, string> }

/// Answers each request with respond call, and records the calls; safe for concurrent requests.
type FakeExperianHandler(respond: Call -> int * string) =
    inherit HttpMessageHandler()
    let calls = Collections.Concurrent.ConcurrentQueue<Call>()

    member _.Calls = List.ofSeq calls
    member this.ApiCalls = this.Calls |> List.filter (fun c -> not (c.Uri.EndsWith "/oauth2/v1/token"))
    member this.TokenCalls = this.Calls |> List.filter (fun c -> c.Uri.EndsWith "/oauth2/v1/token")

    override _.SendAsync(request, ct) =
        task {
            ct.ThrowIfCancellationRequested()
            let! body = if isNull request.Content then Task.FromResult "" else request.Content.ReadAsStringAsync ct
            let headers =
                Seq.append request.Headers (if isNull request.Content then Seq.empty else request.Content.Headers)
                |> Seq.map (fun h -> h.Key, String.Join(",", h.Value))
                |> Map.ofSeq
            let call = { Method = request.Method.Method; Uri = request.RequestUri.AbsoluteUri; Body = body; Headers = headers }
            calls.Enqueue call
            let status, json = respond call
            let response = new HttpResponseMessage(enum<HttpStatusCode> status)
            if json <> "" then
                response.Content <- new StringContent(json, Encoding.UTF8, "application/json")
            return response
        }

let credentials =
    { Username = "portal-user@example.com"
      Password = "portal-password"
      ClientId = "client-id"
      ClientSecret = "client-secret" }

let tokenJson (accessToken: string) = $"""{{"issued_at":"1761580000000","expires_in":"1800","token_type":"Bearer","access_token":"{accessToken}","refresh_token":"r"}}"""

let isToken (call: Call) = call.Uri.EndsWith "/oauth2/v1/token"

/// Token calls get tok-1, tok-2, ... in order; API calls go to api.
let respondWith (api: Call -> int * string) =
    let issued = ref 0
    fun (call: Call) ->
        if isToken call then
            200, tokenJson $"tok-{Interlocked.Increment &issued.contents}"
        else
            api call

let clientWith (handler: FakeExperianHandler) = ExperianClient.create credentials Endpoints.sandbox handler

/// A client whose clock is read from `clock`.
let clientAt (clock: DateTimeOffset ref) (handler: FakeExperianHandler) =
    ExperianClient(credentials, Endpoints.sandbox, handler, fun () -> clock.Value)

let expectOk =
    function
    | Ok v -> v
    | Error e -> failwith (ExperianFailure.describe e)

let ct = CancellationToken.None
