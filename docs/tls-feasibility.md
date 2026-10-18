# P1 TLS feasibility: .NET certificate pinning

## Scope and candidate identity

`windows/tests/Ddd.Windows.Tests/TlsFeasibilityTests.cs` exercises actual .NET 10 `SslStream` client/server handshakes over loopback TCP, using the existing xUnit 2 project and no additional packages. This is a test-only feasibility probe, not production transport or a DDD protocol implementation.

**Proposal: the QR identity fingerprint is SHA-256 of the exact leaf certificate DER bytes.** In .NET this is `SHA256.HashData(certificate.RawData)`; the peer callback hashes `GetRawCertData()` and compares with `CryptographicOperations.FixedTimeEquals`. This is not an SPKI/public-key pin, an x963 key representation, or a textual PEM hash. Reissuing a certificate, even with the same key, changes this identity if its DER changes. QR encoding and lifecycle remain to be specified.

Each exchange creates a short-lived, self-signed certificate with a fresh ECDSA P-256 **test-only TLS key**, digital-signature usage, server-authentication EKU, and localhost SAN. The key is separate from any Git signing identity. Non-Windows platforms use the original in-memory `ECDsa.Create` / `CertificateRequest.CreateSelfSigned` certificate. On Windows only, the test exports that certificate to an in-memory PFX and reimports it with .NET 10 `X509CertificateLoader.LoadPkcs12` and `X509KeyStorageFlags.DefaultKeySet`, **not** `EphemeralKeySet`, for Schannel compatibility. This permits OS-managed temporary private-key storage: Windows is an exception to a memory-only key claim. The test does not request `PersistKeySet` or write a PFX file; the exported PFX bytes are zeroed in `finally`, including when import fails. The imported certificate, original certificate, and original key handle are all disposed after the bounded server cleanup. No production/Git key, session token, command-line secret, or secret logging is involved. This is a test certificate-lifetime workaround, not a production key-storage policy or a weakening of the exact-DER pin.

The pinned callback deliberately uses exact identity equality instead of public-CA chain trust. A self-signed chain is expected; accepting its known pin does not mean accepting arbitrary self-signed certificates. Null certificates and mismatched pins are rejected, and no validation callback returns unconditional success. This rejects **unknown identities**, rather than requiring a public CA. The probe does not claim independent certificate expiry/revocation/name-policy enforcement; the configured target name is localhost and the pin is the authentication rule. Strict mTLS is not implemented.

## Test evidence and boundaries

The three tests cover:

- **Correct pin:** TLS authentication succeeds and the server receives exactly the test application bytes.
- **Mismatched pin:** the callback actually runs, client authentication fails, the post-authentication write is never reached, and the server receives zero application bytes. The server's handshake/read failure is observed and asserted; TLS backend/version differences may expose the rejection at either stage.
- **Handshake timeout:** an accepted TCP peer deliberately sends no TLS response. Client authentication is cancelled after one second, before the eight-second test deadline, and the stream remains unauthenticated.

TCP connect/accept, authentication, and reads/writes are bounded by an eight-second cancellation deadline. Cleanup cancels the deadline, closes listeners and sockets, and awaits the exchange's server task with a separate eight-second `WaitAsync` bound before disposing certificates/keys; a cleanup timeout fails the test rather than waiting indefinitely. The silent-peer test has no background server task. Both `PinChecked` assertions include full client and server exception `ToString()` output (including inner exceptions and stack traces), so a failure before certificate validation is not hidden behind a bare boolean assertion. TLS 1.2/1.3 are allowed; this does not independently demonstrate both versions on every OS/backend.

Local validation after this change: the focused command below was run directly through the normal shell, without a Python wrapper, and built successfully. The inner .NET 10 test runner reported **3 passed, 0 failed, 0 skipped** on Linux (approximately one second). The shell subsequently returned code 2 with `/bin/sh: 2: Cannot set tty process group (No such process)`; that shell error is separate from the successful test-runner result. This validates the unchanged non-Windows certificate path, not the Windows import/Schannel path. **Windows validation of this change is pending**, and iOS interoperability remains unverified.

### Windows failure provenance and hypothesis

The supplied CI metadata identifies run `36873826171`, Windows job `110408054374` (`windows-2025`), as failing the pin tests at `Assert.True` with `PinChecked == false`, while Linux passed. The supplied log contains only that assertion, not the underlying client/server exceptions. Detailed CI logs were reported inaccessible with HTTP 403/authentication; these metadata/log details are user-supplied, not independently retrieved here.

Microsoft's official [SslStream troubleshooting guidance: handshake failed with ephemeral keys](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting#handshake-failed-with-ephemeral-keys) documents that Windows Schannel can fail with ephemeral certificate keys and `0x8009030E` ("No credentials are available in the security package"). The original `ECDsa.Create` / `CreateSelfSigned` path makes this a plausible explanation and motivates the Windows-only PFX reimport. **Neither that error code nor a particular inner exception is confirmed for this CI failure.** A new Windows run must verify both pin cases and the timeout test; if it still fails, use the added client/server diagnostics to establish the actual cause. No successful Windows result is claimed.

## iOS coordination: pending

[`ios-feasibility.md`](ios-feasibility.md) identifies Network.framework (`NWProtocolTLS.Options`, `sec_protocol_options_set_verify_block`) as a candidate and already prefers exact leaf-DER SHA-256. That remains a proposal: no iOS Network.framework handshake or cross-platform interoperability is verified here. Its verification block must extract the peer leaf DER via Security APIs, compare the agreed pin, and complete verification only for the intended identity, including explicit self-signed pinned-trust handling.

Before production, verify on iOS/device and Windows: correct/mismatched and missing identities, replaced certificates (including same-key reissuance), TLS negotiation, cancellation/connection loss, and LAN permission/addressing behavior. A QR bearer token or real Git payload must only be sent after authenticated TLS. Token replay/expiry, framing/CBOR, session lifecycle, user approval, and Git signing are outside these tests.

## Reproduction

From the repository root, with dependencies already restored:

```sh
DOTNET_CLI_HOME="$PWD/artifacts/dotnet-home" NUGET_PACKAGES="$PWD/artifacts/nuget" dotnet test windows/tests/Ddd.Windows.Tests/Ddd.Windows.Tests.csproj --no-restore --filter FullyQualifiedName~TlsFeasibilityTests -m:1 -nr:false -p:UseSharedCompilation=false --tl:off
```

Use the inner test runner's result as evidence in environments where the shell wrapper returns code 2 with a TTY/process-group error even after a successful inner command. Do not interpret that wrapper status alone as a test failure or claim Windows/iOS execution from a Linux result.
