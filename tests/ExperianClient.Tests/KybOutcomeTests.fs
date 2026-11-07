/// KybOutcome: whether a KYB answer can be read as clear. Field names and values come from the KYB spec's own examples.
module KybOutcomeTests

open System.IO
open System.Text.Json.Nodes
open Xunit
open Experian
open FakeExperian

let private markerFields =
    [ "PartialOwnershipFlag"; "FormationAgent"; "SlpLlpOwned"; "NoPsc"; "PscDodging"; "Phoenix"; "CHDefaultAddress"; "ResidentialAddress"
      "ResidentialAddressHighVol"; "MailboxAddress"; "RegisteredAddressChanges"; "DirectorNationalityChanges"; "NoUKDirIncorp"
      "HighRiskDirIncorp"; "NoUKDir"; "HighRiskDir"; "NoUKPsc"; "HighRiskPsc"; "NoUKUbo"; "HighRiskUbo"; "NoUKKp"; "HighRiskKp"
      "MultiJurDirIncorp"; "MultiJurDir"; "DirHighVolCos"; "ConflictingDirCoR"; "ConflictingDirNat"; "ConflictingDirDoB"
      "ConsistentOverPerformance"; "FastMoneyMovement"; "LowAverageBalance"; "RoundTransactionsLatestMonth"; "RoundTransactionsLatestYear"
      "RoundTransactionsFull"; "MisalignedInflows"; "MultipleCurrentAccounts"; "MultipleBanks"; "MultipleBanksNewIncorp"; "TradingWhilstDormant" ]

let private cleanLexisNexis =
    """{"LNPep":"No","LNSanction":"No","LNEnforcement":"No","LNSoe":"No","LNRca":"No","LNAdverseMedia":"No","LNUncategorisedHit":"No"}"""

let private cleanCrossCore =
    """{"CCRuleID":"U155","CCDecision":"AU01","CCScore":90,"CCAuthenticationAttempted":"TRUE","CrosscoreAttempted":"True"}"""

/// Every check ran and came back clean: one individual who is UBO, PSC and director, and one corporate shareholder.
let private cleanAnswer =
    let markers = markerFields |> List.map (fun field -> $"\"{field}\":\"N\"") |> String.concat ","
    $"""{{"Results":{{"BusinessRef":"99999999","LegalStatus":"L","LnScreeningSuppress":false,"IdVerificationSuppress":false,
        "CompanyInformation":{{"BusinessName":"ABC Ltd","DissolvedFlag":"N","ExceptionFlag":"N","DirectorNumDisqualifications":0,{markers},"Lexisnexis":{cleanLexisNexis}}},
        "KeyParties":[
          {{"ShareholderType":"I","Forename":"Bob","Surname":"Smith","UBOShareholderID":"A123","PSCID":"C123","DirectorURN":"D12345678","DirectorNumberConvictions":0,"UBOFlag":"Y","Lexisnexis":{cleanLexisNexis},"Crosscore":{cleanCrossCore}}},
          {{"ShareholderType":"C","LegalName":"ABC Holdings Ltd","UBOShareholderID":"E456","Lexisnexis":{cleanLexisNexis}}}]}}}}"""

let private cleanPeopleAnswer =
    $"""{{"Results":{{"LnScreeningSuppress":"false","IdVerificationSuppress":"false","ClientPeopleScreeningLexisNexis":{cleanLexisNexis},"ClientPeopleScreeningCrosscore":{cleanCrossCore}}}}}"""

let private company = KybSubject.Company

let private bob =
    KybSubject.KeyParty
        { Name = ValueSome "Bob Smith"
          UboShareholderId = ValueSome "A123"
          PscId = ValueSome "C123"
          DirectorUrn = ValueSome "D12345678" }

let private holdings =
    KybSubject.KeyParty
        { Name = ValueSome "ABC Holdings Ltd"
          UboShareholderId = ValueSome "E456"
          PscId = ValueNone
          DirectorUrn = ValueNone }

let private lexisNexisFields =
    [ "LNPep", KybHitKind.Pep ValueNone
      "LNSanction", KybHitKind.Sanction
      "LNEnforcement", KybHitKind.Enforcement ValueNone
      "LNSoe", KybHitKind.Soe
      "LNRca", KybHitKind.Rca ValueNone
      "LNAdverseMedia", KybHitKind.AdverseMedia ValueNone
      "LNUncategorisedHit", KybHitKind.Uncategorised ]

let private read (json: string) =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")
    (clientWith handler).Kyb.Deserialize(json, typeof<Kyb.KybDataResponse>) :?> Kyb.KybDataResponse

let private readPeople (json: string) =
    use handler = new FakeExperianHandler(fun _ -> 200, "{}")
    (clientWith handler).Kyb.Deserialize(json, typeof<Kyb.KybDataPeopleResponse>) :?> Kyb.KybDataPeopleResponse

let private editedJson (json: string) (edit: JsonObject -> unit) =
    let root = JsonNode.Parse(json).AsObject()
    edit (root["Results"].AsObject())
    root.ToJsonString()

