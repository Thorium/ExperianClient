module WebhookTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open Experian

[<Literal>]
let private key = "3f2504e0-4f89-11d3-9a0c-0305e82c3301"
let private body = Encoding.UTF8.GetBytes """{"eventType":"DocumentProcessed","customerApplicationId":"A-1","partyId":1}"""

/// The signature computed independently of Webhook.signature.
let private expected =
    use hmac = new HMACSHA256(Encoding.UTF8.GetBytes key)
    Convert.ToBase64String(hmac.ComputeHash body)

[<Fact>]
let ``signature is the base64 HMAC-SHA256 of the body keyed with the shared key`` () =
    Assert.Equal(expected, Webhook.signature key body)

[<Fact>]
let ``a matching header verifies, surrounding whitespace allowed`` () =
    Assert.True(Webhook.verify key body expected)
    Assert.True(Webhook.verify key body $" {expected} ")

[<Fact>]
let ``a changed body, another key or a missing header does not verify`` () =
    let tampered = Array.copy body
    tampered[tampered.Length - 2] <- byte '2'
    Assert.False(Webhook.verify key tampered expected)
    Assert.False(Webhook.verify "another-key" body expected)
    Assert.False(Webhook.verify key body "")
    Assert.False(Webhook.verify key body null)
    Assert.False(Webhook.verify key body (expected.Substring 1))
