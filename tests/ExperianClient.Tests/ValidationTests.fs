/// Address, Email and Phone Validation against a stand-in, and each spec's own examples read and written.
module ValidationTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Experian
open FakeExperian

let private answering (body: string) =
    new FakeExperianHandler(fun _ -> 200, body)

[<Fact>]
let ``an email check sends the token in its header and signs in nowhere`` () : Task =
    task {
        let handler =
            answering
                """{"result":{"email":"a@example.com","confidence":"verified","verbose_output":"verified","did_you_mean":["a@example.org"]},"metadata":{"domain_detail":{"type":"business"}}}"""

        let client = ApertureClient("the-token", handler)

        let options =
            { Api.ValidationOptions.Default with
                ReferenceId = ValueSome "ref-1"
                AddMetadata = ValueSome true
            }

        let! result =
            Api.EmailValidation.validateAsync client "a@example.com" options FakeExperian.ct

        let answer = expectOk result
        Assert.Equal(Some "verified", answer.Result.Confidence)
        Assert.Equal<string[]>([| "a@example.org" |], answer.Result.DidYouMean)
        let call = handler.Calls |> List.exactlyOne
        Assert.Equal("https://api.experianaperture.io/email/validate/v2", call.Uri)
        Assert.Equal("the-token", call.Headers["Auth-Token"])
        Assert.False(call.Headers.ContainsKey "Authorization")
        Assert.Equal("ref-1", call.Headers["Reference-Id"])
        Assert.Equal("true", call.Headers["Add-Metadata"])
        Assert.Equal("application/json", call.Headers["Content-Type"])
        Assert.Contains("a@example.com", call.Body)
    }

[<Fact>]
let ``a phone check reads the number's type`` () : Task =
    task {
        let handler =
            answering
                """{"result":{"number":"+447700900000","phone_type":"Mobile","confidence":"Teleservice not provisioned"}}"""

        let client = ApertureClient("the-token", handler)

        let request =
            PhoneValidation.PhoneValidationV2Request("+447700900000", None, None, Some "GBR", None, None, null)

        let! result =
            Api.PhoneValidation.validateAsync client request Api.ValidationOptions.Default FakeExperian.ct

        let answer = expectOk result
        Assert.Equal(Some "Mobile", answer.Result.PhoneType)
        Assert.Equal(Some "Teleservice not provisioned", answer.Result.Confidence)
        Assert.Equal("https://api.experianaperture.io/phone/validate/v2", (handler.Calls |> List.exactlyOne).Uri)
    }

[<Fact>]
let ``an address key stays one path segment, and a refused token is an API error`` () : Task =
    task {
        let handler =
            new FakeExperianHandler(fun _ -> 401, """{"error":{"title":"Unauthorized"}}""")

        let client = ApertureClient("wrong", handler)

        let options =
            { Api.AddressValidation.Options.Default with
                AddComponents = ValueSome true
            }

        let! result =
            Api.AddressValidation.formatAsync client "a/b?c" options FakeExperian.ct

        match result with
        | Error(ApiError(401, body)) -> Assert.Contains("Unauthorized", body)
        | other -> failwith $"Unexpected: {other}"

        let call = handler.Calls |> List.exactlyOne
        Assert.Equal("https://api.experianaperture.io/address/format/v1/a%2Fb%3Fc", call.Uri)
        Assert.Equal("true", call.Headers["Add-Components"])
        let! refused = Api.AddressValidation.formatAsync client ".." options FakeExperian.ct

        Assert.True(
            match refused with
            | Error(InvalidRequest _) -> true
            | _ -> false
        )
    }

/// The paths of the values in JSON, without nulls and empty objects, which nobody can miss.
let private fieldsOf (json: string) =
    let rec walk (prefix: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            [
                for property in element.EnumerateObject() do
                    yield! walk $"{prefix}.{property.Name}" property.Value
            ]
        | JsonValueKind.Array ->
            [
                for item in element.EnumerateArray() do
                    yield! walk prefix item
            ]
        | JsonValueKind.Null -> []
        | _ -> [ prefix ]

    use document = JsonDocument.Parse json
    walk "" document.RootElement |> Set.ofList

/// Every example under one JSON content, as text.
let private examplesOf (content: JsonElement) =
    [
        match content.TryGetProperty "example" with
        | true, example -> yield example.GetRawText()
        | _ -> ()
        match content.TryGetProperty "examples" with
        | true, examples ->
            for example in examples.EnumerateObject() do
                match example.Value.TryGetProperty "value" with
                | true, value -> yield value.GetRawText()
                | _ -> ()
        | _ -> ()
    ]

let private specOf (name: string) =
    let path =
        IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "src", "ExperianClient", "Schemas", name)

    JsonDocument.Parse(IO.File.ReadAllText path).RootElement

