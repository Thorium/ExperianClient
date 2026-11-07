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

/// A flag for a string parameter: the generated clients would send a boolean as True/False.
let private flag (value: bool voption) =
    value |> ValueOption.map (fun b -> if b then "true" else "false") |> ValueOption.toOption

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
        attempt ct (fun () -> client.DelphiSelect.Application(Json, Json, input, ct))

    /// A previously submitted application by its Experian reference.
    let getApplicationAsync (client: ExperianClient) (experianReference: string) (view: ApplicationView) (ct: CancellationToken) =
        withSegment reference experianReference (fun escaped ->
            attempt ct (fun () ->
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
            attempt ct (fun () -> client.DelphiSelect.ReProcessPut(Json, Json, escaped, input, ct)))

    /// Converts a quotation (soft footprint) into a full application (hard footprint).
    let quoteToFullAsync (client: ExperianClient) (experianReference: string) (input: DelphiSelect.InputQuoteToFull) (ct: CancellationToken) =
        withSegment reference experianReference (fun escaped ->
            attempt ct (fun () -> client.DelphiSelect.QuoteToFullPut(Json, Json, escaped, input, ct)))

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
            attempt ct (fun () -> client.DelphiSelect.SecondPhaseAuthenticatePut(Json, Json, escaped, input, ct)))

    /// Second phase Detect for an earlier application.
    let detectAsync (client: ExperianClient) (experianReference: string) (input: DelphiSelect.InputSecondPhaseDetect) (ct: CancellationToken) =
        withSegment reference experianReference (fun escaped ->
            attempt ct (fun () -> client.DelphiSelect.SecondPhaseDetectPut(Json, Json, escaped, input, ct)))

    /// Stand-alone Identity Authenticate (AuthPlus), without a credit search.
    let authenticateOnlyAsync (client: ExperianClient) (input: DelphiSelect.InputAuthOnly) (ct: CancellationToken) =
        attempt ct (fun () -> client.DelphiSelect.SubmissionsAuthenticatePost(Json, Json, input, ct))

    /// Stand-alone Detect.
    let detectOnlyAsync (client: ExperianClient) (input: DelphiSelect.InputDetectOnly) (ct: CancellationToken) =
        attempt ct (fun () -> client.DelphiSelect.SubmissionsDetectPost(Json, Json, input, ct))

    /// Stand-alone Affordability.
    let affordabilityOnlyAsync (client: ExperianClient) (input: DelphiSelect.InputAffordabilityOnly) (ct: CancellationToken) =
        attempt ct (fun () -> client.DelphiSelect.SubmissionsAffordabilityPost(Json, Json, input, ct))

    /// Stand-alone ExPIN single customer view.
    let exPinScvAsync (client: ExperianClient) (input: DelphiSelect.InputExPINOnly) (ct: CancellationToken) =
        attempt ct (fun () -> client.DelphiSelect.SubmissionsExPinScvPost(Json, Json, input, ct))

/// Know Your Customer and PEP / sanctions screening.
module Kyc =

    /// A KYC check; acceptLanguage goes in the AcceptLanguage header the spec names (no hyphen).
    let checkAsync (client: ExperianClient) (request: Kyc.RequestMessage) (acceptLanguage: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.Kyc.PostV1Kyc(request, ValueOption.toOption acceptLanguage, ct))

    /// A PEP and sanctions screen.
    let pepSanctionsAsync (client: ExperianClient) (request: Kyc.RequestMessage) (acceptLanguage: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.Kyc.PostV1Pepsanctions(request, ValueOption.toOption acceptLanguage, ct))

/// National Employer Database.
module Ned =

    /// Employers whose name matches; top limits the count. Pass the same sessionId through one customer journey.
    let searchEmployersAsync (client: ExperianClient) (name: string) (top: int voption) (sessionId: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.Ned.GetCisVerificationsNedV1Employers(name, ValueOption.toOption top, ValueOption.toOption sessionId, ct))

    /// One employer by its NED identifier.
    let getEmployerAsync (client: ExperianClient) (employerId: string) (sessionId: string voption) (ct: CancellationToken) =
        withSegment "employerId" employerId (fun escaped ->
            attempt ct (fun () -> client.Ned.GetCisVerificationsNedV1Employer(escaped, ValueOption.toOption sessionId, ct)))