let private set (target: JsonObject) (field: string) (value: string) = target[field] <- JsonValue.Create value
let private child (target: JsonObject) (field: string) = target[field].AsObject()
let private companyOf (results: JsonObject) = child results "CompanyInformation"
let private partyAt (index: int) (results: JsonObject) = results["KeyParties"].AsArray().Item(index).AsObject()
let private bobOf = partyAt 0
let private holdingsOf = partyAt 1
let private lexisNexisOf (block: JsonObject) = child block "Lexisnexis"
let private crossCoreOf (party: JsonObject) = child party "Crosscore"
let private removeField (target: JsonObject) (field: string) = target.Remove field |> ignore

let private outcomeOf edit = KybOutcome.ofResponse (read (editedJson cleanAnswer edit))
let private outcomeFor checks edit = KybOutcome.ofResponseFor checks (read (editedJson cleanAnswer edit))
let private peopleOutcomeOf edit = KybOutcome.ofPeopleResponse (readPeople (editedJson cleanPeopleAnswer edit))

let private hit subject kind = { Subject = subject; Kind = kind }
let private soleHit subject kind = KybOutcome.NeedsReview([ hit subject kind ], [])
let private gaps (found: KybGap list) = KybOutcome.NotScreened found

let private sample (name: string) =
    File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "Samples", name))

[<Fact>]
let ``a KYB answer is clear only with every check run and clean`` () =
    Assert.Equal(KybOutcome.Clear, KybOutcome.ofResponse (read cleanAnswer))

[<Fact>]
let ``a KYB people answer is clear only with every check run and clean`` () =
    Assert.Equal(KybOutcome.Clear, KybOutcome.ofPeopleResponse (readPeople cleanPeopleAnswer))

[<Fact>]
let ``an empty KYB answer is not clear`` () =
    let noResults = gaps [ KybGap.NoResults ]
    Assert.Equal(noResults, KybOutcome.ofResponse (read "{}"))
    Assert.Equal(noResults, KybOutcome.ofResponse null)
    Assert.Equal(noResults, KybOutcome.ofPeopleResponse (readPeople "{}"))
    Assert.Equal(noResults, KybOutcome.ofPeopleResponse null)

[<Fact>]
let ``KYB results with nothing in them name every block missing`` () =
    Assert.Equal(
        gaps [ KybGap.CompanyNotIdentified; KybGap.BlockMissing(company, "CompanyInformation"); KybGap.BlockMissing(company, "KeyParties") ],
        KybOutcome.ofResponse (read """{"Results":{}}"""))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(KybSubject.Person, "ClientPeopleScreeningLexisNexis"); KybGap.BlockMissing(KybSubject.Person, "ClientPeopleScreeningCrosscore") ],
        KybOutcome.ofPeopleResponse (readPeople """{"Results":{}}"""))

/// uk-aml-answer.json holds the spec's example values, as Experian's sandbox answers them.
[<Fact>]
let ``the spec's example answer needs a person, naming whose hit it is`` () =
    match KybOutcome.ofResponse (read (sample "uk-aml-answer.json")) with
    | KybOutcome.NeedsReview(hits, found) ->
        let party =
            KybSubject.KeyParty
                { Name = ValueSome "Bob Smith"
                  UboShareholderId = ValueSome "A123"
                  PscId = ValueSome "C123"
                  DirectorUrn = ValueSome "D12345678" }
        // The spec's example gives neither director count.
        Assert.Equal<KybGap list>(
            [ KybGap.FieldMissing(company, "DirectorNumDisqualifications"); KybGap.FieldMissing(party, "DirectorNumberConvictions") ],
            found)
        for expected in
            [ hit company (KybHitKind.Pep(ValueSome "High"))
              hit company KybHitKind.Sanction
              hit company (KybHitKind.AdverseMedia(ValueSome "High"))
              hit company (KybHitKind.AdverseMarker "Phoenix")
              hit company (KybHitKind.AdverseMarker "HighRiskUbo")
              hit party (KybHitKind.Pep(ValueSome "High"))
              hit party KybHitKind.Sanction
              hit party (KybHitKind.AdverseMedia(ValueSome "High"))
              hit party (KybHitKind.Authentication(NotAuthenticated 0)) ] do
            Assert.Contains(expected, hits)
        Assert.Contains(hits, fun found ->
            match found.Kind with
            | KybHitKind.OwnershipException(ValueSome description) -> description.StartsWith "Foreign company;"
            | _ -> false)
        Assert.DoesNotContain(hit company KybHitKind.Dissolved, hits)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``the sample AML flags answer keeps its hits and names the key parties it lacks`` () =
    match KybOutcome.ofResponse (read (sample "uk-aml-flags-answer.json")) with
    | KybOutcome.NeedsReview(hits, found) ->
        Assert.Equal<KybGap list>([ KybGap.FieldMissing(company, "DirectorNumDisqualifications"); KybGap.BlockMissing(company, "KeyParties") ], found)
        Assert.Contains(hit (KybSubject.Unlinked(ValueSome "Bob Smith")) (KybHitKind.Pep(ValueSome "High")), hits)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``each Lexis Nexis flag on the company is a hit on the company`` () =
    for field, kind in lexisNexisFields do
        Assert.Equal(soleHit company kind, outcomeOf (fun results -> set (lexisNexisOf (companyOf results)) field "Yes"))

[<Fact>]
let ``each Lexis Nexis flag on a key party is a hit on that party`` () =
    for field, kind in lexisNexisFields do
        Assert.Equal(soleHit bob kind, outcomeOf (fun results -> set (lexisNexisOf (bobOf results)) field "Yes"))
        Assert.Equal(soleHit holdings kind, outcomeOf (fun results -> set (lexisNexisOf (holdingsOf results)) field "Yes"))

