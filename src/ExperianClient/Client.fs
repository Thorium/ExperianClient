namespace Experian

open System
open System.Globalization
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// The developer-portal user and the registered app's client id and secret; the token endpoint needs all four.
/// Printing one (%A, ToString) shows the username and client id only.
[<StructuredFormatDisplay("{Display}")>]
type Credentials =
    { Username: string
      Password: string
      ClientId: string
      ClientSecret: string }

    member this.Display = $"{{ Username = {this.Username}; ClientId = {this.ClientId}; Password and ClientSecret hidden }}"
    override this.ToString() = this.Display

/// The token endpoint and each product's base address. Product addresses end with '/': generated paths resolve against them.
type Endpoints =
    { Token: Uri
      DelphiSelect: Uri
      Kyc: Uri
      Ned: Uri
      WorkReport: Uri
      Idp: Uri
      PowerCurve: Uri }

module Endpoints =

    /// The bundled specs' base paths on one host.
    let onHost (host: string) =
        let at (path: string) = Uri($"https://{host}{path}")
        { Token = at "/oauth2/v1/token"
          DelphiSelect = at "/risk/consumer/v2/"
          Kyc = at "/da/ccis-devportalapis/"
          Ned = at "/"
          WorkReport = at "/"
          Idp = at "/"
          PowerCurve = at "/decisioning/experianone/customeracquisition/v1/" }

    let sandbox = onHost "sandbox-uk-api.experian.com"

    /// The KYC spec only names its sandbox path; set Kyc if Experian gives a different live one.
    let production = onHost "uk-api.experian.com"

type ExperianFailure =
    /// The request was not sent: an argument cannot be valid.
    | InvalidRequest of reason: string
    /// Experian answered with a non-success status; body is the response text.
    | ApiError of httpStatus: int * body: string
    /// The token endpoint refused the credentials, or answered without an access_token.
    | TokenRefused of httpStatus: int * body: string
    /// The request was not answered, or the answer could not be read.
    | TransportError of exn

module ExperianFailure =

    /// One line saying what went wrong, for logs and exception messages.
    let describe failure =
        match failure with
        | InvalidRequest reason -> $"Not sent: {reason}"
        | ApiError(status, "") -> $"Experian answered HTTP {status}"
        | ApiError(status, body) -> $"Experian answered HTTP {status}: {body}"
        | TokenRefused(status, body) -> $"Experian refused the token request (HTTP {status}): {body}"
        | TransportError e -> $"Request failed: {e.Message}"

/// Raised inside a call when the token endpoint refuses; calls return it as TokenRefused.
exception TokenRefusedException of httpStatus: int * body: string

/// A bearer token and the time after which it is not used. Printing one shows the expiry only.
[<StructuredFormatDisplay("{Display}")>]
type AccessToken =
    { Value: string
      ExpiresAt: DateTimeOffset }

    member this.Display = $"{{ Value hidden; ExpiresAt = {this.ExpiresAt:o} }}"
    override this.ToString() = this.Display

module internal Wire =

    /// Experian's gateway answers 415 to "application/json; charset=utf-8"; JSON is UTF-8 by definition, so the charset is dropped.
    let plainJson (content: HttpContent) =
        if not (isNull content || isNull content.Headers.ContentType) && content.Headers.ContentType.MediaType = "application/json" then
            content.Headers.ContentType.CharSet <- null

/// Reads a number or boolean in a string field as its JSON text: Experian sends numeric-looking codes (scorecard ids, CIFAS codes) unquoted.
type internal LenientStringConverter() =
    inherit Serialization.JsonConverter<string>()

    override _.Read(reader, _, _) =
        match reader.TokenType with
        | JsonTokenType.String -> reader.GetString()
        | JsonTokenType.Number
        | JsonTokenType.True
        | JsonTokenType.False ->
            if reader.HasValueSequence then
                let sequence = reader.ValueSequence
                Encoding.UTF8.GetString(Buffers.BuffersExtensions.ToArray(&sequence))
            else
                Encoding.UTF8.GetString reader.ValueSpan
        | token -> raise (JsonException $"Expected a string, got {token}")

    override _.Write(writer, value, _) = writer.WriteStringValue value