/// Verifications Work Report.
module WorkReport =

    /// Creates the person whose employment and income are verified.
    let createPersonAsync (client: ExperianClient) (person: WorkReport.CreatePersonRequestBody) (ct: CancellationToken) =
        attempt ct (fun () -> client.WorkReport.PostCisVerificationsWorkReportV1Persons(Json, Json, person, ct))

    /// Starts the connection journey for a person.
    let connectAsync (client: ExperianClient) (personId: int) (request: WorkReport.ConnectWorkReportRequestBody) (ct: CancellationToken) =
        attempt ct (fun () -> client.WorkReport.PostCisVerificationsWorkReportV1PersonConnect(Json, Json, personId, request, ct))

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
            attempt ct (fun () -> client.Idp.PostCisVerificationsIdpV1ApiApplicationDocuments(escaped, partyId, documents, ct)))

    /// Replaces the documents of one party.
    let replaceDocumentsAsync (client: ExperianClient) (customerApplicationId: string) (partyId: int) (documents: Idp.DocumentDetails) (ct: CancellationToken) =
        withSegment application customerApplicationId (fun escaped ->
            attempt ct (fun () -> client.Idp.PutCisVerificationsIdpV1ApiApplicationDocuments(escaped, partyId, documents, ct)))

    /// The documents of one party and their extraction results.
    let getDocumentsAsync (client: ExperianClient) (customerApplicationId: string) (partyId: int) (ct: CancellationToken) =
        withSegment application customerApplicationId (fun escaped ->
            attempt ct (fun () -> client.Idp.GetCisVerificationsIdpV1ApiApplication(escaped, partyId, ct)))

    let createWebhookAsync (client: ExperianClient) (webhook: Idp.WebhookRequest) (ct: CancellationToken) =
        attempt ct (fun () -> client.Idp.PostCisVerificationsIdpV1ApiWebhooks(webhook, ct))

    let listWebhooksAsync (client: ExperianClient) (ct: CancellationToken) =
        attempt ct (fun () -> client.Idp.GetCisVerificationsIdpV1ApiWebhooks ct)

    let getWebhookAsync (client: ExperianClient) (webhookId: Guid) (ct: CancellationToken) =
        attempt ct (fun () -> client.Idp.GetCisVerificationsIdpV1ApiWebhook(webhookId, ct))

    let updateWebhookAsync (client: ExperianClient) (webhookId: Guid) (webhook: Idp.WebhookRequest) (ct: CancellationToken) =
        attempt ct (fun () -> client.Idp.PatchCisVerificationsIdpV1ApiWebhook(webhookId, webhook, ct))

    let deleteWebhookAsync (client: ExperianClient) (webhookId: Guid) (ct: CancellationToken) =
        attempt ct (fun () -> client.Idp.DeleteCisVerificationsIdpV1ApiWebhook(webhookId, ct))

    /// The event types a webhook can subscribe to.
    let listEventTypesAsync (client: ExperianClient) (ct: CancellationToken) =
        attempt ct (fun () -> client.Idp.GetCisVerificationsIdpV1ApiWebhooksEventtypes ct)

    /// Creates the webhook signing secret; deliveries then carry X-HMAC-SHA256-Signature.
    let createSecretAsync (client: ExperianClient) (ct: CancellationToken) =
        attempt ct (fun () -> client.Idp.PostCisVerificationsIdpV1ApiWebhooksSecrets ct)

    let getSecretAsync (client: ExperianClient) (ct: CancellationToken) =
        attempt ct (fun () -> client.Idp.GetCisVerificationsIdpV1ApiWebhooksSecrets ct)

    /// Replaces the webhook signing secret.
    let rotateSecretAsync (client: ExperianClient) (ct: CancellationToken) =
        attempt ct (fun () -> client.Idp.PatchCisVerificationsIdpV1ApiWebhooksSecrets ct)

    /// Removes the secret; deliveries are then unsigned.
    let deleteSecretAsync (client: ExperianClient) (ct: CancellationToken) =
        attempt ct (fun () -> client.Idp.DeleteCisVerificationsIdpV1ApiWebhooksSecrets ct)

