# ExperianClient

An F# client for the most used Experian UK developer-portal APIs, generated with
[SwaggerProvider](https://github.com/fsprojects/SwaggerProvider) from the specs in `src/ExperianClient/Schemas`.
It targets `net10.0`.

It does not cover every Experian service, only the six products below. To add another, put its spec in `Schemas`,
add a provider type in `Providers.fs` and its base path in `Endpoints`, and wrap its calls in `Api`. The token, the
JSON handling and the error type are shared.

Each product is its own type with its own models:

| Type | Product | Wrapped calls (`Api.*`) |
|---|---|---|
| `DelphiSelect` | Delphi Select v2.5: consumer bureau search, Identity Authenticate (AuthPlus), Detect, Affordability, ExPIN | application, get, reprocess, quote to full, second phase authenticate (AuthPlus) and detect, stand-alone authenticate (AuthPlus), detect, affordability, ExPIN |
| `Kyc` | Know Your Customer (consumer), CrossCore | KYC check, PEP and sanctions screen |
| `PowerCurve` | PowerCurve Customer Acquisition v1.21 | application, reprocess, quote to full, query, decision detail |
| `WorkReport` | Verifications Work Report | create person, connect, report PDF |
| `Ned` | National Employer Database | employer search, employer by id |
| `Idp` | Intelligent Document Processing | documents (upload, replace, get), webhooks, event types, signing secret |

AuthPlus is part of Delphi Select: request it in a new application with `Options.AuthenticatePlusRequired`, add it
to an earlier application with `Api.DelphiSelect.authenticateAsync` (second phase, no applicant details needed), or
run it without a credit search with `Api.DelphiSelect.authenticateOnlyAsync`.

## Search type: soft or hard footprint

The search type (CAPS type) decides the footprint left on the applicant's credit file, and writing the correct one is
a legal obligation. `SearchType` keeps the two apart:
- `Quotation Quotation.PersonalLoan` (`QL`) and the other quotations leave a **soft** footprint, which other lenders
  don't see; use them for eligibility.
- `CreditApplication CreditProduct.DebtConsolidationLoan` (`CD`), `PersonalLoan` (`PL`) and the others leave a
  **hard** one.
- `OtherSearch "EQ"` sends any other code from Experian's list unclassified.

```fsharp
let quote = input |> Api.DelphiSelect.setSearchType (Quotation Quotation.PersonalLoan)      // soft
// ... the customer accepts the offer:
Api.DelphiSelect.convertQuoteAsync experian experianReference CreditProduct.DebtConsolidationLoan (ValueSome "A1234") ct  // hard
```

`convertQuoteAsync` only takes a `CreditProduct`, so a quotation can't be "converted" into another soft search.

## Credit accounts and the debt list

`CreditAccounts.ofOutput output` reads every credit account (CAIS record) in a Delphi Select answer, with:
- the lender, account number, Experian's account and company type codes;
- whose file it matched (applicant, alias, associate, joint applicant), whether it's joint;
- its status; a default with a satisfied date or a paid narrative counts as settled;
- balance, default balance, delinquent balance, credit limit and payment in pounds;
- start, settlement and last-updated dates, and the worst status.

It returns `ValueNone` when the answer carries no consumer data at all, and `ValueSome []` when there are no
accounts, so a missing credit file is never read as "no debts".

`CreditAccounts.amountOwed` is what an account owes now: a defaulted account's current default balance when Experian
reports one, otherwise its balance. `CreditAccounts.outstanding` keeps the applicant's own accounts (including under
an alias) that aren't settled and still owe something; associates' accounts are someone else's credit.
`CreditAccounts.totalOwed` sums `amountOwed`.

```fsharp
match CreditAccounts.ofOutput output with
| ValueSome accounts -> let debts = CreditAccounts.outstanding accounts in CreditAccounts.totalOwed debts
| ValueNone -> failwith "no credit file in the answer"
```

Before using it for a consolidation loan:
- **Not a payoff figure.** Balances are what lenders last reported, roughly monthly. Get a settlement figure from each
  lender before paying.
- **It includes mortgages and contracts**, such as a mortgage (type `03`) or a phone contract (type `18`). Choose what
  to consolidate by `AccountTypeCode`, using Experian's Standard Data Codes; they aren't in the spec, so the codes are
  left as Experian sends them.
- **Names and account numbers can be missing.** Experian withholds some lender names (for example company type `10`),
  and shows account numbers only for your own data unless agreed otherwise.

## Financial difficulty and vulnerability signals

Delphi Select has no vulnerability flag. `CreditFileSignals.ofOutput output` gathers the credit-file signals that can
point to financial difficulty or vulnerability; they corroborate your own assessment, they don't replace it:
- `Flags`: summary flags for the whole search (arrangement, debt management, credit insurance claim, account query,
  partial settlement, debt assigned, voluntary termination, recourse, gone away, deceased, CIFAS). They can come from
  associated people's data, not only the applicant's.
