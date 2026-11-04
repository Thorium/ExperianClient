/// Generated clients, one per Experian product; each product has its own types.
[<AutoOpen>]
module Experian.Providers

open SwaggerProvider

let [<Literal>] private schemas = __SOURCE_DIRECTORY__ + "/Schemas/"

/// Delphi Select v2: consumer bureau search, Identity Authenticate (AuthPlus), Detect, Affordability, ExPIN.
type DelphiSelect = OpenApiClientProvider<const(schemas + "delphi-select-v2-swagger-2.5.0.0.yaml"), PreferAsync=true>

/// Know Your Customer (consumer) and PEP / sanctions screening.
type Kyc = OpenApiClientProvider<const(schemas + "kyc_20250629_sandbox_swagger_0.yaml"), PreferAsync=true>

/// National Employer Database employer search.
type Ned = OpenApiClientProvider<const(schemas + "ned_0.11.yaml"), PreferAsync=true>

/// Verifications Work Report: persons, connections, reports.
type WorkReport = OpenApiClientProvider<const(schemas + "wr_0.34.yaml"), PreferAsync=true>

/// Verifications Intelligent Document Processing: documents and webhooks.
type Idp = OpenApiClientProvider<const(schemas + "idp_api_endpoints_1.0.yaml"), PreferAsync=true>

/// PowerCurve Customer Acquisition decisioning and data.
type PowerCurve = OpenApiClientProvider<const(schemas + "swagger_pccadecisioninganddata_v1.21.yml"), PreferAsync=true>

/// Bank Account Verify (consumer): whether a sort code and account number are real and belong to the person.
type BankAccountVerify = OpenApiClientProvider<const(schemas + "bavconsumer_20250813_swagger_0.yaml"), PreferAsync=true>

/// International Payments Verification: whether an international account's details are valid before paying it.
type InternationalPayments = OpenApiClientProvider<const(schemas + "intlpaymentsswagger_28.yaml"), PreferAsync=true>

/// Know Your Business: company search, key parties, sectors, locations, geographies, AML flags, PEP and sanctions data on companies and people.
type Kyb = OpenApiClientProvider<const(schemas + "experian-bi-kyb_api_0_0.yaml"), PreferAsync=true>

/// CrossCore age threshold and date of birth checks. Date of Birth Verify has no spec of its own; it is the same call with its own request type.
type AgeAndDateOfBirth = OpenApiClientProvider<const(schemas + "ATV_20230403_Swagger.yaml"), PreferAsync=true>

// Any region

/// Client Gateway: deploying and running what a client has on Experian's Ascend platform, not credit or identity data.
type ClientGateway = OpenApiClientProvider<const(schemas + "client-gateway-2.6.0.yml"), PreferAsync=true>

// Experian's data validation products, on its own API host and token, for most countries

/// Address Validation: searching, validating, formatting and enriching addresses.
type AddressValidation = OpenApiClientProvider<const(schemas + "aperture-addressvalidation.json"), PreferAsync=true>

/// Email Validation v2: whether a mailbox exists and can be delivered to.
type EmailValidation = OpenApiClientProvider<const(schemas + "aperture-emailvalidationv2.json"), PreferAsync=true>

/// Phone Validation: whether a number is live, and its type and operator.
type PhoneValidation = OpenApiClientProvider<const(schemas + "aperture-phonevalidation.json"), PreferAsync=true>

// The United States

/// Credit Profile: credit report, employment insight, healthcare, automotive, TEC Connect and Connect Check.
type CreditProfile = OpenApiClientProvider<const(schemas + "credit-profile-product-apis-oas-3.1-v-2.0.84.yaml"), PreferAsync=true>

/// Instant Credit: advanced and custom stand-alone instant credit decisions.
type InstantCredit = OpenApiClientProvider<const(schemas + "instant-credit-cp-v-openapi3.2.0.52.yaml"), PreferAsync=true>

/// eCBSV: whether a Social Security number, name and date of birth match the Social Security Administration's records, with the person's consent.
type SsnVerification = OpenApiClientProvider<const(schemas + "experianecbsv_noauth.yaml"), PreferAsync=true>

// The United States, Experian Verify: income and employment. Each report has its own types; Instant reports share one call

type VerifyCore = OpenApiClientProvider<const(schemas + "expverify-core-v2-1.23.24.yaml"), PreferAsync=true>
type VerifyPlus = OpenApiClientProvider<const(schemas + "expverify-plus-v2-1.23.24.yaml"), PreferAsync=true>
type VerifyPremium = OpenApiClientProvider<const(schemas + "expverify-premium-v2-1.23.24.yaml"), PreferAsync=true>
type VerifyPremiumPrequal = OpenApiClientProvider<const(schemas + "expverify-mortgageprequal-v2-1.23.24.yaml"), PreferAsync=true>
type VerifyEmployment = OpenApiClientProvider<const(schemas + "expverify-employment-v2-5.31.24_1.yaml"), PreferAsync=true>
type VerifyGovernment = OpenApiClientProvider<const(schemas + "expverify-gov-updated-1.23.24.yaml"), PreferAsync=true>
type ReverifyVoe = OpenApiClientProvider<const(schemas + "expverify-reverify-voe-v2-1.23.24.yaml"), PreferAsync=true>
type ReverifyVoie = OpenApiClientProvider<const(schemas + "expverify-reverify-voie-v2-1.23.24.yaml"), PreferAsync=true>

// The United States, Experian Verify with the person's permission, on a page the client hosts: widget, status, report

type VerifyCpdCore = OpenApiClientProvider<const(schemas + "expn-verify-cpd-core-1.23.24.yaml"), PreferAsync=true>
type VerifyCpdPlus = OpenApiClientProvider<const(schemas + "expn-verify-cpd-plus-1.23.24.yaml"), PreferAsync=true>
type VerifyCpdPremium = OpenApiClientProvider<const(schemas + "expn_verify_cpd_premium-1.23.24.yaml"), PreferAsync=true>
type ReverifyCpdVoe = OpenApiClientProvider<const(schemas + "expn-verify-cpd-rvoe-1.23.24.yaml"), PreferAsync=true>
type ReverifyCpdVoie = OpenApiClientProvider<const(schemas + "expn_verify_cpd_rvoie-1.23.24.yaml"), PreferAsync=true>

// The Netherlands and Spain

/// EMEA Web Data Insights: a credit risk score for a small business from its digital footprint.
type WebData = OpenApiClientProvider<const(schemas + "aces-webdata-webscore-v2-swagger-apiproxy-gdp.yaml"), PreferAsync=true>

// Brazil, through Serasa Experian: scores and checks from open banking data

type OpenBankingScore = OpenApiClientProvider<const(schemas + "cs-openbanking-scoring-br_2.yaml"), PreferAsync=true>
type IncomeVerifications = OpenApiClientProvider<const(schemas + "cs-openbanking-income_verification-br_2.yaml"), PreferAsync=true>
type TransactionCategorization = OpenApiClientProvider<const(schemas + "cs-openbanking-categorization-br_2.yaml"), PreferAsync=true>
type AffordabilityChecks = OpenApiClientProvider<const(schemas + "cs-openbanking-affordability-check-br_2.yaml"), PreferAsync=true>
