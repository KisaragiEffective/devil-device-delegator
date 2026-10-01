# P1 TLS feasibility: .NET certificate pinning

## Scope and candidate identity

`windows/tests/Ddd.Windows.Tests/TlsFeasibilityTests.cs` exercises actual .NET 10 `SslStream` client/server handshakes over loopback TCP, using the existing xUnit 2 project and no additional packages. This is a test-only feasibility probe, not production transport or a DDD protocol implementation.

**Proposal: the QR identity fingerprint is SHA-256 of the exact leaf certificate DER bytes.** In .NET this is `SHA256.HashData(certificate.RawData)`; the peer callback hashes `GetRawCertData()` and compares with `CryptographicOperations.FixedTimeEquals`. This is not an SPKI/public-key pin, an x963 key representation, or a textual PEM hash. Reissuing a certificate, even with the same key, changes this identity if its DER changes. QR encoding and lifecycle remain to be specified.

Each exchange creates an in-memory, self-signed certificate with a fresh ECDSA P-256 **TLS-only** key, digital-signature usage, server-authentication EKU, and localhost SAN. The key is separate from any Git signing identity. No production key, private-key file, session token, command-line secret, or secret logging is involved; certificate and key objects are disposed after the server task completes.

The pinned callback deliberately uses exact identity equality instead of public-CA chain trust. A self-signed chain is expected; accepting its known pin does not mean accepting arbitrary self-signed certificates. Null certificates and mismatched pins are rejected, and no validation callback returns unconditional success. This rejects **unknown identities**, rather than requiring a public CA. The probe does not claim independent certificate expiry/revocation/name-policy enforcement; the configured target name is localhost and the pin is the authentication rule. Strict mTLS is not implemented.

## Test evidence and boundaries

The three tests cover:

- **Correct pin:** TLS authentication succeeds and the server receives exactly the test application bytes.
- **Mismatched pin:** the callback actually runs, client authentication fails, the post-authentication write is never reached, and the server receives zero application bytes. The server's handshake/read failure is observed and asserted; TLS backend/version differences may expose the rejection at either stage.
- **Handshake timeout:** an accepted TCP peer deliberately sends no TLS response. Client authentication is cancelled after one second, before the eight-second test deadline, and the stream remains unauthenticated.

TCP connect/accept, authentication, and reads/writes are bounded by an eight-second cancellation deadline. Listeners and sockets are closed on every exit path; the exchange's server task is awaited in cleanup before its certificate/key are disposed. The silent-peer test has no background server task. TLS 1.2/1.3 are allowed; this does not independently demonstrate both versions on every OS/backend.

Local validation: the focused command below built successfully and the inner test runner reported **3 passed, 0 failed, 0 skipped** on Linux with SDK 10.0.301 / runtime 10.0.9 (approximately one second). The shell wrapper subsequently returned code 2 with `Cannot set tty process group`; that wrapper error is separate from the successful test-runner result. This verifies local **.NET** behavior, not the Windows TLS backend. Windows CI validation and iOS interoperability are separate evidence requirements.

## iOS coordination: pending

[`ios-feasibility.md`](ios-feasibility.md) identifies Network.framework (`NWProtocolTLS.Options`, `sec_protocol_options_set_verify_block`) as a candidate and already prefers exact leaf-DER SHA-256. That remains a proposal: no iOS Network.framework handshake or cross-platform interoperability is verified here. Its verification block must extract the peer leaf DER via Security APIs, compare the agreed pin, and complete verification only for the intended identity, including explicit self-signed pinned-trust handling.

Before production, verify on iOS/device and Windows: correct/mismatched and missing identities, replaced certificates (including same-key reissuance), TLS negotiation, cancellation/connection loss, and LAN permission/addressing behavior. A QR bearer token or real Git payload must only be sent after authenticated TLS. Token replay/expiry, framing/CBOR, session lifecycle, user approval, and Git signing are outside these tests.

## Reproduction

From the repository root, with dependencies already restored:

```sh
DOTNET_CLI_HOME="$PWD/artifacts/dotnet-home" NUGET_PACKAGES="$PWD/artifacts/nuget" dotnet test windows/tests/Ddd.Windows.Tests/Ddd.Windows.Tests.csproj --no-restore --filter FullyQualifiedName~TlsFeasibilityTests -m:1 -nr:false -p:UseSharedCompilation=false --tl:off
```

Use the inner test runner's result as evidence in environments where the shell wrapper returns code 2 with a TTY/process-group error even after a successful inner command. Do not interpret that wrapper status alone as a test failure or claim Windows/iOS execution from a Linux result.