- `Instructions`: the applicant's own accounts with a special instruction (arrangement, debt management programme,
  credit insurance claim, …) and its dates. `CreditFileSignals.currentInstructions today` keeps those in force.
- `Notices`: the consumer's Notices of Correction, their own statement on the file, as text lines. Lenders are expected
  to read them before deciding.
- `Cifas`: CIFAS markers matched to the applicant, with protective registration (extra identity checks the consumer
  asked for) told apart from fraud categories.

Like `CreditAccounts.ofOutput`, it returns `ValueNone` when the answer carries no consumer data.

## Outcomes and reports

Typed views over the parts of an answer that decisions rest on. Codes Experian lists in the spec become cases;
anything else stays visible as an `Other…` case.

- **AuthPlus:** `AuthPlus.ofOutput output` (or `AuthPlus.ofResults` for second-phase and stand-alone answers) gives
  each applicant's result:
  - the decision: `Authenticated`, `NotAuthenticated` or `Refer` with its level, or `NoDecision`;
  - the decision text, the authentication index, and the level the identity was confirmed at;
  - the high-risk policy rules that fired.
- **Impaired credit and public records:**
  - `PublicRecords.impairedCredit output` gives Experian's classification per applicant: bankruptcy or IVA in the last
    three years, CCJ total by band, and three or more months of secured or unsecured arrears in the last two years.
    The joint applicant's is read only when the answer lists two applicants.
  - `PublicRecords.ofOutput output` lists the public records with Experian's type code, whose file they matched,
    amount, plaintiff and satisfaction date. `PublicRecords.unsatisfied` keeps the applicant's own unsatisfied ones.
- **Affordability:** `Affordability.ofOutput output` gives:
  - CATO income estimates (gross annual, net annual, net monthly) for the main applicant, the joint applicant and the
    household;
  - how the income the applicant supplied compares with the estimate;
  - unsecured and secured debt-to-income, and revolving and non-revolving commitments as a share of income.

  Fields are `ValueNone` when the package that fills them wasn't requested. Clients set up for the CATO warning
  indicator get verification codes 1-3, which stay as `OtherVerificationCode`. Experian's other affordability variables
  stay on the generated types.
- **KYC and PEP/sanctions:** `KycOutcome.ofResponse response` gives:
  - the overall decision (`Continue`, `Refer`, `Stop`), its reasons and recommended next actions;
  - per service, the rules that fired. For identity verification they confirm; for contra-indicator and PEP/sanctions
    services they alert. The housekeeping rules 9990 and 9991 are left out.

## Use

```fsharp
open Experian

let credentials =
    { Username = "portal user"          // the Experian developer-portal login
      Password = "portal password"
      ClientId = "app client id"        // from My Apps
      ClientSecret = "app client secret" }

// One per application: it holds the token cache and the connections.
let experian = ExperianClient.Create(credentials, Endpoints.sandbox)

let input =
    DelphiSelect.Input(
        Submission =
            DelphiSelect.Submission(
                Applicants = [| DelphiSelect.Applicant(ApplicantIdentifier = 1,
                                    Person = DelphiSelect.Person(Forename = Some "ALEXANDER", Surname = Some "MAYER",
                                                                 DateOfBirth = Some(System.DateOnly(1981, 6, 19)))) |],
                Locations = [| DelphiSelect.LocationItem(LocationIdentifier = 1,
                                   UkLocation = DelphiSelect.UKLocation(HouseNumber = Some "36", Postcode = Some "BT13 3SJ")) |],
                Residencies = [| DelphiSelect.ResidencyItem(ApplicantIdentifier = 1, LocationIdentifier = 1,
                                     ResidentFrom = Some(System.DateOnly(2018, 2, 18)), Type = Some "01", CanBeSearched = Some "Y") |],
                Application = DelphiSelect.Application(ApplicationType = Some "EQ", SearchConsent = Some true),
                Options = DelphiSelect.Options(FullFblRequired = true, AuthenticatePlusRequired = true),
                ClientData = DelphiSelect.ClientData(ClientAccountNumber = Some "A1234")))

task {
    match! Api.DelphiSelect.applicationAsync experian input cancellationToken with
    | Ok output -> printfn "%A" output.Response.AuthPlusResults
    | Error failure -> printfn "%s" (ExperianFailure.describe failure)
}
```

Every `Api` call returns `Task<Result<_, ExperianFailure>>`:

- `InvalidRequest reason`: not sent. A path parameter was empty, `.` or `..`.
- `ApiError(status, body)`: Experian answered with a non-success status.
- `TokenRefused(status, body)`: the token endpoint refused the credentials.
- `TransportError exn`: there was no answer, or it could not be read.

Cancelling your token throws `OperationCanceledException` as usual. For operations not in `Api`, the generated
clients are on the client, already authorised: `experian.DelphiSelect`, `experian.Kyc`, `experian.PowerCurve`, and so on.

`ExperianClient.create credentials endpoints handler` shares a caller-owned `HttpMessageHandler`, such as one
`SocketsHttpHandler` for the whole application. The client never disposes it.