/// PowerCurve Customer Acquisition. The handler sets Authorization, so the generated authorization argument is left null.
module PowerCurve =

    let private noAuthorization: string = null
    let private application = "powerCurveId"

    /// A new application, decisioned with the bureau data used.
    let applicationAsync (client: ExperianClient) (submission: PowerCurve.Submission) (ct: CancellationToken) =
        attempt ct (fun () -> client.PowerCurve.Application(noAuthorization, Json, Json, submission, ct))

    /// Re-decisions an application with new or amended data.
    let reprocessAsync (client: ExperianClient) (powerCurveId: string) (submission: PowerCurve.Submission) (ct: CancellationToken) =
        withSegment application powerCurveId (fun escaped ->
            attempt ct (fun () -> client.PowerCurve.Reprocess(noAuthorization, Json, Json, escaped, submission, ct)))

    /// Converts a quotation into a full application with a hard footprint.
    let quoteToFullAsync (client: ExperianClient) (powerCurveId: string) (submission: PowerCurve.Submission) (ct: CancellationToken) =
        withSegment application powerCurveId (fun escaped ->
            attempt ct (fun () -> client.PowerCurve.QuoteToFull(noAuthorization, Json, Json, escaped, submission, ct)))

    /// The current state of an application.
    let queryAsync (client: ExperianClient) (powerCurveId: string) (ct: CancellationToken) =
        withSegment application powerCurveId (fun escaped ->
            attempt ct (fun () -> client.PowerCurve.Query(noAuthorization, Json, Json, escaped, ct)))

    /// Decision details for an application.
    let decisionDetailAsync (client: ExperianClient) (powerCurveId: string) (request: PowerCurve.SubmissionDecDetails) (ct: CancellationToken) =
        withSegment application powerCurveId (fun escaped ->
            attempt ct (fun () -> client.PowerCurve.DecisionDetail(noAuthorization, Json, Json, escaped, request, ct)))

/// Bank Account Verify (consumer), in the UK.
module BankAccountVerify =

    /// A check of a person's bank details; acceptLanguage goes in the AcceptLanguage header the spec names (no hyphen).
    let verifyAsync (client: ExperianClient) (request: BankAccountVerify.RequestMessage) (acceptLanguage: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.BankAccountVerify.PostV1Api(request, ValueOption.toOption acceptLanguage, ct))

/// International Payments Verification, from the UK host.
module InternationalPayments =

    let verifyAsync (client: ExperianClient) (request: InternationalPayments.RequestMessage) (ct: CancellationToken) =
        attempt ct (fun () -> client.InternationalPayments.PostV1Api(request, ct))

