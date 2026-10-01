# P0 / P1 status and implementation gates

This milestone implements executable compatibility tests and feasibility probes. It does **not** claim all platform/device gates have passed. Final protocol constants belong in the future `protocol/SPEC.md` / CDDL; no speculative v1 wire schema is introduced here.

## P0

| Gate | Implemented evidence | Remaining |
|---|---|---|
| Git exact invocation | Real Git → C# adapter → OpenSSH software P-256 → signed commit → ordinary Git verify; normalized argv and exact payload fixtures | Execute Windows CI; observe supported Git for Windows versions |
| AgePonyCore exact API | Actual public package inspected, immutable revision dependency, software SSHSIG and external enclave assembly probes | Run macOS Swift Debug/Release tests and native app build |
| Key persistence | Explicit creation, Keychain storage, enclave reconstruction, public-key consistency check, no silent replacement, update/re-sign test matrix | Real iPhone; Personal Team CI provisioning/install proof; record Bundle ID/Team/access-group behavior |
| Payload parser boundary | Unsigned Git body fixture and SSHSIG API/preimage boundary documented and tested in Git interop | Production parser + trusted UI are Phase 5, not implemented |

See [Git contract](git-compatibility.md) and [iOS findings](ios-feasibility.md). The inspected package exports SSHSIG primitives but the upstream Secure Enclave persistence service is in its app, not in the package. DDD therefore uses native CryptoKit/Security/LocalAuthentication glue for its disposable device probe; it does not vendor the app's service or implement crypto primitives.

## P1 decisions and proposals

| Topic | Current choice/probe | Status / follow-up |
|---|---|---|
| LocalAuthentication | Probe uses fresh authentication context per explicitly confirmed signature; user-presence protected enclave key | Device UX, fallback policy and background/auth race need real-device checks. Session unlock is not yet implemented |
| TLS API | .NET `SslStream`, in-memory ephemeral P-256 TLS identity separate from Git key, SHA-256 of exact leaf certificate DER | Local .NET correct/wrong pin and deadline tests pass. iOS Network.framework + Windows cross-platform handshake pending; no product transport yet |
| CBOR C# | `System.Formats.Cbor` 10.0.0 in tests only, Strict mode; binary roundtrip and malformed/duplicate-key probes | Feasible candidate, not production codec. Future codec must bound input, nesting, collection sizes, field lengths, reject trailing data and enforce semantic limits |
| CBOR Swift | Upstream's FIDO CBOR decoder inspected but not adopted for hostile DDD input | Select a bounded consumer-compatible implementation and run shared fixtures before adopting CBOR as final wire format |
| Session model | Start with 1 bootstrap = 1 request, outstanding requests = 1 | Proposal; sequential Git invocations stay independent. No persistent process/pairing/batch approval added |
| Lifetime | Production adapter currently has a two-minute total deadline; probes have shorter bounded operation deadlines | QR/connect/approval expiry values must be fixed in protocol after device UX measurements |
| Payload limit | Provisional 1 MiB cap, tested at limit and one byte beyond; signature response cap 16 KiB | These are milestone adapter bounds, not finalized protocol constants. Measure real data before freezing v1 |
| Packaging | Production-only self-contained `win-x64` directory publish in CI | First candidate is zip/directory bundle. Single-file and machine-wide install are not required |

Single-use token consumption, request IDs, replay handling, framing and final metadata/QR limits are Phase 2/4 protocol work. The feasibility probes do not provide authenticated LAN sessions or authorize requests.

## Stop / go gates

1. Require actual Windows Git compatibility results before declaring the Windows contract confirmed.
2. Require macOS compilation and AgePony/OpenSSH/Git interoperability before replacing the test backend with the iPhone signer.
3. Require a real signed/installable app and routine-refresh key persistence evidence before relying on daily IPA refresh. An unsigned CI app is compile evidence only.
4. If Personal Team automation or dependency consumption fails, document evidence and decide a supported alternative; do not silently introduce a paid account assumption, vendor upstream, or rotate the identity.
5. Do not expose remote signing until parsing, trusted display, per-request approval, session authentication and failure-path tests are complete.

## Local validation boundary

The development host is Linux with .NET/Git/OpenSSH, but no Swift/Xcode or connected test iPhone. Final local .NET validation: **30 passed, 0 failed, 0 skipped in both Debug and Release**. This includes real Git/OpenSSH interoperability, TLS and CBOR feasibility tests. The Debug TRX is in ignored `artifacts/test-results/`. A production-only self-contained `win-x64` cross-publish succeeded and contains no `Ddd.CompatSigner.dll`; running that executable on Windows remains a CI/device check. macOS/Windows CI files have been added, not executed by this editor. Real-device results in the iOS matrix remain unverified.

This environment's shell wrapper prints `Cannot set tty process group` and returns 2 even after successful subprocesses. Final validation used ordinary shell commands, not a Python wrapper: `dotnet ... && printf ...` emitted success markers only after the .NET process returned zero. Records use these markers and the actual test-runner summary; the subsequent shell-wrapper error is not represented as a clean overall command exit.