/// What each example loses when read into its generated type and written back.
let private lostFields
    (spec: JsonElement)
    (path: string)
    (verb: string)
    (requestType: Type)
    (responseType: Type)
    (read: string * Type -> obj)
    (write: obj -> string)
    =
    let operation = spec.GetProperty("paths").GetProperty(path).GetProperty verb

    let check (kind: string) (target: Type) (content: JsonElement) =
        [
            for example in examplesOf content do
                let written = write (read (example, target))

                for field in Set.difference (fieldsOf example) (fieldsOf written) do
                    yield $"{verb} {path} {kind}{field}"
        ]

    [
        if not (isNull requestType) then
            match operation.TryGetProperty "requestBody" with
            | true, body ->
                yield! check "request" requestType (body.GetProperty("content").GetProperty "application/json")
            | _ -> ()
        match operation.GetProperty("responses").GetProperty("200").TryGetProperty "content" with
        | true, content -> yield! check "response" responseType (content.GetProperty "application/json")
        | _ -> ()
    ]
    |> List.distinct

/// Experian's examples can hold fields its schemas leave out, which generated types would drop without a word.
[<Fact>]
let ``every example in the validation specs keeps its fields`` () =
    let client = ApertureClient("t", answering "{}")
    let address = specOf "aperture-addressvalidation.json"

    let onAddress path verb (request: Type) (response: Type) =
        lostFields
            address
            path
            verb
            request
            response
            (fun (text, target) -> client.Address.Deserialize(text, target))
            client.Address.Serialize

    let lost =
        [
            yield! onAddress "/address/datasets/v1" "get" null typeof<AddressValidation.AddressDatasetsV1Response>
            yield!
                onAddress
                    "/address/format/v1/{global_address_key}"
                    "get"
                    null
                    typeof<AddressValidation.AddressFormatV1Response>
            yield!
                onAddress
                    "/address/format/v1/{global_address_key}"
                    "post"
                    typeof<AddressValidation.AddressFormatV1Request>
                    typeof<AddressValidation.AddressFormatV1Response>
            yield!
                onAddress
                    "/address/layouts/v1"
                    "post"
                    typeof<AddressValidation.AddressLayoutsV1Request>
                    typeof<AddressValidation.AddressLayoutsV1Response>
            yield!
                onAddress
                    "/address/layouts/v2"
                    "post"
                    typeof<AddressValidation.AddressLayoutsV2CreateRequest>
                    typeof<AddressValidation.AddressLayoutsV2CreateResponse>
            yield! onAddress "/address/layouts/v2" "get" null typeof<AddressValidation.AddressLayoutsV2ListResponse>
            yield!
                onAddress "/address/layouts/v2/{name}" "get" null typeof<AddressValidation.AddressLayoutsV2GetResponse>
            yield!
                onAddress
                    "/address/lookup/v1"
                    "post"
                    typeof<AddressValidation.AddressLookupV1Request>
                    typeof<AddressValidation.AddressSearchV1Response>
            yield!
                onAddress
                    "/address/lookup/v2"
                    "post"
                    typeof<AddressValidation.AddressLookupV2Request>
                    typeof<AddressValidation.AddressLookupV2Response>
            yield!
                onAddress
                    "/address/promptsets/v1"
                    "post"
                    typeof<AddressValidation.AddressPromptSetsV1Request>
                    typeof<AddressValidation.AddressPromptSetsV1Response>
            yield!
                onAddress
                    "/address/search/v1"
                    "post"
                    typeof<AddressValidation.AddressSearchV1Request>
                    typeof<AddressValidation.AddressSearchV1Response>
            yield!
                onAddress
                    "/address/suggestions/stepin/v1/{global_address_key}"
                    "get"
                    null
                    typeof<AddressValidation.AddressSuggestionsV1Response>
            yield!
                onAddress
                    "/address/suggestions/refine/v1/{key}"
                    "post"
                    typeof<AddressValidation.AddressSuggestionsV1Request>
                    typeof<AddressValidation.AddressSuggestionsV1Response>
            yield!
                onAddress
                    "/address/suggestions/format/v1"
                    "post"
                    typeof<AddressValidation.AddressSuggestionsFormatV1Request>
                    typeof<AddressValidation.AddressSuggestionsFormatV1Response>
            yield!
                onAddress
                    "/address/validate/v1"
                    "post"
                    typeof<AddressValidation.AddressValidateV1Request>
                    typeof<AddressValidation.AddressValidateV1Response>
            yield!
                lostFields
                    (specOf "aperture-emailvalidationv2.json")
                    "/email/validate/v2"
                    "post"
                    typeof<EmailValidation.EmailValidationV2Request>
                    typeof<EmailValidation.EmailValidationV2Response>
                    (fun (text, target) -> client.Email.Deserialize(text, target))
                    client.Email.Serialize
            yield!
                lostFields
                    (specOf "aperture-phonevalidation.json")
                    "/phone/validate/v2"
                    "post"
                    typeof<PhoneValidation.PhoneValidationV2Request>
                    typeof<PhoneValidation.PhoneValidationV2Response>
                    (fun (text, target) -> client.Phone.Deserialize(text, target))
                    client.Phone.Serialize
        ]

    Assert.Equal<string>("", String.Join("\n", lost))