/// Know Your Business, in the UK. businessRef is a Companies House number or an Experian non-limited key; regNumber a Companies House number;
/// ldcId comes from the data sources call. All are escaped as one segment.
module Kyb =

    /// What to search for; ValueNone leaves it out.
    type BusinessSearch =
        { BusinessRef: string voption
          Name: string voption
          Postcode: string voption
          PhoneNumber: string voption
          SearchCorporates: bool voption
          /// Sent as excludedissolved, which Experian's spec describes against its name: true includes dissolved limited
          /// businesses, false excludes them, and the default is true. Whichever is sent, check each result's BusinessStatus
          /// ('D' is dissolved) rather than rely on this to leave dissolved companies out.
          ExcludeDissolved: bool voption
          SearchNonCorps: bool voption }

        static member Default =
            { BusinessRef = ValueNone
              Name = ValueNone
              Postcode = ValueNone
              PhoneNumber = ValueNone
              SearchCorporates = ValueNone
              ExcludeDissolved = ValueNone
              SearchNonCorps = ValueNone }

    /// Who to search for; ValueNone leaves it out. Experian refuses a name search without Directors and Secretaries.
    type DirectorSearch =
        { Forename: string voption
          MiddleNames: string voption
          Surname: string voption
          Postcode: string voption
          Directors: bool voption
          Secretaries: bool voption }

        static member Default =
            { Forename = ValueNone
              MiddleNames = ValueNone
              Surname = ValueNone
              Postcode = ValueNone
              Directors = ValueSome true
              Secretaries = ValueSome true }

    let searchBusinessesAsync (client: ExperianClient) (search: BusinessSearch) (ct: CancellationToken) =
        attempt ct (fun () ->
            client.Kyb.BusinessSearch(
                Json,
                ValueOption.toOption search.BusinessRef,
                ValueOption.toOption search.Name,
                ValueOption.toOption search.Postcode,
                ValueOption.toOption search.PhoneNumber,
                flag search.SearchCorporates,
                flag search.ExcludeDissolved,
                flag search.SearchNonCorps,
                ct))

    let searchDirectorsAsync (client: ExperianClient) (search: DirectorSearch) (ct: CancellationToken) =
        attempt ct (fun () ->
            client.Kyb.DirectorSearch(
                Json,
                ValueOption.toOption search.Forename,
                ValueOption.toOption search.MiddleNames,
                ValueOption.toOption search.Surname,
                ValueOption.toOption search.Postcode,
                flag search.Directors,
                flag search.Secretaries,
                ct))

    /// A company's profile.
    let profileAsync (client: ExperianClient) (businessRef: string) (ct: CancellationToken) =
        withSegment "businessRef" businessRef (fun escaped -> attempt ct (fun () -> client.Kyb.BusinessProfile(Json, escaped, ct)))

    /// Trading activity.
    let sectorsAsync (client: ExperianClient) (businessRef: string) (ct: CancellationToken) =
        withSegment "businessRef" businessRef (fun escaped -> attempt ct (fun () -> client.Kyb.IndustrySectors(Json, escaped, ct)))

    let locationsAsync (client: ExperianClient) (businessRef: string) (ct: CancellationToken) =
        withSegment "businessRef" businessRef (fun escaped -> attempt ct (fun () -> client.Kyb.Locations(Json, escaped, ct)))

    /// The countries it is associated with.
    let geographiesAsync (client: ExperianClient) (businessRef: string) (ct: CancellationToken) =
        withSegment "businessRef" businessRef (fun escaped -> attempt ct (fun () -> client.Kyb.Geographies(Json, escaped, ct)))

    /// Where the data comes from, with the ids the visit calls take.
    let dataSourcesAsync (client: ExperianClient) (businessRef: string) (ct: CancellationToken) =
        withSegment "businessRef" businessRef (fun escaped -> attempt ct (fun () -> client.Kyb.DataSources(Json, escaped, ct)))

    let directorsAsync (client: ExperianClient) (regNumber: string) (ct: CancellationToken) =
        withSegment "regNumber" regNumber (fun escaped -> attempt ct (fun () -> client.Kyb.Directors(Json, escaped, ct)))

    let shareholdersAsync (client: ExperianClient) (regNumber: string) (ct: CancellationToken) =
        withSegment "regNumber" regNumber (fun escaped -> attempt ct (fun () -> client.Kyb.Shareholders(Json, escaped, ct)))

    /// Its ultimate beneficial owners.
    let ultimateOwnersAsync (client: ExperianClient) (regNumber: string) (ct: CancellationToken) =
        withSegment "regNumber" regNumber (fun escaped -> attempt ct (fun () -> client.Kyb.Ubo(Json, escaped, ct)))

    /// When an agent last visited the location.
    let lastVisitAsync (client: ExperianClient) (ldcId: string) (ct: CancellationToken) =
        withSegment "ldcId" ldcId (fun escaped -> attempt ct (fun () -> client.Kyb.LdcBase(Json, escaped, ct)))

    /// Whether a money service business was found there.
    let moneyServiceAsync (client: ExperianClient) (ldcId: string) (ct: CancellationToken) =
        withSegment "ldcId" ldcId (fun escaped -> attempt ct (fun () -> client.Kyb.Ldcmsb(Json, escaped, ct)))

    /// A link to a picture of the shopfront.
    let shopfrontAsync (client: ExperianClient) (ldcId: string) (ct: CancellationToken) =
        withSegment "ldcId" ldcId (fun escaped -> attempt ct (fun () -> client.Kyb.LdcImage(Json, escaped, ct)))

    /// Screens and authenticates people against PEP, sanctions and adverse media data.
    let peopleDataAsync (client: ExperianClient) (request: Kyb.KybDataPeopleRequestBody) (ct: CancellationToken) =
        attempt ct (fun () -> client.Kyb.PostComplianceBusinessV1PeopleKybdata(Json, request, Some Json, ct))

    /// A summary of a business, matched to third-party sources; DataBlocks chooses what comes back.
    let companyDataAsync (client: ExperianClient) (request: Kyb.KybDataRequestBody) (ct: CancellationToken) =
        attempt ct (fun () -> client.Kyb.PostComplianceBusinessV1CompanyKybdata(Json, request, Some Json, ct))

/// AML Flags, in the UK: a company's adverse markers and cashflow profiles. KYB's company data call: DataBlocks asks for AMLFlags.
module AmlFlags =

    let companyAsync (client: ExperianClient) (request: Kyb.KybDataRequestBody) (ct: CancellationToken) =
        Kyb.companyDataAsync client request ct

/// CrossCore age threshold and date of birth checks, in the UK. Experian's delivery team provides the tenant id and request type, which
/// choose the check; its documents show DoBVerify-Standalone and Standalone-DoBVerify for date of birth, and AgeThreshold-Standalone for age.
module AgeAndDateOfBirth =

    /// acceptLanguage goes in the AcceptLanguage header the spec names (no hyphen).
    let checkAsync (client: ExperianClient) (request: AgeAndDateOfBirth.RequestMessage) (acceptLanguage: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.AgeAndDateOfBirth.PostV1Api(request, ValueOption.toOption acceptLanguage, ct))

/// The United States: eCBSV, whether a Social Security number, name and date of birth match the Social Security Administration's records. It
/// needs the person's consent, and subcode and companyId from onboarding.
module SsnVerification =

    /// externalTransactionId comes back to trace the request.
    let verifyAsync (client: ExperianClient) (subcode: string) (companyId: string) (request: SsnVerification.verifyRequest) (externalTransactionId: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.SsnVerification.VerifyOperation(Json, Json, subcode, companyId, request, ValueOption.toOption externalTransactionId, ct))

