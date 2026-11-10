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

/// How a region's token endpoint is asked.
type SignIn =
    /// Experian's developer portal: the portal user and the app's client id and secret, in JSON.
    | PortalPassword
    /// Serasa Experian in Brazil: the app's client id and secret alone, as Basic authentication.
    | SerasaClient

/// The developer-portal user and the registered app's client id and secret; the token endpoint needs all four.
/// Printing one (%A, ToString) shows the username and client id only.
[<StructuredFormatDisplay("{Display}")>]
type Credentials =
    {
        Username: string
        Password: string
        ClientId: string
        ClientSecret: string
    }

    member this.Display =
        $"{{ Username = {this.Username}; ClientId = {this.ClientId}; Password and ClientSecret hidden }}"

    override this.ToString() = this.Display

/// The token endpoint and each product's base address. Product addresses end with '/': generated paths resolve against them.
type Endpoints =
    {
        Token: Uri
        DelphiSelect: Uri
        Kyc: Uri
        Ned: Uri
        WorkReport: Uri
        Idp: Uri
        PowerCurve: Uri
        SignIn: SignIn
        ClientGateway: Uri
        BankAccountVerify: Uri
        InternationalPayments: Uri
        Kyb: Uri
        AgeAndDateOfBirth: Uri
        SsnVerification: Uri
        Verify: Uri
        VerifyCpd: Uri
        InstantCredit: Uri
        CreditProfile: Uri
        WebData: Uri
        OpenBankingScore: Uri
        IncomeVerification: Uri
        TransactionCategorization: Uri
        AffordabilityCheck: Uri
    }

/// Credentials for a sign-in that takes only the app's client id and secret: Serasa Experian's.
module Credentials =
    let ofClient (clientId: string) (clientSecret: string) =
        {
            Username = ""
            Password = ""
            ClientId = clientId
            ClientSecret = clientSecret
        }

module Endpoints =

    /// The bundled specs' base paths on one host.
    let onHost (host: string) =
        let at (path: string) = Uri($"https://{host}{path}")

        {
            Token = at "/oauth2/v1/token"
            DelphiSelect = at "/risk/consumer/v2/"
            Kyc = at "/da/ccis-devportalapis/"
            Ned = at "/"
            WorkReport = at "/"
            Idp = at "/"
            PowerCurve = at "/decisioning/experianone/customeracquisition/v1/"
            SignIn = PortalPassword
            ClientGateway = at "/v1/"
            BankAccountVerify = at "/da/ccis-devportalapis/"
            InternationalPayments = at "/da/ccis-devportalapis/"
            Kyb = at "/"
            AgeAndDateOfBirth = at "/da/ccis-devportalapis/"
            SsnVerification = at "/consumerservices/ecbsv/v1/"
            Verify = at "/consumerservices/verification/v2/"
            VerifyCpd = at "/consumerservices/evcpd/v1/"
            InstantCredit = at "/consumerservices/credit-profile/"
            CreditProfile = at "/consumerservices/credit-profile/"
            WebData = at "/creditrisk/webdata/gdp/v1/"
            OpenBankingScore = at "/open-banking/score/v1/"
            IncomeVerification = at "/open-banking/income-verification/v1/"
            TransactionCategorization = at "/open-banking/categorization/v1/"
            AffordabilityCheck = at "/open-banking/affordability-check/v1/"
        }

    /// The scheme, host and port of every address in a set: the only places its token goes.
    let internal origins (endpoints: Endpoints) =
        Reflection.FSharpValue.GetRecordFields endpoints
        |> Array.choose (function
            | :? Uri as uri -> Some(uri.GetLeftPart UriPartial.Authority)
            | _ -> None)
        |> Set.ofArray

    /// Serasa Experian's own sign-in, in Brazil.
    let private onSerasaHost (host: string) =
        { onHost host with
            Token = Uri($"https://{host}/security/iam/v1/client-identities/login")
            SignIn = SerasaClient
        }

    let sandbox = onHost "sandbox-uk-api.experian.com"

    /// The KYC spec only names its sandbox path; set Kyc if Experian gives a different live one.
    let production = onHost "uk-api.experian.com"

    /// The sandbox answers Experian Verify on v1 only, with one fixed report.
    let sandboxUs =
        { onHost "sandbox-us-api.experian.com" with
            Verify = Uri "https://sandbox-us-api.experian.com/consumerservices/verification/v1/"
        }

    /// Experian Verify on a page the client hosts answers on a host of its own.
    let productionUs =
        { onHost "us-api.experian.com" with
            VerifyCpd = Uri "https://us-ev-api.experian.com/consumerservices/evcpd/v1/"
        }

    /// Web Data Insights, for the Netherlands and Spain.
    let sandboxEu = onHost "sandbox-eu-api.experian.com"
    let productionEu = onHost "eu-api.experian.com"

    /// Serasa Experian's sandbox, as the Brazilian specs name it.
    let sandboxBrazil = onSerasaHost "sandbox-api.serasaexperian.com.br"
    let productionBrazil = onSerasaHost "api.serasaexperian.com.br"