[<Fact>]
let ``a Lexis Nexis hit carries Experian's risk`` () =
    let withRisk field riskField kind =
        Assert.Equal(
            soleHit bob kind,
            outcomeOf (fun results ->
                let block = (lexisNexisOf (bobOf results))
                set block field "Yes"
                set block riskField "High"))
    withRisk "LNPep" "LNPepRisk" (KybHitKind.Pep(ValueSome "High"))
    withRisk "LNEnforcement" "LNEnforcementRisk" (KybHitKind.Enforcement(ValueSome "High"))
    withRisk "LNRca" "LNRcaRisk" (KybHitKind.Rca(ValueSome "High"))
    withRisk "LNAdverseMedia" "LNAdverseMediaRisk" (KybHitKind.AdverseMedia(ValueSome "High"))

[<Fact>]
let ``a dissolved company needs a person`` () =
    Assert.Equal(soleHit company KybHitKind.Dissolved, outcomeOf (fun results -> set (companyOf results) "DissolvedFlag" "Y"))
    Assert.Equal(soleHit company KybHitKind.Dissolved, outcomeOf (fun results -> set (companyOf results) "DissolutionDate" "2020-12-31"))

[<Fact>]
let ``each AML flag is a hit by its name`` () =
    for field in markerFields do
        Assert.Equal(soleHit company (KybHitKind.AdverseMarker field), outcomeOf (fun results -> set (companyOf results) field "Y"))

[<Fact>]
let ``a UBO structure exception carries its description`` () =
    Assert.Equal(
        soleHit company (KybHitKind.OwnershipException(ValueSome "Circular ownership")),
        outcomeOf (fun results ->
            set (companyOf results) "ExceptionFlag" "Y"
            set (companyOf results) "ExceptionDescription" "Circular ownership"))

[<Fact>]
let ``disqualifications, convictions and FCA action are hits`` () =
    Assert.Equal(
        soleHit company (KybHitKind.AdverseMarker "DirectorNumDisqualifications"),
        outcomeOf (fun results -> (companyOf results)["DirectorNumDisqualifications"] <- JsonValue.Create 1))
    Assert.Equal(
        soleHit bob (KybHitKind.AdverseMarker "DirectorNumberConvictions"),
        outcomeOf (fun results -> (bobOf results)["DirectorNumberConvictions"] <- JsonValue.Create 2))
    Assert.Equal(KybOutcome.Clear, outcomeOf (fun results -> (bobOf results)["DirectorNumberConvictions"] <- JsonValue.Create 0))
    Assert.Equal(
        soleHit company (KybHitKind.AdverseMarker "FcaDisciplinaryActionTaken"),
        outcomeOf (fun results -> results["Matching"] <- JsonNode.Parse """{"Matched":"yes","MatchingDetails":[{"FcaDisciplinaryActionTaken":"Y"}]}"""))

[<Fact>]
let ``CrossCore refusing or referring an individual needs a person`` () =
    let decided code = outcomeOf (fun results -> set (crossCoreOf (bobOf results)) "CCDecision" code)
    Assert.Equal(soleHit bob (KybHitKind.Authentication(NotAuthenticated 0)), decided "NA00")
    Assert.Equal(soleHit bob (KybHitKind.Authentication(Refer 0)), decided "RA00")
    Assert.Equal(KybOutcome.Clear, decided "AU02")

let private row (fields: string) = JsonNode.Parse $"""[{{{fields}}}]"""

[<Fact>]
let ``a Lexis Nexis match is a hit on the party its ids link to, even when the summary says No`` () =
    let linked ids = outcomeOf (fun results -> results["LexisNexisDetails"] <- row $""" {ids},"Sanction":"Yes" """)
    Assert.Equal(soleHit bob KybHitKind.Sanction, linked """ "UBOShareholderId":"A123" """)
    Assert.Equal(soleHit bob KybHitKind.Sanction, linked """ "PSCID":"C123" """)
    Assert.Equal(soleHit bob KybHitKind.Sanction, linked """ "DirectorURN":"D12345678" """)
    Assert.Equal(soleHit holdings KybHitKind.Sanction, linked """ "UBOShareholderId":"E456" """)

[<Fact>]
let ``every flag on a Lexis Nexis match is read`` () =
    let flagged (field: string) risk =
        outcomeOf (fun results -> results["LexisNexisDetails"] <- row $""" "PSCID":"C123","{field}":"Yes","{field}Risk":"{risk}" """)
    Assert.Equal(soleHit bob (KybHitKind.Pep(ValueSome "High")), flagged "Pep" "High")
    Assert.Equal(soleHit bob (KybHitKind.Enforcement(ValueSome "High")), flagged "Enforcement" "High")
    Assert.Equal(soleHit bob (KybHitKind.Rca(ValueSome "High")), flagged "Rca" "High")
    Assert.Equal(soleHit bob (KybHitKind.AdverseMedia(ValueSome "High")), flagged "AdverseMedia" "High")
    Assert.Equal(soleHit bob KybHitKind.Soe, flagged "Soe" "")
    Assert.Equal(soleHit bob KybHitKind.Uncategorised, flagged "UncategorisedHit" "")