/// The United States: Experian Verify, income and employment reports answered at once. All share one call; reportType in the request and
/// the subcode from onboarding choose the report, and the answer's fields follow it. It comes in ExpVerifyReport, or IncomeReport from the
/// sandbox, which answers every report with one fixed example. clientReferenceId identifies the request to Experian.
module Verify =

    let coreAsync (client: ExperianClient) (subcode: string) (clientReferenceId: string) (request: VerifyCore.RequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyCore.GetIncomeAndEmploymentData(clientReferenceId, Json, Json, subcode, request, ct))

    let plusAsync (client: ExperianClient) (subcode: string) (clientReferenceId: string) (request: VerifyPlus.RequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyPlus.GetIncomeAndEmploymentData(clientReferenceId, Json, Json, subcode, request, ct))

    let premiumAsync (client: ExperianClient) (subcode: string) (clientReferenceId: string) (request: VerifyPremium.voieRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyPremium.GetIncomeAndEmploymentData(clientReferenceId, Json, Json, subcode, request, ct))

    let premiumPrequalAsync (client: ExperianClient) (subcode: string) (clientReferenceId: string) (request: VerifyPremiumPrequal.voieRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyPremiumPrequal.GetIncomeAndEmploymentData(clientReferenceId, Json, Json, subcode, request, ct))

    let employmentScreeningAsync (client: ExperianClient) (subcode: string) (clientReferenceId: string) (request: VerifyEmployment.RequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyEmployment.GetEmploymentData(clientReferenceId, Json, Json, subcode, request, ct))

    let governmentAsync (client: ExperianClient) (subcode: string) (clientReferenceId: string) (request: VerifyGovernment.RequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyGovernment.GetIncomeAndEmploymentData(clientReferenceId, Json, Json, subcode, request, ct))

    let reverifyEmploymentAsync (client: ExperianClient) (subcode: string) (clientReferenceId: string) (request: ReverifyVoe.RequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.ReverifyVoe.GetIncomeAndEmploymentData(clientReferenceId, Json, Json, subcode, request, ct))

    let reverifyIncomeAndEmploymentAsync (client: ExperianClient) (subcode: string) (clientReferenceId: string) (request: ReverifyVoie.voieRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.ReverifyVoie.GetIncomeAndEmploymentData(clientReferenceId, Json, Json, subcode, request, ct))