/// A success answer the client could not read. Experian did the work, so sending the call again repeats it: a Delphi search writes
/// another footprint. Printing one (%A, ToString) leaves the body out.
[<StructuredFormatDisplay("{Display}")>]
type UnreadableAnswer =
    {
        HttpStatus: int
        /// The answer as sent. It can hold a whole credit file or a signing secret: keep it out of logs.
        Body: string
        /// Where the answer could not be read, or the failure's type. It quotes nothing from the answer except plain field names.
        Reason: string
    }

    member this.Display =
        $"{{ HttpStatus = {this.HttpStatus}; Reason = {this.Reason}; Body hidden }}"

    override this.ToString() = this.Display

type ExperianFailure =
    /// The request was not sent: an argument cannot be valid.
    | InvalidRequest of reason: string
    /// Experian answered with a non-success status; body is the response text.
    | ApiError of httpStatus: int * body: string
    /// The token endpoint refused the credentials, or answered without an access_token.
    | TokenRefused of httpStatus: int * body: string
    /// There was no complete answer, so whether Experian did the work is unknown.
    | TransportError of exn
    /// Experian answered with success, and the client could not read the answer.
    | InvalidResponse of UnreadableAnswer

module ExperianFailure =

    /// Whether a token endpoint's status refuses the credentials, as opposed to a busy or failing server.
    let refusesCredentials (httpStatus: int) =
        httpStatus >= 400 && httpStatus < 500 && httpStatus <> 408 && httpStatus <> 429

    /// Whether this is a sign-in Experian refused. Sending the same credentials again counts as another failed sign-in.
    let isCredentialRefusal failure =
        match failure with
        | TokenRefused(status, _) -> refusesCredentials status
        | _ -> false

    /// The error codes in an error body: the short, code-like values of its ErrorCode, error_code, code, error and errorType
    /// fields in any letter case, five at most. A message can quote what was sent, a name or an address, so none is read.
    let codesOf (body: string) =
        let named = set [ "errorcode"; "error_code"; "code"; "error"; "errortype" ]

        let isCode (text: string) =
            text.Length > 0
            && text.Length <= 60
            && text
               |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_' || c = '-' || c = '.')

        let rec codes (depth: int) (element: JsonElement) : string seq =
            seq {
                if depth < 8 then
                    match element.ValueKind with
                    | JsonValueKind.Object ->
                        for property in element.EnumerateObject() do
                            match property.Value.ValueKind with
                            | JsonValueKind.String when named.Contains(property.Name.ToLowerInvariant()) ->
                                let text = property.Value.GetString()

                                if isCode text then
                                    yield text
                            | JsonValueKind.Number when named.Contains(property.Name.ToLowerInvariant()) ->
                                yield property.Value.GetRawText()
                            | _ -> yield! codes (depth + 1) property.Value
                    | JsonValueKind.Array ->
                        for item in element.EnumerateArray() do
                            yield! codes (depth + 1) item
                    | _ -> ()
            }

        try
            use document = JsonDocument.Parse body
            codes 0 document.RootElement |> Seq.distinct |> Seq.truncate 5 |> List.ofSeq
        with :? JsonException ->
            []

    let private withCodes (body: string) =
        match codesOf body with
        | [] -> ""
        | found -> " (" + String.Join(", ", found) + ")"

    /// One line saying what went wrong, for logs and exception messages. Of an error body it quotes the error codes only
    /// (codesOf); the whole body is on the failure.
    let describe failure =
        match failure with
        | InvalidRequest reason -> $"Not sent: {reason}"
        | ApiError(status, body) -> $"Experian answered HTTP {status}{withCodes body}"
        | TokenRefused(status, body) -> $"Experian refused the token request (HTTP {status}){withCodes body}"
        | TransportError e -> $"Request failed: {e.Message}"
        | InvalidResponse answer ->
            $"Experian answered HTTP {answer.HttpStatus}, and the answer could not be read: {answer.Reason}"