[<Fact>]
let ``a Lexis Nexis match without a flag or a link still needs a person`` () =
    Assert.Equal(
        soleHit bob KybHitKind.Uncategorised,
        outcomeOf (fun results -> results["LexisNexisDetails"] <- row """ "PSCID":"C123","Pep":"No" """))
    Assert.Equal(
        soleHit (KybSubject.Unlinked(ValueSome "Bob Smyth")) KybHitKind.Sanction,
        outcomeOf (fun results -> results["LexisNexisDetails"] <- row """ "InputName":"Bob Smyth","DirectorURN":"Z999","Sanction":"Yes" """))

/// The spec returns the word Unknown for a director without a URN, which identifies nobody.
[<Fact>]
let ``an Unknown director URN links to nobody`` () =
    Assert.Equal(
        soleHit (KybSubject.Unlinked ValueNone) KybHitKind.Sanction,
        outcomeOf (fun results ->
            set (holdingsOf results) "DirectorURN" "Unknown"
            (holdingsOf results)["DirectorNumberConvictions"] <- JsonValue.Create 0
            results["LexisNexisDetails"] <- row """ "DirectorURN":"Unknown","Sanction":"Yes" """))

[<Fact>]
let ``a hit keeps the gaps that came with it`` () =
    Assert.Equal(
        KybOutcome.NeedsReview([ hit company KybHitKind.Sanction ], [ KybGap.ErrorCode "ORCHESTRATOR_API_TIMEOUT" ]),
        outcomeOf (fun results ->
            results["ErrorCodes"] <- JsonNode.Parse """["ORCHESTRATOR_API_TIMEOUT"]"""
            set (lexisNexisOf (companyOf results)) "LNSanction" "Yes"))

[<Fact>]
let ``a repeated hit is named once`` () =
    Assert.Equal(
        soleHit bob KybHitKind.Sanction,
        outcomeOf (fun results ->
            set (lexisNexisOf (bobOf results)) "LNSanction" "Yes"
            results["LexisNexisDetails"] <- row """ "PSCID":"C123","Sanction":"Yes" """))

[<Fact>]
let ``error codes in a 200 are not screened`` () =
    Assert.Equal(
        gaps [ KybGap.ErrorCode "ORCHESTRATOR_API_TIMEOUT"; KybGap.ErrorCode "OTHER" ],
        outcomeOf (fun results -> results["ErrorCodes"] <- JsonNode.Parse """["ORCHESTRATOR_API_TIMEOUT"," OTHER "]"""))
    Assert.Equal(gaps [ KybGap.ErrorCode "" ], outcomeOf (fun results -> results["ErrorCodes"] <- JsonNode.Parse "[null]"))
    Assert.Equal(KybOutcome.Clear, outcomeOf (fun results -> results["ErrorCodes"] <- JsonNode.Parse "[]"))
    Assert.Equal(gaps [ KybGap.ErrorCode "E" ], outcomeOf (fun results -> results["ErrorCodes"] <- JsonNode.Parse """["E","E"]"""))

[<Fact>]
let ``suppressed checks are not screened, as text or as a JSON boolean`` () =
    for field in [ "LnScreeningSuppress"; "IdVerificationSuppress" ] do
        let expected = gaps [ KybGap.Suppressed field ]
        Assert.Equal(expected, outcomeOf (fun results -> set results field "true"))
        Assert.Equal(expected, outcomeOf (fun results -> results[field] <- JsonValue.Create true))
        Assert.Equal(KybOutcome.Clear, outcomeOf (fun results -> set results field "false"))
        Assert.Equal(KybOutcome.Clear, outcomeOf (fun results -> removeField results field))
        Assert.Equal(gaps [ KybGap.UnknownValue(company, field, "maybe") ], outcomeOf (fun results -> set results field "maybe"))

[<Fact>]
let ``a missing block is not screened, naming it`` () =
    Assert.Equal(
        gaps [ KybGap.BlockMissing(company, "CompanyInformation") ],
        outcomeOf (fun results -> removeField results "CompanyInformation"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(company, "Lexisnexis") ],
        outcomeOf (fun results -> removeField (companyOf results) "Lexisnexis"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(company, "KeyParties") ],
        outcomeOf (fun results -> removeField results "KeyParties"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(bob, "Lexisnexis") ],
        outcomeOf (fun results -> removeField (bobOf results) "Lexisnexis"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(holdings, "Lexisnexis") ],
        outcomeOf (fun results -> removeField (holdingsOf results) "Lexisnexis"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(bob, "Crosscore") ],
        outcomeOf (fun results -> removeField (bobOf results) "Crosscore"))

[<Fact>]
let ``empty or null key parties are not screened`` () =
    Assert.Equal(
        gaps [ KybGap.BlockMissing(company, "KeyParties") ],
        outcomeOf (fun results -> results["KeyParties"] <- JsonNode.Parse "[]"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(company, "KeyParties") ],
        outcomeOf (fun results -> (results["KeyParties"].AsArray()).Add(null: JsonNode)))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(company, "KeyParties"); KybGap.NoIndividualKeyParty ],
        outcomeOf (fun results -> results["KeyParties"] <- JsonNode.Parse "[null]"))

