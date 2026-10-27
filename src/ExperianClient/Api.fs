/// Task-based calls per product. Each returns Ok with the generated response type, or Error ExperianFailure.
/// String path parameters are escaped as one segment, so they cannot reach another endpoint.
module Experian.Api

open System
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Experian.Call

let [<Literal>] private Json = "application/json"

/// Delphi Select: consumer applications, second-phase Identity Authenticate (AuthPlus) and Detect, and stand-alone products.
module DelphiSelect =

    /// What a retrieved application includes; ValueNone leaves Experian's default.
    type ApplicationView =
        { ClientAccountNumber: string voption
          FullFblRequired: bool voption
          DisplayConsumer: bool voption
          DisplayAuthenticate: bool voption
          DisplayDetect: bool voption
          DisplayAddress: bool voption
          DisplayCaseHistory: bool voption }

        static member Default =
            { ClientAccountNumber = ValueNone
              FullFblRequired = ValueNone
              DisplayConsumer = ValueNone
              DisplayAuthenticate = ValueNone
              DisplayDetect = ValueNone
              DisplayAddress = ValueNone
              DisplayCaseHistory = ValueNone }

    [<Literal>]
    let private reference = "experianReference"

    /// A new application: bureau search plus the products requested in Options (AuthenticatePlusRequired for AuthPlus).
    let applicationAsync (client: ExperianClient) (input: DelphiSelect.Input) (ct: CancellationToken) =
        attempt ct (client.DelphiSelect.Application(Json, Json, input, ct))

    /// The bundled spec declares the view flags as strings: the generated client would send booleans as True/False.
    let private flag (value: bool voption) =
        value |> ValueOption.map (fun b -> if b then "true" else "false") |> ValueOption.toOption

    /// A previously submitted application by its Experian reference.
    let getApplicationAsync (client: ExperianClient) (experianReference: string) (view: ApplicationView) (ct: CancellationToken) =
        withSegment reference experianReference (fun escaped ->
            attempt ct (
                client.DelphiSelect.GetApplication(
                    Json, Json, escaped,
                    ValueOption.toOption view.ClientAccountNumber,
                    flag view.FullFblRequired,
                    flag view.DisplayConsumer,
                    flag view.DisplayAuthenticate,
                    flag view.DisplayDetect,
                    flag view.DisplayAddress,
                    flag view.DisplayCaseHistory,
                    ct)))

    /// Re-targets the bureau for an application; this always writes a new search footprint.
    let reprocessAsync (client: ExperianClient) (experianReference: string) (input: DelphiSelect.Input) (ct: CancellationToken) =
        withSegment reference experianReference (fun escaped ->
            attempt ct (client.DelphiSelect.ReProcessPut(Json, Json, escaped, input, ct)))

    /// Converts a quotation (soft footprint) into a full application (hard footprint).
    let quoteToFullAsync (client: ExperianClient) (experianReference: string) (input: DelphiSelect.InputQuoteToFull) (ct: CancellationToken) =
        withSegment reference experianReference (fun escaped ->
            attempt ct (client.DelphiSelect.QuoteToFullPut(Json, Json, escaped, input, ct)))

    /// Writes the search type into the input's Application (creating the Submission and Application if absent) and
    /// returns the same input.
    let setSearchType (searchType: SearchType) (input: DelphiSelect.Input) =
        if isNull input.Submission then
            input.Submission <- DelphiSelect.Submission()
        let submission = input.Submission
        if isNull submission.Application then
            submission.Application <- DelphiSelect.Application()
        submission.Application.ApplicationType <- Some(SearchType.code searchType)
        input

    /// Converts a quotation into an application for the product; only a credit product (hard footprint) is accepted.
    let convertQuoteAsync (client: ExperianClient) (experianReference: string) (product: CreditProduct) (clientAccountNumber: string voption) (ct: CancellationToken) =
        let input =
            DelphiSelect.InputQuoteToFull(
                Submission =
                    DelphiSelect.SubmissionQuoteToFull(
                        ClientData = DelphiSelect.SubmissionSimplifiedClientData(ClientAccountNumber = ValueOption.toOption clientAccountNumber),
                        Application = DelphiSelect.SubmissionSimplifiedApplication(ApplicationType = Some(SearchType.code (CreditApplication product)))))
        quoteToFullAsync client experianReference input ct

    /// Second phase Identity Authenticate (AuthPlus) for an earlier application, without re-entering the applicants.
    let authenticateAsync (client: ExperianClient) (experianReference: string) (input: DelphiSelect.InputSimplified) (ct: CancellationToken) =
        withSegment reference experianReference (fun escaped ->
            attempt ct (client.DelphiSelect.SecondPhaseAuthenticatePut(Json, Json, escaped, input, ct)))

    /// Second phase Detect for an earlier application.
    let detectAsync (client: ExperianClient) (experianReference: string) (input: DelphiSelect.InputSecondPhaseDetect) (ct: CancellationToken) =
        withSegment reference experianReference (fun escaped ->
            attempt ct (client.DelphiSelect.SecondPhaseDetectPut(Json, Json, escaped, input, ct)))

    /// Stand-alone Identity Authenticate (AuthPlus), without a credit search.
    let authenticateOnlyAsync (client: ExperianClient) (input: DelphiSelect.InputAuthOnly) (ct: CancellationToken) =
        attempt ct (client.DelphiSelect.SubmissionsAuthenticatePost(Json, Json, input, ct))

    /// Stand-alone Detect.
    let detectOnlyAsync (client: ExperianClient) (input: DelphiSelect.InputDetectOnly) (ct: CancellationToken) =
        attempt ct (client.DelphiSelect.SubmissionsDetectPost(Json, Json, input, ct))

    /// Stand-alone Affordability.
    let affordabilityOnlyAsync (client: ExperianClient) (input: DelphiSelect.InputAffordabilityOnly) (ct: CancellationToken) =
        attempt ct (client.DelphiSelect.SubmissionsAffordabilityPost(Json, Json, input, ct))

    /// Stand-alone ExPIN single customer view.
    let exPinScvAsync (client: ExperianClient) (input: DelphiSelect.InputExPINOnly) (ct: CancellationToken) =
        attempt ct (client.DelphiSelect.SubmissionsExPinScvPost(Json, Json, input, ct))