/// The United States: Experian Verify with the person's permission, on a page the client hosts. The widget call gives the page's address and a
/// report id; the status call says when it is ready; the report call fetches it. Live, this is its own host: Endpoints.productionUs has it.
module VerifyCpd =

    let coreWidgetAsync (client: ExperianClient) (clientReferenceId: string) (request: VerifyCpdCore.CPDRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyCpdCore.GetWidgetUrlTokenExpirationAndReportId(clientReferenceId, Json, Json, request, ct))

    let coreStatusAsync (client: ExperianClient) (clientReferenceId: string) (request: VerifyCpdCore.CPDStatusRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyCpdCore.GetTheStatusOfTheExperianReportId(clientReferenceId, Json, Json, request, ct))

    let coreReportAsync (client: ExperianClient) (clientReferenceId: string) (request: VerifyCpdCore.CPDStatusRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyCpdCore.GetTheReportBasedOnGivenReportId(clientReferenceId, Json, Json, request, ct))

    let plusWidgetAsync (client: ExperianClient) (clientReferenceId: string) (request: VerifyCpdPlus.CPDRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyCpdPlus.GetWidgetUrlTokenExpirationAndReportId(clientReferenceId, Json, Json, request, ct))

    let plusStatusAsync (client: ExperianClient) (clientReferenceId: string) (request: VerifyCpdPlus.CPDStatusRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyCpdPlus.GetTheStatusOfTheExperianReportId(clientReferenceId, Json, Json, request, ct))

    let plusReportAsync (client: ExperianClient) (clientReferenceId: string) (request: VerifyCpdPlus.CPDStatusRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyCpdPlus.GetTheReportBasedOnGivenReportId(clientReferenceId, Json, Json, request, ct))

    let premiumWidgetAsync (client: ExperianClient) (clientReferenceId: string) (request: VerifyCpdPremium.CPDRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyCpdPremium.GetWidgetUrlTokenExpirationAndTransactionId(clientReferenceId, Json, Json, request, ct))

    let premiumStatusAsync (client: ExperianClient) (clientReferenceId: string) (request: VerifyCpdPremium.CPDStatusRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyCpdPremium.GetTheStatusOfTheExperianReportId(clientReferenceId, Json, Json, request, ct))

    let premiumReportAsync (client: ExperianClient) (clientReferenceId: string) (request: VerifyCpdPremium.CPDStatusRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.VerifyCpdPremium.GetTheReportBasedOnGivenReportId(clientReferenceId, Json, Json, request, ct))

    let reverifyEmploymentWidgetAsync (client: ExperianClient) (clientReferenceId: string) (request: ReverifyCpdVoe.CPDRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.ReverifyCpdVoe.GetWidgetUrlTokenExpirationAndReportId(clientReferenceId, Json, Json, request, ct))

    let reverifyEmploymentStatusAsync (client: ExperianClient) (clientReferenceId: string) (request: ReverifyCpdVoe.CPDStatusRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.ReverifyCpdVoe.GetTheStatusOfTheExperianReportId(clientReferenceId, Json, Json, request, ct))

    let reverifyEmploymentReportAsync (client: ExperianClient) (clientReferenceId: string) (request: ReverifyCpdVoe.CPDStatusRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.ReverifyCpdVoe.GetTheReportBasedOnGivenReportId(clientReferenceId, Json, Json, request, ct))

    let reverifyIncomeAndEmploymentWidgetAsync (client: ExperianClient) (clientReferenceId: string) (request: ReverifyCpdVoie.CPDRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.ReverifyCpdVoie.GetWidgetUrlTokenExpirationAndTransactionId(clientReferenceId, Json, Json, request, ct))

    let reverifyIncomeAndEmploymentStatusAsync (client: ExperianClient) (clientReferenceId: string) (request: ReverifyCpdVoie.CPDStatusRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.ReverifyCpdVoie.GetTheStatusOfTheExperianReportId(clientReferenceId, Json, Json, request, ct))

    let reverifyIncomeAndEmploymentReportAsync (client: ExperianClient) (clientReferenceId: string) (request: ReverifyCpdVoie.CPDStatusRequestParams) (ct: CancellationToken) =
        attempt ct (fun () -> client.ReverifyCpdVoie.GetTheReportBasedOnGivenReportId(clientReferenceId, Json, Json, request, ct))

/// The United States: Instant Credit. clientReferenceId identifies the request to Experian; the sandbox takes SBMYSQL.
module InstantCredit =

    let advancedAsync (client: ExperianClient) (request: InstantCredit.attributesRequest) (clientReferenceId: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.InstantCredit.AdvancedInstantCredit(Json, request, ValueOption.toOption clientReferenceId, Some Json, ct))

    let customStandaloneAsync (client: ExperianClient) (request: InstantCredit.attributesRequest) (clientReferenceId: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.InstantCredit.CustomStandaloneInstantCredit(Json, request, ValueOption.toOption clientReferenceId, Some Json, ct))

/// The United States: Credit Profile. clientReferenceId identifies the request to Experian; the sandbox takes SBMYSQL.
module CreditProfile =

    /// A credit report with the scores and add-ons asked for.
    let creditReportAsync (client: ExperianClient) (request: CreditProfile.creditProfileRequest) (clientReferenceId: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.CreditProfile.CreditReports(Json, request, ValueOption.toOption clientReferenceId, Some Json, ct))

    let employmentInsightAsync (client: ExperianClient) (request: CreditProfile.employmentInsightRequest) (clientReferenceId: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.CreditProfile.EmploymentInsightData(Json, request, ValueOption.toOption clientReferenceId, Some Json, ct))

    let healthcareAsync (client: ExperianClient) (request: CreditProfile.healthcareCreditRequest) (clientReferenceId: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.CreditProfile.HealthcareCreditProfileData(Json, request, ValueOption.toOption clientReferenceId, Some Json, ct))

    let automotiveAsync (client: ExperianClient) (request: CreditProfile.autoCreditProfileRequest) (clientReferenceId: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.CreditProfile.AutomotiveCreditProfileData(Json, request, ValueOption.toOption clientReferenceId, Some Json, ct))

    let tecConnectAsync (client: ExperianClient) (request: CreditProfile.tecCreditProfileRequest) (clientReferenceId: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.CreditProfile.TecConnctCreditProfileData(Json, request, ValueOption.toOption clientReferenceId, Some Json, ct))

    let connectCheckAsync (client: ExperianClient) (request: CreditProfile.ConnectCreditProfileRequest) (clientReferenceId: string voption) (ct: CancellationToken) =
        attempt ct (fun () -> client.CreditProfile.ConnctCheckCreditProfileData(Json, request, ValueOption.toOption clientReferenceId, Some Json, ct))