[<Fact>]
let ``a missing flag is not screened, naming the field and whose it is`` () =
    for field, _ in lexisNexisFields do
        Assert.Equal(gaps [ KybGap.FieldMissing(company, field) ], outcomeOf (fun results -> removeField (lexisNexisOf (companyOf results)) field))
        Assert.Equal(gaps [ KybGap.FieldMissing(bob, field) ], outcomeOf (fun results -> removeField (lexisNexisOf (bobOf results)) field))
        Assert.Equal(gaps [ KybGap.FieldMissing(bob, field) ], outcomeOf (fun results -> set (lexisNexisOf (bobOf results)) field " "))
    for field in "DissolvedFlag" :: "ExceptionFlag" :: markerFields do
        Assert.Equal(gaps [ KybGap.FieldMissing(company, field) ], outcomeOf (fun results -> removeField (companyOf results) field))

[<Fact>]
let ``a value the spec does not list is not clear`` () =
    Assert.Equal(
        gaps [ KybGap.UnknownValue(bob, "LNPep", "Maybe") ],
        outcomeOf (fun results -> set (lexisNexisOf (bobOf results)) "LNPep" "Maybe"))
    // Lexis Nexis flags are Yes or No: N is not their clean value
    Assert.Equal(
        gaps [ KybGap.UnknownValue(bob, "LNPep", "N") ],
        outcomeOf (fun results -> set (lexisNexisOf (bobOf results)) "LNPep" "N"))
    // AML flags are Y or N: No is not their clean value
    Assert.Equal(
        gaps [ KybGap.UnknownValue(company, "Phoenix", "No") ],
        outcomeOf (fun results -> set (companyOf results) "Phoenix" "No"))
    Assert.Equal(
        gaps [ KybGap.UnknownValue(company, "RoundTransactionsFull", "25") ],
        outcomeOf (fun results -> set (companyOf results) "RoundTransactionsFull" "25"))
    Assert.Equal(
        gaps [ KybGap.UnknownValue(company, "DissolvedFlag", "?") ],
        outcomeOf (fun results -> set (companyOf results) "DissolvedFlag" "?"))

[<Fact>]
let ``a CrossCore decision the spec does not list is not clear`` () =
    let decided code = outcomeOf (fun results -> set (crossCoreOf (bobOf results)) "CCDecision" code)
    Assert.Equal(gaps [ KybGap.UnknownValue(bob, "CCDecision", "AU03") ], decided "AU03")
    Assert.Equal(gaps [ KybGap.UnknownValue(bob, "CCDecision", "PASS") ], decided "PASS")

[<Fact>]
let ``CrossCore not attempted or timed out is not screened`` () =
    let attempted value = outcomeOf (fun results -> set (crossCoreOf (bobOf results)) "CrosscoreAttempted" value)
    for value in [ "Error"; "Timeout"; "False"; "Other" ] do
        Assert.Equal(gaps [ KybGap.CrossCoreNotCompleted(bob, "CrosscoreAttempted", value) ], attempted value)
    let sent value = outcomeOf (fun results -> set (crossCoreOf (bobOf results)) "CCAuthenticationAttempted" value)
    for value in [ "FALSE"; "ERROR" ] do
        Assert.Equal(gaps [ KybGap.CrossCoreNotCompleted(bob, "CCAuthenticationAttempted", value) ], sent value)

[<Fact>]
let ``a failed CrossCore call names the call and not the fields it leaves empty`` () =
    Assert.Equal(
        gaps [ KybGap.CrossCoreNotCompleted(bob, "CrosscoreAttempted", "Timeout") ],
        outcomeOf (fun results -> (bobOf results)["Crosscore"] <- JsonNode.Parse """{"CrosscoreAttempted":"Timeout"}"""))
    Assert.Equal(
        gaps [ KybGap.CrossCoreNotCompleted(bob, "CCAuthenticationAttempted", "FALSE") ],
        outcomeOf (fun results -> (bobOf results)["Crosscore"] <- JsonNode.Parse """{"CrosscoreAttempted":"True","CCAuthenticationAttempted":"FALSE"}"""))

[<Fact>]
let ``a CrossCore block without its fields is not screened`` () =
    for field in [ "CrosscoreAttempted"; "CCAuthenticationAttempted"; "CCDecision" ] do
        Assert.Equal(gaps [ KybGap.FieldMissing(bob, field) ], outcomeOf (fun results -> removeField (crossCoreOf (bobOf results)) field))

[<Fact>]
let ``a corporate key party needs no authentication, but a failed call still counts`` () =
    Assert.Equal(
        KybOutcome.Clear,
        outcomeOf (fun results -> (holdingsOf results)["Crosscore"] <- JsonNode.Parse """{"CCAuthenticationAttempted":"FALSE","CrosscoreAttempted":"True"}"""))
    Assert.Equal(KybOutcome.Clear, outcomeOf (fun results -> set (holdingsOf results) "ShareholderType" "O"))
    Assert.Equal(
        gaps [ KybGap.CrossCoreNotCompleted(holdings, "CrosscoreAttempted", "Timeout") ],
        outcomeOf (fun results -> (holdingsOf results)["Crosscore"] <- JsonNode.Parse """{"CrosscoreAttempted":"Timeout"}"""))
    Assert.Equal(
        gaps [ KybGap.CrossCoreNotCompleted(holdings, "CCAuthenticationAttempted", "ERROR") ],
        outcomeOf (fun results -> (holdingsOf results)["Crosscore"] <- JsonNode.Parse """{"CCAuthenticationAttempted":"ERROR"}"""))