/// Know Your Customer and PEP / sanctions screening.
module Kyc =

    /// A KYC check; acceptLanguage goes in the AcceptLanguage header the spec names (no hyphen).
    let checkAsync (client: ExperianClient) (request: Kyc.RequestMessage) (acceptLanguage: string voption) (ct: CancellationToken) =
        attempt ct (client.Kyc.PostV1Kyc(request, ValueOption.toOption acceptLanguage, ct))

    /// A PEP and sanctions screen.
    let pepSanctionsAsync (client: ExperianClient) (request: Kyc.RequestMessage) (acceptLanguage: string voption) (ct: CancellationToken) =
        attempt ct (client.Kyc.PostV1Pepsanctions(request, ValueOption.toOption acceptLanguage, ct))

/// National Employer Database.
module Ned =

    /// Employers whose name matches; top limits the count. Pass the same sessionId through one customer journey.
    let searchEmployersAsync (client: ExperianClient) (name: string) (top: int voption) (sessionId: string voption) (ct: CancellationToken) =
        attempt ct (client.Ned.GetCisVerificationsNedV1Employers(name, ValueOption.toOption top, ValueOption.toOption sessionId, ct))

    /// One employer by its NED identifier.
    let getEmployerAsync (client: ExperianClient) (employerId: string) (sessionId: string voption) (ct: CancellationToken) =
        withSegment "employerId" employerId (fun escaped ->
            attempt ct (client.Ned.GetCisVerificationsNedV1Employer(escaped, ValueOption.toOption sessionId, ct)))

/// Verifications Work Report.
module WorkReport =

    /// Creates the person whose employment and income are verified.
    let createPersonAsync (client: ExperianClient) (person: WorkReport.CreatePersonRequestBody) (ct: CancellationToken) =
        attempt ct (client.WorkReport.PostCisVerificationsWorkReportV1Persons(Json, Json, person, ct))

    /// Starts the connection journey for a person.
    let connectAsync (client: ExperianClient) (personId: int) (request: WorkReport.ConnectWorkReportRequestBody) (ct: CancellationToken) =
        attempt ct (client.WorkReport.PostCisVerificationsWorkReportV1PersonConnect(Json, Json, personId, request, ct))

    /// The Work Report PDF for a person, after a successful connect. Sent directly: the generated client discards binary answers.
    let requestReportAsync (client: ExperianClient) (personId: int) (request: WorkReport.ReportRequestBody) (ct: CancellationToken) : Task<Result<byte[], ExperianFailure>> =
        guarded ct (fun () -> task {
            use message = new HttpRequestMessage(HttpMethod.Post, $"cis/verifications/work-report/v1/persons/{personId}/reports")
            message.Headers.Accept.ParseAdd Json
            message.Content <- new StringContent(client.WorkReport.Serialize request, Encoding.UTF8, Json)
            use! response = client.WorkReport.HttpClient.SendAsync(message, ct)
            let! bytes = response.Content.ReadAsByteArrayAsync ct
            return
                if response.IsSuccessStatusCode then
                    Ok bytes
                else
                    Error(ApiError(int response.StatusCode, Encoding.UTF8.GetString bytes)) })