/// Raised inside a call when the token endpoint refuses; calls return it as TokenRefused.
exception TokenRefusedException of httpStatus: int * body: string

/// Raised inside a call when a request would carry the token off the client's endpoints; calls return it as InvalidRequest.
exception OtherHostException of host: string

/// A bearer token and the time after which it is not used. Printing one shows the expiry only.
[<StructuredFormatDisplay("{Display}")>]
type AccessToken =
    {
        Value: string
        ExpiresAt: DateTimeOffset
    }

    member this.Display = $"{{ Value hidden; ExpiresAt = {this.ExpiresAt:o} }}"
    override this.ToString() = this.Display

module internal Wire =

    /// Experian's gateway answers 415 to "application/json; charset=utf-8"; JSON is UTF-8 by definition, so the charset is dropped.
    let plainJson (content: HttpContent) =
        if
            not (isNull content || isNull content.Headers.ContentType)
            && content.Headers.ContentType.MediaType = "application/json"
        then
            content.Headers.ContentType.CharSet <- null

    /// An HttpClient over handler. Without a timeout it keeps HttpClient's default of 100 seconds.
    let httpClient (handler: HttpMessageHandler) (baseAddress: Uri) (timeout: TimeSpan voption) =
        let client = new HttpClient(handler, false, BaseAddress = baseAddress)
        timeout |> ValueOption.iter (fun limit -> client.Timeout <- limit)
        client

    /// Refuses at creation a timeout HttpClient would refuse at the first call.
    let checkTimeout (timeout: TimeSpan) =
        let longest = TimeSpan.FromMilliseconds(float Int32.MaxValue)

        if
            timeout <> Timeout.InfiniteTimeSpan
            && (timeout <= TimeSpan.Zero || timeout > longest)
        then
            raise (
                ArgumentOutOfRangeException(
                    nameof timeout,
                    timeout,
                    "A timeout must be positive and at most Int32.MaxValue milliseconds, or infinite."
                )
            )

/// The success answer a call received, kept so that the call can report it if it can't read it. Each Api call has its own slot;
/// generated clients called directly have none.
module internal LastAnswer =

    [<AllowNullLiteral>]
    type Slot() =
        member val Answer: struct (int * byte[]) voption = ValueNone with get, set

    let private current = AsyncLocal<Slot>()

    /// A new slot for the call running on this execution flow.
    let start () =
        let slot = Slot()
        current.Value <- slot
        slot

    /// Keeps a success answer's whole body. The content stays buffered, so the generated client still reads it.
    let keep (response: HttpResponseMessage) (ct: CancellationToken) =
        task {
            match current.Value with
            | null -> ()
            | slot when response.IsSuccessStatusCode && not (isNull response.Content) ->
                let! body = response.Content.ReadAsByteArrayAsync ct
                slot.Answer <- ValueSome(struct (int response.StatusCode, body))
            | _ -> ()
        }

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

module internal QuotedNumber =

    /// A quoted number's value. A JsonException gives the failure a JSON path; the parser's own message would quote the text.
    let read (parsed: bool * 'T) =
        match parsed with
        | true, value -> value
        | _ -> raise (JsonException())

/// Number converters that also read quoted numbers. Registered converters reach option elements too, which NumberHandling does not.
type internal LenientInt32Converter() =
    inherit Serialization.JsonConverter<int>()

    override _.Read(reader, _, _) =
        if reader.TokenType = JsonTokenType.String then
            QuotedNumber.read (Int32.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture))
        else
            reader.GetInt32()

    override _.Write(writer, value, _) = writer.WriteNumberValue value