[<Fact>]
let ``a key party of unknown or missing type is authenticated as an individual`` () =
    Assert.Equal(
        gaps [ KybGap.BlockMissing(holdings, "Crosscore") ],
        outcomeOf (fun results -> set (holdingsOf results) "ShareholderType" "X"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(holdings, "Crosscore") ],
        outcomeOf (fun results -> removeField (holdingsOf results) "ShareholderType"))

[<Fact>]
let ``key parties that are all companies leave authentication unrun`` () =
    Assert.Equal(
        gaps [ KybGap.NoIndividualKeyParty ],
        outcomeOf (fun results -> results["KeyParties"].AsArray().RemoveAt 0))

[<Fact>]
let ``a company not found is not screened`` () =
    Assert.Equal(
        gaps [ KybGap.CompanyNotFound "Company not found"; KybGap.BlockMissing(company, "KeyParties") ],
        outcomeOf (fun results ->
            results["CompanyInformation"] <- JsonNode.Parse """{"DataQualityFlag":"Company not found"}"""
            removeField results "KeyParties"))
    Assert.Equal(
        gaps [ KybGap.CompanyNotFound "no" ],
        outcomeOf (fun results -> results["Matching"] <- JsonNode.Parse """{"Matched":"no"}"""))
    Assert.Equal(
        gaps [ KybGap.FieldMissing(company, "Matched") ],
        outcomeOf (fun results -> results["Matching"] <- JsonNode.Parse """{"NumberOfMatches":2}"""))
    Assert.Equal(KybOutcome.Clear, outcomeOf (fun results -> results["Matching"] <- JsonNode.Parse """{"Matched":"yes"}"""))

[<Fact>]
let ``an answer that names no company is not clear`` () =
    let withoutNames (results: JsonObject) =
        removeField results "BusinessRef"
        removeField (companyOf results) "BusinessName"
    Assert.Equal(gaps [ KybGap.CompanyNotIdentified ], outcomeOf withoutNames)
    Assert.Equal(
        gaps [ KybGap.CompanyNotIdentified ],
        outcomeFor [ KybCheck.Screening ] withoutNames)
    Assert.Equal(
        gaps [ KybGap.CompanyNotIdentified ],
        outcomeOf (fun results ->
            withoutNames results
            results["Matching"] <- JsonNode.Parse """{"Matched":"yes"}"""))
    Assert.Equal(
        KybOutcome.Clear,
        outcomeOf (fun results ->
            withoutNames results
            results["Matching"] <- JsonNode.Parse """{"Matched":"yes","BestMatchedId":"12345678"}"""))
    Assert.Equal(KybOutcome.Clear, outcomeOf (fun results -> removeField results "BusinessRef"))

[<Fact>]
let ``a director count absent or a dash is not clear`` () =
    let disqualifications = gaps [ KybGap.FieldMissing(company, "DirectorNumDisqualifications") ]
    let convictions = gaps [ KybGap.FieldMissing(bob, "DirectorNumberConvictions") ]
    Assert.Equal(disqualifications, outcomeOf (fun results -> removeField (companyOf results) "DirectorNumDisqualifications"))
    Assert.Equal(disqualifications, outcomeOf (fun results -> set (companyOf results) "DirectorNumDisqualifications" "-"))
    Assert.Equal(convictions, outcomeOf (fun results -> removeField (bobOf results) "DirectorNumberConvictions"))
    Assert.Equal(convictions, outcomeOf (fun results -> set (bobOf results) "DirectorNumberConvictions" "-"))

[<Fact>]
let ``a director count is expected of directors, and with adverse markers`` () =
    Assert.Equal(
        gaps [ KybGap.FieldMissing(holdings, "DirectorNumberConvictions") ],
        outcomeOf (fun results -> set (holdingsOf results) "DirectorFlag" "Y"))
    Assert.Equal(KybOutcome.Clear, outcomeOf (fun results -> removeField (holdingsOf results) "DirectorNumberConvictions"))
    Assert.Equal(
        KybOutcome.Clear,
        outcomeFor [ KybCheck.Screening ] (fun results ->
            removeField (companyOf results) "DirectorNumDisqualifications"
            removeField (bobOf results) "DirectorNumberConvictions"))

[<Fact>]
let ``a stated risk beside a No flag needs a person`` () =
    for field, kind in
        [ "LNPep", KybHitKind.Pep(ValueSome "High")
          "LNEnforcement", KybHitKind.Enforcement(ValueSome "High")
          "LNRca", KybHitKind.Rca(ValueSome "High")
          "LNAdverseMedia", KybHitKind.AdverseMedia(ValueSome "High") ] do
        Assert.Equal(soleHit bob kind, outcomeOf (fun results -> set (lexisNexisOf (bobOf results)) $"{field}Risk" "High"))
        Assert.Equal(soleHit company kind, outcomeOf (fun results -> set (lexisNexisOf (companyOf results)) $"{field}Risk" "High"))
    Assert.Equal(KybOutcome.Clear, outcomeOf (fun results -> set (lexisNexisOf (bobOf results)) "LNPepRisk" " "))