String path parameters (Experian references, PowerCurve ids, NED employer ids, IDP application ids) are escaped as
one URL segment by the `Api` functions. The generated clients insert them as they are, so if you call those directly
(`experian.DelphiSelect.GetApplication`, …), pass only values you trust. Printing `Credentials` or an `AccessToken`
shows neither the password, the client secret nor the token.

`Api.WorkReport.requestReportAsync` returns the report PDF bytes. The generated client has no return value for binary
answers, so this call is sent directly over the same authorised connection.

## Authentication

Experian's token endpoint uses the OAuth password flow. It needs all four credentials: the developer-portal username
and password, plus the app's client id and secret. The client fetches the token on first use and keeps it until a
minute before `expires_in` runs out; concurrent callers share one fetch. A 401 drops the token and the request is
sent once more with a new one.

## Lenient reading

Experian's answers don't always match the specs, so responses are read leniently. Requests are still written exactly
as the specs type and name them.

- A string field may arrive as a number (Delphi scorecard ids `E5S042`, CIFAS code `SPAC24`, Detect indexes): read
  as its text.
- A number may arrive quoted (PowerCurve terms, `ReprocessCount`): read as the number.
- `""` or `"-"` in an optional date, number or enum (PowerCurve `ScoreIndex`): read as `None`.
- Property names may differ in case (NED sends `employerid`): matched case-insensitively. None of the generated
  types has two names that differ only in case.
- A success body that is a JSON string holding an object or array (the KYC sandbox answers this way) is decoded.
- JSON is sent as `application/json` without `charset`, since the gateway answers 415 to `charset=utf-8`.

## Endpoints

`Endpoints.sandbox` (`sandbox-uk-api.experian.com`) and `Endpoints.production` (`uk-api.experian.com`) use the
specs' base paths. The KYC spec only names its sandbox path (`/da/ccis-devportalapis/`), so set
`{ Endpoints.production with Kyc = ... }` if Experian gives you a different live one.

## IDP webhooks

After `Api.Idp.createSecretAsync`, deliveries carry `X-HMAC-SHA256-Signature`. Check the raw body before parsing:

```fsharp
Webhook.verify sharedKey bodyBytes (request.Headers[Webhook.SignatureHeader])
```

The key is the UTF-8 bytes of `sharedKey` as Experian returns it. The spec doesn't state the key encoding, so this is
inferred and unconfirmed; check it against the first real delivery.

## Changes to the bundled specs

The specs are Experian's downloads with these edits, made so SwaggerProvider 4.2.1 reads them and the wire format
stays right:

- **All specs:**
  - Number fields without a format, or with `decimal` or `float`, are `format: double`. Otherwise SwaggerProvider
    makes them `float32`, and an amount like 1234567.89 would be sent as 1234567.9.
  - String enums with bare numeric values (`- 0`) are quoted. YAML reads them as numbers, which gives empty enums.
- **Delphi Select:** the six `GetApplication` view flags are strings, not booleans. The generated client would write
  booleans as `True`/`False`; `Api.DelphiSelect.getApplicationAsync` sends `true`/`false`.
- **Work Report:**
  - `Income` has no discriminator: its `$type` isn't a required property, which the reader rejects. `Incomes` keeps
    its own.
  - Person addresses use `postCode`, as Experian's examples and the API do (the downloaded spec has `postcode`).
- **KYC:**
  - `Address` gains `subLocality`, `stateProvinceCode` and `residentTo`; the request header gains `txnId` and
    `time`; decision elements gain `decisionReason`. All appear in Experian's examples and answers, and the
    generated types would otherwise drop them.
  - `fullDateFrom` is a plain string: the API sends `2019-01-01T00:00:00`, not a date.
- **NED:** the employer search returns a list, as in Experian's example, not one employer.
- **IDP:** event types come as `{"eventNames": [...]}`, not a bare list.
- **PowerCurve:**
  - Two-digit-year dates (`NDDOB`, `EA4S02`, `EA4S04`, `EA4S06`, documented as YY-MM-DD) are plain strings, not
    dates.
  - Sample raw responses inside examples are shortened: six single-line samples and one HTML description exceed the
    reader's 64 KB scalar limit.
  - 63 references whose target name ends with the referring property's name (`DeliveryDetails` →
    `MortgageSpecific_DeliveryDetails`, `items` → `..._Item`) are wrapped as a one-element `allOf`. Plain references
    would hit a SwaggerProvider name clash ("Cannot compile object … based on unresolved reference"). The wrapped type
    is the same.

## Tests

`dotnet test` runs the offline tests against a fake handler. The sandbox tests run only when `EXPERIAN_USERNAME`,
`EXPERIAN_PASSWORD`, `EXPERIAN_CLIENT_ID` and `EXPERIAN_CLIENT_SECRET` are set. They cover:
- the token;
- the application with AuthPlus and second phase, stand-alone AuthPlus, and quotation to full;
- the Work Report flow up to the PDF;
- PowerCurve application, query and decision;
- NED and IDP reads.
