/// Generated clients, one per Experian UK product; each product has its own types.
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