/// The Netherlands and Spain: a small business's credit risk score from its digital footprint, the probability of it going bad
/// between 0 and 1.
module WebData =

    /// id comes back to trace the request; date is when the web data is taken from. The spec names only the sandbox's test path.
    let scoreAsync (client: ExperianClient) (date: DateOnly) (name: string) (address: string) (town: string) (id: string) (ct: CancellationToken) =
        task {
            let! answer = attempt ct (fun () -> client.WebData.GetWebscoresTestScore(date.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture), name, address, town, id, ct))

            // success is false on an answer that isn't a score, where the score would read as the lowest risk
            return
                answer
                |> Result.bind (fun score ->
                    if isNull (box score) || not score.Success then
                        Error(TransportError(System.IO.InvalidDataException "Experian's answer holds no score."))
                    else
                        Ok score)
        }

/// Brazil, through Serasa Experian: scores and checks of a person's open banking data. Connect with Endpoints.productionBrazil and a
/// Credentials of the client id and secret. Its sandbox has stopped answering, so these have only run against a stand-in.
module OpenBanking =

    let scoreAsync (client: ExperianClient) (request: OpenBankingScore.Score) (ct: CancellationToken) =
        attempt ct (fun () -> client.OpenBankingScore.Score(request, ct))

    let incomeVerificationAsync (client: ExperianClient) (request: IncomeVerifications.IncomeVerification) (ct: CancellationToken) =
        attempt ct (fun () -> client.IncomeVerification.IncomeVerification(request, ct))

    let categorizeAsync (client: ExperianClient) (request: TransactionCategorization.Transaction) (ct: CancellationToken) =
        attempt ct (fun () -> client.TransactionCategorization.CategorizeTransactions(request, ct))

    let affordabilityCheckAsync (client: ExperianClient) (request: AffordabilityChecks.AffordabilityCheck) (ct: CancellationToken) =
        attempt ct (fun () -> client.AffordabilityCheck.AffordabilityChecks(request, ct))