type internal LenientInt64Converter() =
    inherit Serialization.JsonConverter<int64>()

    override _.Read(reader, _, _) =
        if reader.TokenType = JsonTokenType.String then
            QuotedNumber.read (Int64.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture))
        else
            reader.GetInt64()

    override _.Write(writer, value, _) = writer.WriteNumberValue value

type internal LenientDoubleConverter() =
    inherit Serialization.JsonConverter<double>()

    override _.Read(reader, _, _) =
        if reader.TokenType = JsonTokenType.String then
            QuotedNumber.read (Double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture))
        else
            reader.GetDouble()

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
        t.IsGenericType
        && t.GetGenericTypeDefinition() = typedefof<option<_>>
        && t.GetGenericArguments()[0] <> typeof<string>

    override _.CreateConverter(t, _) =
        Activator.CreateInstance(typedefof<BlankAsNoneConverter<_>>.MakeGenericType(t.GetGenericArguments()))
        :?> Serialization.JsonConverter

module internal Reading =

    /// Unset optional fields are left out of request bodies rather than sent as null. Responses are read leniently:
    /// strings may arrive as numbers, numbers as strings, optional dates and numbers as "", and names in another case
    /// (NED sends employerid for employerId); requests are written as the specs type and name them.
    let options =
        let options =
            JsonSerializerOptions(
                DefaultIgnoreCondition = Serialization.JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true
            )

        options.Converters.Add(BlankAsNoneFactory())
        options.Converters.Add(LenientStringConverter())
        options.Converters.Add(LenientInt32Converter())
        options.Converters.Add(LenientInt64Converter())
        options.Converters.Add(LenientDoubleConverter())
        options

module internal EncodedJson =

    /// The JSON inside a body that is one JSON string holding an object or array, as the KYC sandbox answers.
    let decoded (text: string) =
        if text.Length > 1 && text.TrimStart().StartsWith '"' then
            try
                match JsonSerializer.Deserialize<string> text with
                | null -> ValueNone
                | inner ->
                    let trimmed = inner.TrimStart()

                    if trimmed.StartsWith '{' || trimmed.StartsWith '[' then
                        use _ = JsonDocument.Parse inner
                        ValueSome inner
                    else
                        ValueNone
            with :? JsonException ->
                ValueNone
        else
            ValueNone

module internal Token =

    /// Tokens are renewed this long before Experian's expiry, so a request never carries one that expires in flight.
    let renewBefore = TimeSpan.FromMinutes 1.

    /// How long refused credentials are not sent again by one client. Failed sign-ins can lock the account, and Experian doesn't
    /// publish how many. A client made with new credentials starts afresh.
    let refusedPause = TimeSpan.FromHours 1.

    /// Experian documents 30-minute tokens; used when expires_in is missing or unreadable.
    let defaultLifetime = TimeSpan.FromMinutes 30.

    /// Serasa Experian's sign-in: the client id and secret alone. The answer's names are read by parse.
    let serasaRequest (credentials: Credentials) (tokenUri: Uri) =
        let request = new HttpRequestMessage(HttpMethod.Post, tokenUri)

        let pair =
            Convert.ToBase64String(Encoding.UTF8.GetBytes $"{credentials.ClientId}:{credentials.ClientSecret}")

        request.Headers.Authorization <- AuthenticationHeaderValue("Basic", pair)
        request.Content <- new StringContent("{}", Encoding.UTF8, "application/json")
        request

    let request (credentials: Credentials) (tokenUri: Uri) =
        let body =
            JsonSerializer.Serialize(
                {|
                    username = credentials.Username
                    password = credentials.Password
                    client_id = credentials.ClientId
                    client_secret = credentials.ClientSecret
                |}
            )

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
            // Serasa Experian names them in camel case. A body that is JSON and no object holds no token
            let named (snake: string) (camel: string) =
                if root.ValueKind <> JsonValueKind.Object then
                    ValueNone
                else
                    match root.TryGetProperty snake with
                    | true, value -> ValueSome value
                    | _ ->
                        match root.TryGetProperty camel with
                        | true, value -> ValueSome value
                        | _ -> ValueNone

            match named "access_token" "accessToken" with
            | ValueSome token when token.ValueKind = JsonValueKind.String && token.GetString() <> "" ->
                let lifetime =
                    match named "expires_in" "expiresIn" with
                    | ValueSome expires ->
                        match seconds expires with
                        | ValueSome s when s > 0. -> TimeSpan.FromSeconds s
                        | _ -> defaultLifetime
                    | ValueNone -> defaultLifetime

                ValueSome
                    {
                        Value = token.GetString()
                        ExpiresAt = now + lifetime
                    }
            | _ -> ValueNone
        with :? JsonException ->
            ValueNone