[<Fact>]
let ``a key party with no name or ids is not clear`` () =
    let nobody =
        KybSubject.KeyParty
            { Name = ValueNone
              UboShareholderId = ValueNone
              PscId = ValueNone
              DirectorUrn = ValueNone }
    Assert.Equal(
        gaps [ KybGap.FieldMissing(nobody, "Name") ],
        outcomeOf (fun results ->
            results["KeyParties"].AsArray().Add(JsonNode.Parse $"""{{"ShareholderType":"C","Lexisnexis":{cleanLexisNexis}}}""")))
    Assert.Equal(
        gaps [ KybGap.FieldMissing(nobody, "Name") ],
        outcomeOf (fun results ->
            results["KeyParties"].AsArray().Add(JsonNode.Parse $"""{{"ShareholderType":"C","LegalName":" ","Lexisnexis":{cleanLexisNexis}}}""")))

[<Fact>]
let ``a null Lexis Nexis match is not clear`` () =
    Assert.Equal(
        gaps [ KybGap.BlockMissing(company, "LexisNexisDetails") ],
        outcomeOf (fun results -> results["LexisNexisDetails"] <- JsonNode.Parse "[null]"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(KybSubject.Person, "LexisNexisDetails") ],
        peopleOutcomeOf (fun results -> results["LexisNexisDetails"] <- JsonNode.Parse "[null]"))
    Assert.Equal(KybOutcome.Clear, outcomeOf (fun results -> results["LexisNexisDetails"] <- JsonNode.Parse "[]"))

[<Fact>]
let ``a match count other than one is not clear`` () =
    let matches (count: int) =
        outcomeOf (fun results -> results["Matching"] <- JsonNode.Parse $"""{{"Matched":"yes","NumberOfMatches":{count}}}""")
    Assert.Equal(gaps [ KybGap.MatchCount 5. ], matches 5)
    Assert.Equal(gaps [ KybGap.MatchCount 0. ], matches 0)
    Assert.Equal(KybOutcome.Clear, matches 1)

[<Fact>]
let ``DataQualityFlag can't hide a hit`` () =
    Assert.Equal(
        KybOutcome.NeedsReview([ hit company KybHitKind.Sanction ], [ KybGap.CompanyNotFound "Company not found" ]),
        outcomeOf (fun results ->
            set (lexisNexisOf (companyOf results)) "LNSanction" "Yes"
            set (companyOf results) "DataQualityFlag" "Company not found"))
    Assert.Equal(
        KybOutcome.NeedsReview([ hit company (KybHitKind.AdverseMarker "DirectorNumDisqualifications") ], [ KybGap.CompanyNotFound "x" ]),
        outcomeOf (fun results ->
            (companyOf results)["DirectorNumDisqualifications"] <- JsonValue.Create 1
            set (companyOf results) "DataQualityFlag" "x"))

[<Fact>]
let ``checks not asked for may be absent`` () =
    Assert.Equal(
        KybOutcome.Clear,
        outcomeFor [ KybCheck.Screening ] (fun results ->
            removeField (bobOf results) "Crosscore"
            removeField (companyOf results) "Phoenix"
            removeField (companyOf results) "ExceptionFlag"))
    Assert.Equal(
        KybOutcome.Clear,
        outcomeFor [ KybCheck.AdverseMarkers ] (fun results ->
            removeField results "KeyParties"
            removeField (companyOf results) "Lexisnexis"))
    Assert.Equal(
        KybOutcome.Clear,
        outcomeFor [ KybCheck.Authentication ] (fun results ->
            removeField results "CompanyInformation"
            removeField (bobOf results) "Lexisnexis"
            removeField (holdingsOf results) "Lexisnexis"))

[<Fact>]
let ``a check asked for may not be absent`` () =
    Assert.Equal(
        gaps [ KybGap.BlockMissing(bob, "Lexisnexis") ],
        outcomeFor [ KybCheck.Screening ] (fun results -> removeField (bobOf results) "Lexisnexis"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(company, "Lexisnexis") ],
        outcomeFor [ KybCheck.Screening ] (fun results -> removeField (companyOf results) "Lexisnexis"))
    Assert.Equal(
        gaps [ KybGap.FieldMissing(company, "Phoenix") ],
        outcomeFor [ KybCheck.AdverseMarkers ] (fun results -> removeField (companyOf results) "Phoenix"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(bob, "Crosscore") ],
        outcomeFor [ KybCheck.Authentication ] (fun results -> removeField (bobOf results) "Crosscore"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(company, "CompanyInformation") ],
        outcomeFor [ KybCheck.Screening ] (fun results -> removeField results "CompanyInformation"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(company, "KeyParties") ],
        outcomeFor [ KybCheck.Authentication ] (fun results -> removeField results "KeyParties"))

[<Fact>]
let ``asking for fewer checks does not hide what the answer shows`` () =
    let screeningOnly = outcomeFor [ KybCheck.Screening ]
    Assert.Equal(
        soleHit bob (KybHitKind.Authentication(NotAuthenticated 0)),
        screeningOnly (fun results -> set (crossCoreOf (bobOf results)) "CCDecision" "NA00"))
    Assert.Equal(
        soleHit company (KybHitKind.AdverseMarker "Phoenix"),
        screeningOnly (fun results -> set (companyOf results) "Phoenix" "Y"))
    Assert.Equal(
        gaps [ KybGap.CrossCoreNotCompleted(bob, "CrosscoreAttempted", "Timeout") ],
        screeningOnly (fun results -> set (crossCoreOf (bobOf results)) "CrosscoreAttempted" "Timeout"))
    Assert.Equal(
        gaps [ KybGap.ErrorCode "E" ],
        screeningOnly (fun results -> results["ErrorCodes"] <- JsonNode.Parse """["E"]"""))
    Assert.Equal(
        gaps [ KybGap.UnknownValue(company, "IdVerificationSuppress", "maybe") ],
        screeningOnly (fun results -> set results "IdVerificationSuppress" "maybe"))