/// Number converters that also read quoted numbers. Registered converters reach option elements too, which NumberHandling does not.
type internal LenientInt32Converter() =
    inherit Serialization.JsonConverter<int>()
    override _.Read(reader, _, _) =
        if reader.TokenType = JsonTokenType.String then Int32.Parse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture) else reader.GetInt32()
    override _.Write(writer, value, _) = writer.WriteNumberValue value

type internal LenientInt64Converter() =
    inherit Serialization.JsonConverter<int64>()
    override _.Read(reader, _, _) =
        if reader.TokenType = JsonTokenType.String then Int64.Parse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture) else reader.GetInt64()
    override _.Write(writer, value, _) = writer.WriteNumberValue value

type internal LenientDoubleConverter() =
    inherit Serialization.JsonConverter<double>()
    override _.Read(reader, _, _) =
        if reader.TokenType = JsonTokenType.String then Double.Parse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture) else reader.GetDouble()
    override _.Write(writer, value, _) = writer.WriteNumberValue value

module internal Placeholder =

    /// "" and "-" are what Experian sends for an absent date or number.
    let isAbsent (text: string) =
        match text.Trim() with
        | ""
        | "-" -> true
        | _ -> false

/// Reads "" or "-" in an optional non-string field as None.
type internal BlankAsNoneConverter<'T>() =
    inherit Serialization.JsonConverter<'T option>()

    override _.HandleNull = true

    override _.Read(reader, _, options) =
        match reader.TokenType with
        | JsonTokenType.Null -> None
        | JsonTokenType.String when Placeholder.isAbsent (reader.GetString()) -> None
        | _ -> Some(JsonSerializer.Deserialize<'T>(&reader, options))

    override _.Write(writer, value, options) =
        match value with
        | None -> writer.WriteNullValue()
        | Some v -> JsonSerializer.Serialize(writer, v, options)

type internal BlankAsNoneFactory() =
    inherit Serialization.JsonConverterFactory()

    override _.CanConvert t =
        t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>> && t.GetGenericArguments()[0] <> typeof<string>

    override _.CreateConverter(t, _) =
        Activator.CreateInstance(typedefof<BlankAsNoneConverter<_>>.MakeGenericType(t.GetGenericArguments())) :?> Serialization.JsonConverter

module internal EncodedJson =

    /// The JSON inside a body that is one JSON string holding an object or array, as the KYC sandbox answers.
    let decoded (text: string) =
        if text.Length > 1 && text.TrimStart().StartsWith "\"" then
            try
                match JsonSerializer.Deserialize<string> text with
                | null -> ValueNone
                | inner ->
                    let trimmed = inner.TrimStart()
                    if trimmed.StartsWith "{" || trimmed.StartsWith "[" then
                        use _ = JsonDocument.Parse inner
                        ValueSome inner
                    else
                        ValueNone
            with :? JsonException -> ValueNone
        else
            ValueNone

module internal Token =

    /// Tokens are renewed this long before Experian's expiry, so a request never carries one that expires in flight.
    let renewBefore = TimeSpan.FromMinutes 1.

    /// Experian documents 30-minute tokens; used when expires_in is missing or unreadable.
    let defaultLifetime = TimeSpan.FromMinutes 30.

    let request (credentials: Credentials) (tokenUri: Uri) =
        let body =
            JsonSerializer.Serialize(
                {| username = credentials.Username
                   password = credentials.Password
                   client_id = credentials.ClientId
                   client_secret = credentials.ClientSecret |})
        let request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
        request.Headers.TryAddWithoutValidation("Grant_type", "password") |> ignore
        request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        Wire.plainJson request.Content
        request

    /// Seconds from a number or a numeric string; Experian has sent both.
    let private seconds (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Number -> ValueSome(element.GetDouble())
        | JsonValueKind.String ->
            match Double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value -> ValueSome value
            | _ -> ValueNone
        | _ -> ValueNone

    /// The token in a 2xx token response, expiring expires_in after `now`; issued_at is ignored so clock skew does not matter.
    let parse (now: DateTimeOffset) (text: string) =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement
            match root.TryGetProperty "access_token" with
            | true, token when token.ValueKind = JsonValueKind.String && token.GetString() <> "" ->
                let lifetime =
                    match root.TryGetProperty "expires_in" with
                    | true, expires ->
                        match seconds expires with
                        | ValueSome s when s > 0. -> TimeSpan.FromSeconds s
                        | _ -> defaultLifetime
                    | _ -> defaultLifetime
                ValueSome { Value = token.GetString(); ExpiresAt = now + lifetime }
            | _ -> ValueNone
        with :? JsonException -> ValueNone

/// Fetches the bearer token and keeps it until a minute before it expires; concurrent callers share one fetch.
[<Sealed>]
type TokenSource internal (credentials: Credentials, tokenUri: Uri, invoker: HttpMessageInvoker, now: unit -> DateTimeOffset) =
    let gate = new SemaphoreSlim(1, 1)

    /// A reference cell, not a voption: it is read outside the gate, and a struct read could be torn.
    let mutable current: AccessToken option = None

    let usable () =
        match current with
        | Some token when token.ExpiresAt - Token.renewBefore > now () -> ValueSome token
        | _ -> ValueNone

    let fetch (ct: CancellationToken) =
        task {
            use request = Token.request credentials tokenUri
            use! response = invoker.SendAsync(request, ct)
            let! text = response.Content.ReadAsStringAsync ct
            let status = int response.StatusCode
            return
                if not response.IsSuccessStatusCode then
                    raise (TokenRefusedException(status, text))
                else
                    match Token.parse (now ()) text with
                    | ValueSome token -> token
                    | ValueNone -> raise (TokenRefusedException(status, text))
        }

    /// A valid token, fetched if there is none or it is about to expire.
    member _.GetAsync(ct: CancellationToken) : Task<AccessToken> =
        match usable () with
        | ValueSome token -> Task.FromResult token
        | ValueNone ->
            task {
                do! gate.WaitAsync ct
                try
                    match usable () with
                    | ValueSome token -> return token
                    | ValueNone ->
                        let! token = fetch ct
                        current <- Some token
                        return token
                finally
                    gate.Release() |> ignore
            }

    /// Drops the token if it is still the cached one, so the next GetAsync fetches a new one.
    member _.Invalidate(stale: AccessToken) =
        match current with
        | Some token when obj.ReferenceEquals(token, stale) -> current <- None
        | _ -> ()

/// Adds the bearer token to every request and sends JSON without a charset; a 401 drops the token and the request is sent once more
/// with a fresh one. A success body that is one JSON string holding an object or array is replaced by that JSON.
type internal BearerHandler(tokens: TokenSource, inner: HttpMessageHandler) =
    inherit DelegatingHandler(inner)

    member private _.SendInner(request, ct) = base.SendAsync(request, ct)

    member private this.SendAuthorized(request: HttpRequestMessage, ct) =
        task {
            let! token = tokens.GetAsync ct
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token.Value)
            Wire.plainJson request.Content
            let! response = this.SendInner(request, ct)
            if response.StatusCode <> HttpStatusCode.Unauthorized then
                return response
            else
                response.Dispose()
                tokens.Invalidate token
                let! fresh = tokens.GetAsync ct
                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", fresh.Value)
                return! this.SendInner(request, ct)
        }

    override this.SendAsync(request, ct) =
        task {
            let! response = this.SendAuthorized(request, ct)
            let isJson =
                not (isNull response.Content)
                && (isNull response.Content.Headers.ContentType || response.Content.Headers.ContentType.MediaType = "application/json")
            if not (response.IsSuccessStatusCode && isJson) then
                return response
            else
                let! text = response.Content.ReadAsStringAsync ct
                return
                    match EncodedJson.decoded text with
                    | ValueSome json ->
                        response.Content <- new StringContent(json, Encoding.UTF8, "application/json")
                        response
                    | ValueNone -> response
        }

/// An Experian UK connection: one token cache and the generated client of each product over one shared handler.
/// Keep it for the application's lifetime; the handler stays the caller's (it is never disposed here).
[<Sealed>]
type ExperianClient internal (credentials: Credentials, endpoints: Endpoints, handler: HttpMessageHandler, now: unit -> DateTimeOffset) =
    let tokens = TokenSource(credentials, endpoints.Token, new HttpMessageInvoker(handler, false), now)
    let authorized = new BearerHandler(tokens, handler)

    /// Unset optional fields are left out of request bodies rather than sent as null. Responses are read leniently:
    /// strings may arrive as numbers, numbers as strings, optional dates and numbers as "", and names in another case
    /// (NED sends employerid for employerId); requests are written as the specs type and name them.
    let json =
        let options =
            JsonSerializerOptions(
                DefaultIgnoreCondition = Serialization.JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true)
        options.Converters.Add(BlankAsNoneFactory())
        options.Converters.Add(LenientStringConverter())
        options.Converters.Add(LenientInt32Converter())
        options.Converters.Add(LenientInt64Converter())
        options.Converters.Add(LenientDoubleConverter())
        options

    let http (baseAddress: Uri) = new HttpClient(authorized, false, BaseAddress = baseAddress)
    let delphiSelect = lazy DelphiSelect.Client(http endpoints.DelphiSelect, json)
    let kyc = lazy Kyc.Client(http endpoints.Kyc, json)
    let ned = lazy Ned.Client(http endpoints.Ned, json)
    let workReport = lazy WorkReport.Client(http endpoints.WorkReport, json)
    let idp = lazy Idp.Client(http endpoints.Idp, json)
    let powerCurve = lazy PowerCurve.Client(http endpoints.PowerCurve, json)

    /// A client over a caller-owned handler, such as one shared SocketsHttpHandler.
    new(credentials, endpoints, handler) = ExperianClient(credentials, endpoints, handler, fun () -> DateTimeOffset.UtcNow)

    member _.Endpoints = endpoints
    member _.Tokens = tokens
    member _.DelphiSelect = delphiSelect.Value
    member _.Kyc = kyc.Value
    member _.Ned = ned.Value
    member _.WorkReport = workReport.Value
    member _.Idp = idp.Value
    member _.PowerCurve = powerCurve.Value

    /// A handler for the Experian hosts. Create it once per application.
    static member CreateHandler() : HttpMessageHandler =
        new SocketsHttpHandler(UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes 5.)

    /// A client with its own handler.
    static member Create(credentials, endpoints) = ExperianClient(credentials, endpoints, ExperianClient.CreateHandler())

module ExperianClient =

    /// A client over a caller-owned handler.
    let create credentials endpoints (handler: HttpMessageHandler) = ExperianClient(credentials, endpoints, handler)

/// How the Api functions run a call and report its failure.
module internal Call =

    [<TailCall>]
    let rec private failureOf (e: exn) =
        match e with
        | :? AggregateException as e when e.InnerExceptions.Count = 1 -> failureOf e.InnerException
        | :? Swagger.OpenApiException as e -> ApiError(e.StatusCode, e.ResponseBody)
        | TokenRefusedException(status, body) -> TokenRefused(status, body)
        | e -> TransportError e

    /// Runs a call; exceptions become Error. Cancellation of the token propagates.
    let guarded (ct: CancellationToken) (run: unit -> Task<Result<'T, ExperianFailure>>) =
        task {
            try
                return! run ()
            with error when not (ct.IsCancellationRequested && (error :? OperationCanceledException)) ->
                return Error(failureOf error)
        }

    /// Runs a generated call.
    let attempt (ct: CancellationToken) (call: Async<'T>) =
        guarded ct (fun () -> task {
            let! value = Async.StartAsTask(call, cancellationToken = ct)
            return Ok value })

    /// A path parameter as one escaped segment. The generated clients insert path values as they are, so "../" or "?"
    /// would reach another endpoint with the bearer token; empty, "." and ".." are refused.
    let segment (name: string) (value: string) =
        match value with
        | null
        | ""
        | "."
        | ".." -> Error(InvalidRequest $"{name} must be a non-empty identifier")
        | _ -> Ok(Uri.EscapeDataString value)

    /// Runs send with the escaped path parameter, or returns InvalidRequest without sending.
    let withSegment name value (send: string -> Task<Result<'T, ExperianFailure>>) =
        match segment name value with
        | Ok escaped -> send escaped
        | Error failure -> Task.FromResult(Error failure)