/// Fetches the bearer token and keeps it until a minute before it expires; concurrent callers share one fetch. Credentials Experian
/// refused are not sent again for an hour: until then a call fails with TokenRefused, without a request.
[<Sealed>]
type TokenSource
    internal
    (credentials: Credentials, tokenUri: Uri, invoker: HttpMessageInvoker, now: unit -> DateTimeOffset, ?signIn: SignIn)
    =
    let gate = new SemaphoreSlim(1, 1)

    /// A reference cell, not a voption: it is read outside the gate, and a struct read could be torn.
    let mutable current: AccessToken option = None

    /// The status that refused the credentials and when they may be sent again. Read and written inside the gate.
    let mutable refused: (int * DateTimeOffset) option = None

    let usable () =
        match current with
        | Some token when token.ExpiresAt - Token.renewBefore > now () -> ValueSome token
        | _ -> ValueNone

    let fetch (ct: CancellationToken) =
        task {
            use request =
                match defaultArg signIn PortalPassword with
                | PortalPassword -> Token.request credentials tokenUri
                | SerasaClient -> Token.serasaRequest credentials tokenUri

            use! response = invoker.SendAsync(request, ct)
            let! text = response.Content.ReadAsStringAsync ct
            let status = int response.StatusCode

            return
                if not response.IsSuccessStatusCode then
                    if ExperianFailure.refusesCredentials status then
                        refused <- Some(status, now () + Token.refusedPause)

                    raise (TokenRefusedException(status, text))
                else
                    match Token.parse (now ()) text with
                    | ValueSome token ->
                        refused <- None
                        token
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
                        match refused with
                        | Some(status, until) when now () < until ->
                            return
                                raise (
                                    TokenRefusedException(
                                        status,
                                        "Not sent: Experian refused these credentials within the last hour."
                                    )
                                )
                        | _ ->
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
/// with a fresh one. A request already carrying Authorization keeps it, with no sign-in or retry. A request for a host outside origins
/// isn't sent. A success body that is one JSON string holding an object or array is replaced by that JSON.
type internal BearerHandler(tokens: TokenSource, origins: Set<string>, inner: HttpMessageHandler) =
    inherit DelegatingHandler(inner)

    member private _.SendInner(request, ct) = base.SendAsync(request, ct)

    member private this.SendAuthorized(request: HttpRequestMessage, ct) =
        let refuseOtherHost () =
            if not (origins.Contains(request.RequestUri.GetLeftPart UriPartial.Authority)) then
                raise (OtherHostException request.RequestUri.Host)

        task {
            refuseOtherHost ()
            Wire.plainJson request.Content

            if request.Headers.Contains "Authorization" then
                return! this.SendInner(request, ct)
            else
                let! token = tokens.GetAsync ct
                request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token.Value)
                let! response = this.SendInner(request, ct)

                if response.StatusCode <> HttpStatusCode.Unauthorized then
                    return response
                else
                    response.Dispose()
                    tokens.Invalidate token
                    // A handler that followed a redirect has moved the request.
                    refuseOtherHost ()
                    let! fresh = tokens.GetAsync ct
                    request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", fresh.Value)
                    return! this.SendInner(request, ct)
        }

    override this.SendAsync(request, ct) =
        task {
            let! response = this.SendAuthorized(request, ct)
            do! LastAnswer.keep response ct

            let isJson =
                not (isNull response.Content)
                && (isNull response.Content.Headers.ContentType
                    || response.Content.Headers.ContentType.MediaType = "application/json")

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

/// A connection to one Experian region: one token cache and the generated client of each product over one shared handler. Each
/// product's client is made when first used, so an app enabled for one product needs nothing set up for the others.
/// Keep it for the application's lifetime; the handler stays the caller's (it is never disposed here). A caller's handler must not follow
/// redirects: it would send the body, and the sign-in's password, to wherever a redirect points.
[<Sealed>]
type ExperianClient
    internal
    (
        credentials: Credentials,
        endpoints: Endpoints,
        handler: HttpMessageHandler,
        now: unit -> DateTimeOffset,
        timeout: TimeSpan voption
    ) =
    let tokens =
        TokenSource(credentials, endpoints.Token, new HttpMessageInvoker(handler, false), now, endpoints.SignIn)

    let authorized = new BearerHandler(tokens, Endpoints.origins endpoints, handler)

    let json = Reading.options

    let http (baseAddress: Uri) =
        Wire.httpClient authorized baseAddress timeout

    let delphiSelect = lazy DelphiSelect.Client(http endpoints.DelphiSelect, json)
    let kyc = lazy Kyc.Client(http endpoints.Kyc, json)
    let ned = lazy Ned.Client(http endpoints.Ned, json)
    let workReport = lazy WorkReport.Client(http endpoints.WorkReport, json)
    let idp = lazy Idp.Client(http endpoints.Idp, json)
    let powerCurve = lazy PowerCurve.Client(http endpoints.PowerCurve, json)
    let clientGateway = lazy ClientGateway.Client(http endpoints.ClientGateway, json)

    let bankAccountVerify =
        lazy BankAccountVerify.Client(http endpoints.BankAccountVerify, json)

    let internationalPayments =
        lazy InternationalPayments.Client(http endpoints.InternationalPayments, json)

    let kyb = lazy Kyb.Client(http endpoints.Kyb, json)

    let ageAndDateOfBirth =
        lazy AgeAndDateOfBirth.Client(http endpoints.AgeAndDateOfBirth, json)

    let ssnVerification =
        lazy SsnVerification.Client(http endpoints.SsnVerification, json)

    let verifyCore = lazy VerifyCore.Client(http endpoints.Verify, json)
    let verifyPlus = lazy VerifyPlus.Client(http endpoints.Verify, json)
    let verifyPremium = lazy VerifyPremium.Client(http endpoints.Verify, json)

    let verifyPremiumPrequal =
        lazy VerifyPremiumPrequal.Client(http endpoints.Verify, json)

    let verifyEmployment = lazy VerifyEmployment.Client(http endpoints.Verify, json)
    let verifyGovernment = lazy VerifyGovernment.Client(http endpoints.Verify, json)
    let reverifyVoe = lazy ReverifyVoe.Client(http endpoints.Verify, json)
    let reverifyVoie = lazy ReverifyVoie.Client(http endpoints.Verify, json)
    let verifyCpdCore = lazy VerifyCpdCore.Client(http endpoints.VerifyCpd, json)
    let verifyCpdPlus = lazy VerifyCpdPlus.Client(http endpoints.VerifyCpd, json)
    let verifyCpdPremium = lazy VerifyCpdPremium.Client(http endpoints.VerifyCpd, json)
    let reverifyCpdVoe = lazy ReverifyCpdVoe.Client(http endpoints.VerifyCpd, json)
    let reverifyCpdVoie = lazy ReverifyCpdVoie.Client(http endpoints.VerifyCpd, json)
    let instantCredit = lazy InstantCredit.Client(http endpoints.InstantCredit, json)
    let creditProfile = lazy CreditProfile.Client(http endpoints.CreditProfile, json)
    let webData = lazy WebData.Client(http endpoints.WebData, json)

    let openBankingScore =
        lazy OpenBankingScore.Client(http endpoints.OpenBankingScore, json)

    let incomeVerification =
        lazy IncomeVerifications.Client(http endpoints.IncomeVerification, json)

    let transactionCategorization =
        lazy TransactionCategorization.Client(http endpoints.TransactionCategorization, json)

    let affordabilityCheck =
        lazy AffordabilityChecks.Client(http endpoints.AffordabilityCheck, json)

    internal new(credentials, endpoints, handler, now: unit -> DateTimeOffset) =
        ExperianClient(credentials, endpoints, handler, now, ValueNone)

    /// A client over a caller-owned handler, such as one shared SocketsHttpHandler.
    new(credentials, endpoints, handler) =
        ExperianClient(credentials, endpoints, handler, (fun () -> DateTimeOffset.UtcNow), ValueNone)

    member _.Endpoints = endpoints
    member _.Tokens = tokens
    member _.DelphiSelect = delphiSelect.Value
    member _.Kyc = kyc.Value
    member _.Ned = ned.Value
    member _.WorkReport = workReport.Value
    member _.Idp = idp.Value
    member _.PowerCurve = powerCurve.Value
    /// The generated client, with no Api functions. Its authorization argument, when given, is sent instead of the portal token.
    member _.ClientGateway = clientGateway.Value
    member _.BankAccountVerify = bankAccountVerify.Value
    member _.InternationalPayments = internationalPayments.Value
    member _.Kyb = kyb.Value
    member _.AgeAndDateOfBirth = ageAndDateOfBirth.Value
    member _.SsnVerification = ssnVerification.Value
    member _.VerifyCore = verifyCore.Value
    member _.VerifyPlus = verifyPlus.Value
    member _.VerifyPremium = verifyPremium.Value
    member _.VerifyPremiumPrequal = verifyPremiumPrequal.Value
    member _.VerifyEmployment = verifyEmployment.Value
    member _.VerifyGovernment = verifyGovernment.Value
    member _.ReverifyVoe = reverifyVoe.Value
    member _.ReverifyVoie = reverifyVoie.Value
    member _.VerifyCpdCore = verifyCpdCore.Value
    member _.VerifyCpdPlus = verifyCpdPlus.Value
    member _.VerifyCpdPremium = verifyCpdPremium.Value
    member _.ReverifyCpdVoe = reverifyCpdVoe.Value
    member _.ReverifyCpdVoie = reverifyCpdVoie.Value
    member _.InstantCredit = instantCredit.Value
    member _.CreditProfile = creditProfile.Value
    member _.WebData = webData.Value
    member _.OpenBankingScore = openBankingScore.Value
    member _.IncomeVerification = incomeVerification.Value
    member _.TransactionCategorization = transactionCategorization.Value
    member _.AffordabilityCheck = affordabilityCheck.Value

    /// A handler for the Experian hosts. Create it once per application. It doesn't follow redirects, so a token or personal data can't
    /// follow one to another host: a 3xx answer is an ApiError.
    static member CreateHandler() : HttpMessageHandler =
        new SocketsHttpHandler(
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes 5.,
            AllowAutoRedirect = false
        )

    /// A client with its own handler and HttpClient's default timeout of 100 seconds.
    static member Create(credentials: Credentials, endpoints: Endpoints) =
        ExperianClient(credentials, endpoints, ExperianClient.CreateHandler())

    /// A client with its own handler. timeout limits one Api call, with any sign-in and 401 resend inside it; past it the call is a
    /// TransportError, and Experian may still have done the work.
    static member Create(credentials: Credentials, endpoints: Endpoints, timeout: TimeSpan) =
        Wire.checkTimeout timeout

        ExperianClient(
            credentials,
            endpoints,
            ExperianClient.CreateHandler(),
            (fun () -> DateTimeOffset.UtcNow),
            ValueSome timeout
        )

module ExperianClient =

    /// A client over a caller-owned handler.
    let create credentials endpoints (handler: HttpMessageHandler) =
        ExperianClient(credentials, endpoints, handler)

/// Adds the token to every request and sends JSON without a charset.
type internal AuthTokenHandler(authToken: string, inner: HttpMessageHandler) =
    inherit DelegatingHandler(inner)

    member private _.SendInner(request, ct) = base.SendAsync(request, ct)

    override this.SendAsync(request, ct) =
        request.Headers.TryAddWithoutValidation("Auth-Token", authToken) |> ignore
        Wire.plainJson request.Content

        task {
            let! response = this.SendInner(request, ct)
            do! LastAnswer.keep response ct
            return response
        }

/// A connection to Experian's Address, Email and Phone Validation. They don't use the developer portal: they answer on one host, for any
/// region, to a token from Experian's Self Service Portal that opens whichever of them are licensed. Each product's client is made when
/// first used. Keep it for the application's lifetime; the handler stays the caller's (it is never disposed here). A caller's handler must
/// not follow redirects: .NET would send this token's header to wherever a redirect points.
[<Sealed>]
type ApertureClient internal (authToken: string, handler: HttpMessageHandler, timeout: TimeSpan voption) =
    let authorized = new AuthTokenHandler(authToken, handler)

    let http () =
        Wire.httpClient authorized (Uri "https://api.experianaperture.io/") timeout

    let address = lazy AddressValidation.Client(http (), Reading.options)
    let email = lazy EmailValidation.Client(http (), Reading.options)
    let phone = lazy PhoneValidation.Client(http (), Reading.options)

    new(authToken, handler) = ApertureClient(authToken, handler, ValueNone)

    member _.Address = address.Value
    member _.Email = email.Value
    member _.Phone = phone.Value

    /// A client with its own handler and HttpClient's default timeout of 100 seconds.
    static member Create(authToken: string) =
        ApertureClient(authToken, ExperianClient.CreateHandler())

    /// A client with its own handler. timeout limits one Api call; past it the call is a TransportError.
    static member Create(authToken: string, timeout: TimeSpan) =
        Wire.checkTimeout timeout
        ApertureClient(authToken, ExperianClient.CreateHandler(), ValueSome timeout)

/// How the Api functions run a call and report its failure.
module internal Call =

    /// A success answer of JSON null, to a call that expects a value.
    exception private NullAnswerException

    /// A success answer that read, and holds none of what the call answers with. The reason quotes nothing from it.
    exception NoDataException of reason: string

    /// Where an answer could not be read. Exception messages can quote the answer, so none is used. A bracketed path segment is a
    /// key that isn't a plain name, which could be any text, so the path stops there.
    let private reasonOf (e: exn) =
        match e with
        | NullAnswerException -> "The answer is null"
        | NoDataException reason -> reason
        | :? JsonException as e ->
            let path =
                match e.Path with
                | null -> "?"
                | path ->
                    match path.IndexOf("['", StringComparison.Ordinal) with
                    | -1 -> path
                    | at -> path.Substring(0, at) + "[?]"

            $"Invalid at {path}, line {e.LineNumber}, byte {e.BytePositionInLine}"
        | _ -> e.GetType().Name

    [<TailCall>]
    let rec private failureOf (slot: LastAnswer.Slot) (e: exn) =
        match e with
        | :? AggregateException as e when e.InnerExceptions.Count = 1 -> failureOf slot e.InnerException
        | :? Swagger.OpenApiException as e -> ApiError(e.StatusCode, e.ResponseBody)
        | TokenRefusedException(status, body) -> TokenRefused(status, body)
        | OtherHostException host ->
            InvalidRequest $"{host} isn't one of the client's endpoints, so the token isn't sent there"
        | :? HttpRequestException
        | :? IO.IOException
        | :? OperationCanceledException -> TransportError e
        | e ->
            match slot.Answer with
            | ValueSome(struct (status, body)) ->
                InvalidResponse
                    {
                        HttpStatus = status
                        Body = Encoding.UTF8.GetString body
                        Reason = reasonOf e
                    }
            | ValueNone -> TransportError e

    /// Runs a call of one request; exceptions become Error. Cancellation of the token propagates.
    let guarded (ct: CancellationToken) (run: unit -> Task<Result<'T, ExperianFailure>>) =
        task {
            let slot = LastAnswer.start ()

            try
                let! result = run ()

                return
                    match result with
                    | Ok value when isNull (box value) && typeof<'T> <> typeof<unit> ->
                        Error(failureOf slot NullAnswerException)
                    | _ -> result
            with error when not (ct.IsCancellationRequested && (error :? OperationCanceledException)) ->
                return Error(failureOf slot error)
        }

    /// Runs a generated call. A generated client sends its request as soon as it is called, so the call is made inside the guard.
    let attempt (ct: CancellationToken) (call: unit -> Async<'T>) =
        guarded ct (fun () ->
            task {
                let! value = Async.StartAsTask(call (), cancellationToken = ct)
                return Ok value
            })

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