[<Fact>]
let ``suppressing only what was not asked for is let go, and the rest is not`` () =
    let asked check field = outcomeFor [ check ] (fun results -> set results field "true")
    Assert.Equal(KybOutcome.Clear, asked KybCheck.Screening "IdVerificationSuppress")
    Assert.Equal(KybOutcome.Clear, asked KybCheck.Authentication "LnScreeningSuppress")
    Assert.Equal(gaps [ KybGap.Suppressed "LnScreeningSuppress" ], asked KybCheck.Screening "LnScreeningSuppress")
    Assert.Equal(gaps [ KybGap.Suppressed "IdVerificationSuppress" ], asked KybCheck.Authentication "IdVerificationSuppress")

[<Fact>]
let ``asking for no check expects them all`` () =
    Assert.Equal(KybOutcome.ofResponse (read "{\"Results\":{}}"), KybOutcome.ofResponseFor [] (read "{\"Results\":{}}"))
    Assert.Equal(KybOutcome.ofPeopleResponse (readPeople "{\"Results\":{}}"), KybOutcome.ofPeopleResponseFor [] (readPeople "{\"Results\":{}}"))
    Assert.NotEqual(KybOutcome.Clear, KybOutcome.ofResponseFor [] (read "{\"Results\":{}}"))

[<Fact>]
let ``each Lexis Nexis flag on a person is a hit on the person`` () =
    for field, kind in lexisNexisFields do
        Assert.Equal(
            soleHit KybSubject.Person kind,
            peopleOutcomeOf (fun results -> set (child results "ClientPeopleScreeningLexisNexis") field "Yes"))

[<Fact>]
let ``a Lexis Nexis match in a people answer is the person's`` () =
    Assert.Equal(
        soleHit KybSubject.Person KybHitKind.Sanction,
        peopleOutcomeOf (fun results -> results["LexisNexisDetails"] <- row """ "IndividualId":"A123","Sanction":"Yes" """))

[<Fact>]
let ``CrossCore in a people answer is read like a key party's`` () =
    let crossCore (results: JsonObject) = child results "ClientPeopleScreeningCrosscore"
    Assert.Equal(
        soleHit KybSubject.Person (KybHitKind.Authentication(Refer 0)),
        peopleOutcomeOf (fun results -> set (crossCore results) "CCDecision" "RA00"))
    Assert.Equal(
        gaps [ KybGap.CrossCoreNotCompleted(KybSubject.Person, "CrosscoreAttempted", "Timeout") ],
        peopleOutcomeOf (fun results -> set (crossCore results) "CrosscoreAttempted" "Timeout"))

[<Fact>]
let ``a people answer that is incomplete is not screened`` () =
    let person = KybSubject.Person
    Assert.Equal(gaps [ KybGap.ErrorCode "E" ], peopleOutcomeOf (fun results -> results["ErrorCodes"] <- JsonNode.Parse """["E"]"""))
    Assert.Equal(gaps [ KybGap.Suppressed "LnScreeningSuppress" ], peopleOutcomeOf (fun results -> set results "LnScreeningSuppress" "true"))
    Assert.Equal(gaps [ KybGap.Suppressed "IdVerificationSuppress" ], peopleOutcomeOf (fun results -> set results "IdVerificationSuppress" "true"))
    Assert.Equal(gaps [ KybGap.UnknownValue(person, "LnScreeningSuppress", "x") ], peopleOutcomeOf (fun results -> set results "LnScreeningSuppress" "x"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(person, "ClientPeopleScreeningLexisNexis") ],
        peopleOutcomeOf (fun results -> removeField results "ClientPeopleScreeningLexisNexis"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(person, "ClientPeopleScreeningCrosscore") ],
        peopleOutcomeOf (fun results -> removeField results "ClientPeopleScreeningCrosscore"))
    Assert.Equal(
        gaps [ KybGap.FieldMissing(person, "LNSanction") ],
        peopleOutcomeOf (fun results -> removeField (child results "ClientPeopleScreeningLexisNexis") "LNSanction"))

[<Fact>]
let ``a people check not asked for may be absent`` () =
    let people checks edit = KybOutcome.ofPeopleResponseFor checks (readPeople (editedJson cleanPeopleAnswer edit))
    Assert.Equal(KybOutcome.Clear, people [ KybCheck.Screening ] (fun results -> removeField results "ClientPeopleScreeningCrosscore"))
    Assert.Equal(KybOutcome.Clear, people [ KybCheck.Authentication ] (fun results -> removeField results "ClientPeopleScreeningLexisNexis"))
    Assert.Equal(
        gaps [ KybGap.BlockMissing(KybSubject.Person, "ClientPeopleScreeningLexisNexis") ],
        people [ KybCheck.Screening ] (fun results -> removeField results "ClientPeopleScreeningLexisNexis"))