/// Experian's Address Validation, over an ApertureClient. Keys are escaped as one segment. Layouts, and the older lookup v1, are on
/// the generated client: client.Address.
module AddressValidation =

    /// What goes in a call's headers; ValueNone leaves Experian's default. Each call sends only those it takes.
    type Options =
        { /// Comes back in the answer, to trace the request.
          ReferenceId: string voption
          /// 2 to 15; Experian answers 408 when it runs out.
          TimeoutSeconds: int voption
          AddComponents: bool voption
          AddMetadata: bool voption
          AddEnrichment: bool voption
          AddDpid: bool voption
          AddExtraMatchInfo: bool voption
          AddAddresses: bool voption
          AddFinalAddress: bool voption
          Transliterate: bool voption
          AddRetention: bool voption }

        static member Default =
            { ReferenceId = ValueNone
              TimeoutSeconds = ValueNone
              AddComponents = ValueNone
              AddMetadata = ValueNone
              AddEnrichment = ValueNone
              AddDpid = ValueNone
              AddExtraMatchInfo = ValueNone
              AddAddresses = ValueNone
              AddFinalAddress = ValueNone
              Transliterate = ValueNone
              AddRetention = ValueNone }

    let private reference (options: Options) = ValueOption.toOption options.ReferenceId
    let private timeout (options: Options) = ValueOption.toOption options.TimeoutSeconds

    /// The datasets the token can use, in one country or all.
    let datasetsAsync (client: ApertureClient) (countryIso: string voption) (options: Options) (ct: CancellationToken) =
        attempt ct (fun () -> client.Address.GetAddressDatasetsV1(reference options, timeout options, ValueOption.toOption countryIso, ct))

    /// Suggestions for what has been typed so far. Takes Transliterate and AddRetention.
    let searchAsync (client: ApertureClient) (request: AddressValidation.AddressSearchV1Request) (options: Options) (ct: CancellationToken) =
        attempt ct (fun () -> client.Address.PostAddressSearchV1(request, reference options, timeout options, flag options.Transliterate, flag options.AddRetention, ct))

    /// The addresses inside a suggestion, such as those in a block of flats.
    let stepInAsync (client: ApertureClient) (globalAddressKey: string) (options: Options) (ct: CancellationToken) =
        withSegment "globalAddressKey" globalAddressKey (fun escaped ->
            attempt ct (fun () -> client.Address.GetAddressSuggestionsStepinV1(escaped, reference options, timeout options, ct)))

    /// Narrows a suggestion by more text.
    let refineAsync (client: ApertureClient) (key: string) (request: AddressValidation.AddressSuggestionsV1Request) (options: Options) (ct: CancellationToken) =
        withSegment "key" key (fun escaped ->
            attempt ct (fun () -> client.Address.PostAddressSuggestionsRefineV1(escaped, request, reference options, timeout options, ct)))

    /// Suggestions already formatted. Takes AddComponents and AddMetadata.
    let formatSuggestionsAsync (client: ApertureClient) (request: AddressValidation.AddressSuggestionsFormatV1Request) (options: Options) (ct: CancellationToken) =
        attempt ct (fun () -> client.Address.PostAddressSuggestionsFormatV1(request, reference options, timeout options, flag options.AddComponents, flag options.AddMetadata, ct))

    /// Whether a whole address is real, with its corrected form. Takes AddComponents, AddMetadata, AddEnrichment and AddExtraMatchInfo.
    let validateAsync (client: ApertureClient) (request: AddressValidation.AddressValidateV1Request) (options: Options) (ct: CancellationToken) =
        attempt ct (fun () -> client.Address.PostAddressValidateV1(request, reference options, timeout options, flag options.AddComponents, flag options.AddMetadata, flag options.AddEnrichment, flag options.AddExtraMatchInfo, ct))

    /// The addresses at a key such as a postcode. Takes AddAddresses and AddFinalAddress.
    let lookupAsync (client: ApertureClient) (request: AddressValidation.AddressLookupV2Request) (options: Options) (ct: CancellationToken) =
        attempt ct (fun () -> client.Address.PostAddressLookupV2(request, reference options, timeout options, flag options.AddAddresses, flag options.AddFinalAddress, ct))

    /// The final address behind a key from a search. Takes AddComponents, AddMetadata and AddDpid.
    let formatAsync (client: ApertureClient) (globalAddressKey: string) (options: Options) (ct: CancellationToken) =
        withSegment "globalAddressKey" globalAddressKey (fun escaped ->
            attempt ct (fun () -> client.Address.GetAddressFormatV1(escaped, reference options, timeout options, flag options.AddComponents, flag options.AddMetadata, flag options.AddDpid, ct)))

    /// The final address in the layouts and with the attributes asked for. Takes AddComponents, AddMetadata, AddEnrichment and AddDpid.
    let formatWithAsync (client: ApertureClient) (globalAddressKey: string) (request: AddressValidation.AddressFormatV1Request) (options: Options) (ct: CancellationToken) =
        withSegment "globalAddressKey" globalAddressKey (fun escaped ->
            attempt ct (fun () -> client.Address.PostAddressFormatV1(escaped, request, reference options, timeout options, flag options.AddComponents, flag options.AddMetadata, flag options.AddEnrichment, flag options.AddDpid, ct)))

    /// The fields to ask a person for, in a country and dataset.
    let promptSetsAsync (client: ApertureClient) (request: AddressValidation.AddressPromptSetsV1Request) (options: Options) (ct: CancellationToken) =
        attempt ct (fun () -> client.Address.PostAddressPromptsetsV1(request, reference options, timeout options, ct))

/// What goes in the headers of an email or phone validation; ValueNone leaves Experian's default.
type ValidationOptions =
    { /// Comes back in the answer, to trace the request.
      ReferenceId: string voption
      /// Experian answers 408 when it runs out: 3 to 15 for email, 2 to 15 for phone.
      TimeoutSeconds: int voption
      /// Adds the domain or phone details to the answer.
      AddMetadata: bool voption }

    static member Default =
        { ReferenceId = ValueNone
          TimeoutSeconds = ValueNone
          AddMetadata = ValueNone }

/// Experian's Email Validation v2, over an ApertureClient. The answer's confidence says whether the mailbox can be delivered to, and did_you_mean
/// suggests a correction.
module EmailValidation =

    let validateAsync (client: ApertureClient) (email: string) (options: ValidationOptions) (ct: CancellationToken) =
        attempt ct (fun () ->
            client.Email.PostEmailValidateV2(
                EmailValidation.EmailValidationV2Request(email),
                ValueOption.toOption options.ReferenceId,
                ValueOption.toOption options.TimeoutSeconds,
                flag options.AddMetadata,
                ct))

/// Experian's Phone Validation, over an ApertureClient. The answer gives whether the number is live, its type and its format.
module PhoneValidation =

    let validateAsync (client: ApertureClient) (request: PhoneValidation.PhoneValidationV2Request) (options: ValidationOptions) (ct: CancellationToken) =
        attempt ct (fun () ->
            client.Phone.PostPhoneValidateV2(
                request,
                flag options.AddMetadata,
                ValueOption.toOption options.ReferenceId,
                ValueOption.toOption options.TimeoutSeconds,
                ct))