/// Verifications Intelligent Document Processing.
module Idp =

    let private application = "customerApplicationId"

    /// Uploads documents for one party of a customer application.
    let uploadDocumentsAsync (client: ExperianClient) (customerApplicationId: string) (partyId: int) (documents: Idp.DocumentDetails) (ct: CancellationToken) =
        withSegment application customerApplicationId (fun escaped ->
            attempt ct (client.Idp.PostCisVerificationsIdpV1ApiApplicationDocuments(escaped, partyId, documents, ct)))

    /// Replaces the documents of one party.
    let replaceDocumentsAsync (client: ExperianClient) (customerApplicationId: string) (partyId: int) (documents: Idp.DocumentDetails) (ct: CancellationToken) =
        withSegment application customerApplicationId (fun escaped ->
            attempt ct (client.Idp.PutCisVerificationsIdpV1ApiApplicationDocuments(escaped, partyId, documents, ct)))

    /// The documents of one party and their extraction results.
    let getDocumentsAsync (client: ExperianClient) (customerApplicationId: string) (partyId: int) (ct: CancellationToken) =
        withSegment application customerApplicationId (fun escaped ->
            attempt ct (client.Idp.GetCisVerificationsIdpV1ApiApplication(escaped, partyId, ct)))

    let createWebhookAsync (client: ExperianClient) (webhook: Idp.WebhookRequest) (ct: CancellationToken) =
        attempt ct (client.Idp.PostCisVerificationsIdpV1ApiWebhooks(webhook, ct))

    let listWebhooksAsync (client: ExperianClient) (ct: CancellationToken) =
        attempt ct (client.Idp.GetCisVerificationsIdpV1ApiWebhooks ct)

    let getWebhookAsync (client: ExperianClient) (webhookId: Guid) (ct: CancellationToken) =
        attempt ct (client.Idp.GetCisVerificationsIdpV1ApiWebhook(webhookId, ct))

    let updateWebhookAsync (client: ExperianClient) (webhookId: Guid) (webhook: Idp.WebhookRequest) (ct: CancellationToken) =
        attempt ct (client.Idp.PatchCisVerificationsIdpV1ApiWebhook(webhookId, webhook, ct))

    let deleteWebhookAsync (client: ExperianClient) (webhookId: Guid) (ct: CancellationToken) =
        attempt ct (client.Idp.DeleteCisVerificationsIdpV1ApiWebhook(webhookId, ct))

    /// The event types a webhook can subscribe to.
    let listEventTypesAsync (client: ExperianClient) (ct: CancellationToken) =
        attempt ct (client.Idp.GetCisVerificationsIdpV1ApiWebhooksEventtypes ct)

    /// Creates the webhook signing secret; deliveries then carry X-HMAC-SHA256-Signature.
    let createSecretAsync (client: ExperianClient) (ct: CancellationToken) =
        attempt ct (client.Idp.PostCisVerificationsIdpV1ApiWebhooksSecrets ct)

    let getSecretAsync (client: ExperianClient) (ct: CancellationToken) =
        attempt ct (client.Idp.GetCisVerificationsIdpV1ApiWebhooksSecrets ct)

    /// Replaces the webhook signing secret.
    let rotateSecretAsync (client: ExperianClient) (ct: CancellationToken) =
        attempt ct (client.Idp.PatchCisVerificationsIdpV1ApiWebhooksSecrets ct)

    /// Removes the secret; deliveries are then unsigned.
    let deleteSecretAsync (client: ExperianClient) (ct: CancellationToken) =
        attempt ct (client.Idp.DeleteCisVerificationsIdpV1ApiWebhooksSecrets ct)

/// PowerCurve Customer Acquisition. The handler sets Authorization, so the generated authorization argument is left null.
module PowerCurve =

    let private noAuthorization: string = null
    let private application = "powerCurveId"

    /// A new application, decisioned with the bureau data used.
    let applicationAsync (client: ExperianClient) (submission: PowerCurve.Submission) (ct: CancellationToken) =
        attempt ct (client.PowerCurve.Application(noAuthorization, Json, Json, submission, ct))

    /// Re-decisions an application with new or amended data.
    let reprocessAsync (client: ExperianClient) (powerCurveId: string) (submission: PowerCurve.Submission) (ct: CancellationToken) =
        withSegment application powerCurveId (fun escaped ->
            attempt ct (client.PowerCurve.Reprocess(noAuthorization, Json, Json, escaped, submission, ct)))

    /// Converts a quotation into a full application with a hard footprint.
    let quoteToFullAsync (client: ExperianClient) (powerCurveId: string) (submission: PowerCurve.Submission) (ct: CancellationToken) =
        withSegment application powerCurveId (fun escaped ->
            attempt ct (client.PowerCurve.QuoteToFull(noAuthorization, Json, Json, escaped, submission, ct)))

    /// The current state of an application.
    let queryAsync (client: ExperianClient) (powerCurveId: string) (ct: CancellationToken) =
        withSegment application powerCurveId (fun escaped ->
            attempt ct (client.PowerCurve.Query(noAuthorization, Json, Json, escaped, ct)))

    /// Decision details for an application.
    let decisionDetailAsync (client: ExperianClient) (powerCurveId: string) (request: PowerCurve.SubmissionDecDetails) (ct: CancellationToken) =
        withSegment application powerCurveId (fun escaped ->
            attempt ct (client.PowerCurve.DecisionDetail(noAuthorization, Json, Json, escaped, request, ct)))
