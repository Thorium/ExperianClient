/// Verification of signed IDP webhook deliveries.
module Experian.Webhook

open System
open System.Security.Cryptography
open System.Text

/// The header carrying the base64 HMAC-SHA256 of the delivery body.
let [<Literal>] SignatureHeader = "X-HMAC-SHA256-Signature"

/// The base64 HMAC-SHA256 of the body, keyed with the UTF-8 bytes of the shared key as Experian returns it.
let signature (sharedKey: string) (body: byte[]) =
    use hmac = new HMACSHA256(Encoding.UTF8.GetBytes sharedKey)
    Convert.ToBase64String(hmac.ComputeHash body)

/// True when the signature header matches the raw body; compared in constant time. Verify the bytes as received, before parsing.
let verify (sharedKey: string) (body: byte[]) (signatureHeader: string) =
    not (String.IsNullOrWhiteSpace signatureHeader)
    && CryptographicOperations.FixedTimeEquals(
        ReadOnlySpan(Encoding.ASCII.GetBytes(signature sharedKey body)),
        ReadOnlySpan(Encoding.ASCII.GetBytes(signatureHeader.Trim())))
