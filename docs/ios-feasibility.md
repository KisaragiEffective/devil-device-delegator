# iOS P0/P1 feasibility probes

## Status and scope

Implementation prepared on Linux on 2026-10-01. **Swift tests and the unsigned app build have not been run here**: `swift`, `xcodebuild`, and `xcodegen` are absent. The macOS workflow is a validation harness, not evidence of a passing run. No signed IPA, Personal Team provisioning, device persistence, Face ID UX, TLS handshake, or Windows/.NET interoperability has been validated. This workspace also has no Git metadata, so Git diff/status validation is unavailable.

The probes cover development-plan §§9–10, 27, 30, and 38. Git invocation/payload-parser semantics and Windows verification are documented in [Git compatibility](git-compatibility.md). The tests cover both a fixed probe string and the shared exact commit fixture through real Git `commit-tree`/`verify-commit`; they do not implement a production Git parser or establish hosting-service verification. No vendored crypto, custom crypto, production software-key fallback, transport implementation, or batch signing is included.

## P0: actual upstream package/API findings

Inspected public repository: [norsehorse-dev/AgePonyiOS](https://github.com/norsehorse-dev/AgePonyiOS), not a fabricated standalone AgePonyCore repository. Pinned revision: `33731a8d875493a466fc0668d8d9e9aec55f0005` (the `main` tree observed during inspection).

Source references at that revision:

- [Package.swift](https://github.com/norsehorse-dev/AgePonyiOS/blob/33731a8d875493a466fc0668d8d9e9aec55f0005/Package.swift): Swift tools 6.0, iOS 18+, macOS 14+, exported library product `AgePonyCore`, no third-party dependencies; Apple CryptoKit/Security rather than a Linux implementation.
- [SSHSigner.swift](https://github.com/norsehorse-dev/AgePonyiOS/blob/33731a8d875493a466fc0668d8d9e9aec55f0005/Sources/AgePonyCore/Signing/SSHSigner.swift): public `signECDSAP256(message:privateKey:namespace:hash:)` accepts CryptoKit `P256.Signing.PrivateKey`; `assembleECDSAP256(rawRS:publicKeyX963:namespace:hash:)` accepts the enclave's raw signature and public point. No invented enclave wrapper API is used.
- [SSHSig.swift](https://github.com/norsehorse-dev/AgePonyiOS/blob/33731a8d875493a466fc0668d8d9e9aec55f0005/Sources/AgePonyCore/Signing/SSHSig.swift): public `signedData(message:namespace:hash:)`, `ecdsaP256PublicKeyWire(x963Q:)`, parsing and armor helpers. Default namespace is `agepony`; DDD explicitly uses **`git`**. The message hash is SHA-512; CryptoKit P-256 signing hashes the SSHSIG signed-data bytes with SHA-256 internally.
- [SecureEnclaveSigner.swift](https://github.com/norsehorse-dev/AgePonyiOS/blob/33731a8d875493a466fc0668d8d9e9aec55f0005/AgePony/Services/SecureEnclaveSigner.swift): lives in the upstream **app**, outside the package target. Its `StoredIdentity`-based API is not an externally consumable package API. It generates/restores CryptoKit enclave keys from opaque `dataRepresentation` and delegates SSHSIG assembly to the package. Upstream's helper relies on its app biometric gate; it does not establish DDD's approval policy.
- [SSHSigECDSATests.swift](https://github.com/norsehorse-dev/AgePonyiOS/blob/33731a8d875493a466fc0668d8d9e9aec55f0005/Tests/AgePonyCoreTests/SSHSigECDSATests.swift): upstream test source exists; its presence is not a passing test result for DDD.

### External consumption caveat

The package exposes the required public format/signing functions, so DDD references it directly by URL and exact revision. Upstream sets conditional Debug `.unsafeFlags(["-O"])`. Inspection of [SwiftPM 6.0 `Workspace+Manifests.swift`](https://github.com/swiftlang/swift-package-manager/blob/swift-6.0-RELEASE/Sources/Workspace/Workspace%2BManifests.swift) confirms `unsafeAllowedPackages` includes branch/revision-based source checkouts and filesystem dependencies. The exact-revision requirement is therefore intentional and expected to permit this product in SwiftPM 6, not evidence of a confirmed consumer failure. Version/tag-based consumption is a different case; do not switch requirements blindly. [Graph loading](https://github.com/swiftlang/swift-package-manager/blob/swift-6.0-RELEASE/Sources/PackageGraph/ModulesGraph%2BLoading.swift) diagnoses unsafe flags for products not in that allowance, so merely selecting Release is not a reliable workaround for a graph-level rejection of a versioned dependency.

Actual macOS SwiftPM and Xcode consumer results are still pending. CI now runs independent Debug and Release matrix jobs (`fail-fast: false`) and a separate unsigned **Release** app job with no dependency on either test result. Debug failure cannot prevent Release evidence; it still fails its own check and is not hidden. Release avoids applying upstream's Debug optimization flags during the app build, but does not claim to bypass dependency validation. If Xcode rejects the pinned product or compilation fails, retain the failure evidence and request an upstream consumable manifest/release; do not patch, vendor, or replace SSHSIG. No upstream API call changed: software signing still uses `signECDSAP256(message:privateKey:namespace:hash:)` and enclave signing uses `signedData` plus `assembleECDSAP256`.

### Static public-only golden probe

The tests now read the parent's `p256-public-v1.pub`, `p256-signature-v1.sshsig`, and `provenance-v1.json` alongside the exact shared commit payload. They check the provenance SHA-256 and public-only declaration, then require both OpenSSH and the actual public `SSHSigVerifier.verify(message:armoredSignature:expectedNamespace:)` API to accept the golden and reject changed payload/wrong namespace. The verifier returns the embedded signer, **not a trust decision**, so its `publicKeyWire` must equal the Base64 field decoded from the checked-in OpenSSH public-key text line. No custom SSH wire parser is introduced. Upstream's `SSHKey.parsePublicKey` is Ed25519-only at this revision and is not called for P-256. This use of the verifier is limited to trusted, checked-in fixtures; it is not a claim about hostile-input parser hardening.

A separate ephemeral same-key `agepony` signature test uses the correct `agepony` verification namespace throughout: a permissive allowed-signers entry must accept it, while a `namespaces="git"` entry must reject it. Thus allowed-signers namespace policy is tested separately from the original signature-domain-separation negative case.

Linux validation checked the static golden with real OpenSSH: original accepted, tamper/wrong namespace rejected, permissive entry accepted, and an `agepony`-only allowed-signers entry rejected the same valid `git` signature. Provenance hash and Swift 6 project YAML assertions passed. The assertions completed successfully, although the terminal subsequently returned its known TTY error. AgePony verification and the generated same-key namespace test remain unrun without Swift/macOS.

### Software interop probe

From `ios/` on macOS with Xcode/Swift 6:

```sh
swift test
swift test -c release
```

`InteropTests` generates ephemeral **software test-only** P-256 keys and signs both `DDD P0 software P-256 SSHSIG probe\n` and `protocol/fixtures/compat/commit-payload-v1.txt` through the real upstream API. Artifacts are separated into `.build/probe-artifacts/simple/` and `.build/probe-artifacts/shared-commit/`. Each directory exports:

- `payload.bin` — exact bytes including final LF
- `software.pub` — OpenSSH public key, no private key
- `payload.sig` — armored SSHSIG
- `allowed_signers` — principal `ddd-probe`, restricted to namespace `git`

The test invokes `/usr/bin/ssh-keygen -Y verify` with payload on stdin, requiring success for the original payload and failure for a modified payload, wrong namespace, and wrong principal. Missing OpenSSH is a failure on macOS, not a silent skip. The iOS test path skips the subprocess probe; it is not a substitute for macOS interop. CI uploads these public/software-only artifacts, including on failure where available. Signatures/public keys vary between runs; payload bytes are fixed. Private software material is never exported.

The shared-fixture test also initializes a disposable SHA-1 Git repository, builds its empty tree, fixes author/committer identity and timestamps, and compares `git cat-file commit` for an unsigned `git commit-tree` object **byte-for-byte** with the common fixture. A test-only executable adapter returns the already-created AgePony SSHSIG only when Git's requested bytes match that fixture; it never receives a private key. Git itself creates the signed commit via `commit-tree -S` and inserts the `gpgsig` header. Real `git verify-commit` using `/usr/bin/ssh-keygen` must accept it and reject a message-tampered commit. The POSIX `/bin/sh` adapter requires exactly seven arguments with the `-Y sign -n git -f` prefix, uses `cmp` for byte-identical public key and payload checks, and uses `cat` to write the unchanged signature to the requested `file.sig`. It records argv as one argument per line in `git-sign-argv.txt` and raw input in `git-request.bin`; this text log is diagnostic, not a round-trippable encoding for arguments containing newlines. `signed-commit.txt` and `commit-id.txt` preserve the public result. This probes Git's temporary-file signing invocation; the C# compatibility suite additionally covers the CLI's stdin mode.

All subprocesses use file-backed stdin/stdout/stderr (no blocking pipe writes or full-output-buffer deadlock), a 10-second deadline, SIGTERM followed by SIGKILL after 0.5 seconds, and at most another 0.5-second reap wait. No `waitUntilExit` is used. A regression test runs `/bin/sh` with `trap '' TERM; while :; do :; done` and requires bounded timeout. The loop uses shell builtins only and is bounded by the process runner; SIGKILL leaves no child `sleep` process. The iOS probes require only Swift plus standard shell/Git/OpenSSH tools. This runner bounds the direct subprocess; arbitrary descendant process-tree management is outside the minimal probe (CI retains a job-level timeout). Git subprocesses ignore inherited `GIT_*` variables, global/system config, templates/hooks, and interactive prompts. CI disables checkout `core.autocrlf` **before** checkout; the fixture test additionally rejects CR and requires final LF instead of silently normalizing signed bytes. Fixture bytes are never rewritten by the tests.

Manual verification from `ios/` after tests:

```sh
ssh-keygen -Y verify -f .build/probe-artifacts/shared-commit/allowed_signers -I ddd-probe -n git -s .build/probe-artifacts/shared-commit/payload.sig < .build/probe-artifacts/shared-commit/payload.bin
```

## P0: native real-device persistence/approval probe

`ios/project.yml` generates a single SwiftUI application using XcodeGen (minimum 2.42.0). This avoids a bulky hand-maintained Xcode project for two app source files; the generated project is ignored. It links the local Swift package, which resolves the pinned upstream package. CI currently installs the available Homebrew XcodeGen version and prints Xcode/Swift versions; generator/toolchain reproducibility is not fully pinned.

On a Mac:

```sh
brew install xcodegen
# Run the following inside ios/
xcodegen generate
xcodebuild -project DDDFeasibility.xcodeproj -scheme DDDFeasibility -configuration Release -destination 'generic/platform=iOS' -derivedDataPath .build/xcode CODE_SIGNING_ALLOWED=NO build
```

The app now explicitly uses Swift 6 language mode with complete concurrency checking and a nonisolated default; UI/App declarations remain `@MainActor`, while the worker-operation enum is module-level and `Sendable`. The older Swift 5 app mode did not exercise Swift 6 isolation diagnostics. `LAContext` is configured before publication instead of being mutated by the detached worker while cancellation can run. The small cancellation wrapper still deliberately uses `@unchecked Sendable`: revocation is lock-protected, worker key/context use stays synchronous, and cancellation calls context invalidation. This is not a compiler-verified thread-safety guarantee for LocalAuthentication. Actual Swift 6 compilation/SDK annotations and background/authentication races remain pending CI and device checks; no blanket `@preconcurrency` import or unsafe actor-isolation bypass was added.

The unsigned build checks device SDK compilation only; it is not installable or a provisioning test. Open the generated project in Xcode, select your actual Personal Team and physical iPhone, and choose a unique bundle identifier you can provision. Keep that identifier stable for baseline experiments. No team ID, certificate, profile, Apple credentials, or privileged entitlements are supplied. No keychain sharing/App Groups are required for the baseline probe. `NSFaceIDUsageDescription` is generated into Info.plist.

### Probe behavior

- Nothing is created, loaded, or signed at startup.
- **Create** requires explicit confirmation and creates only a disposable enclave probe key. It only proceeds on `errSecItemNotFound`. Existing, corrupt, inaccessible, or duplicate entries are never replaced. There is no delete/update/reset or software fallback.
- An enclave-unavailable device fails explicitly. All software keys are confined to tests; the device probe intentionally creates a hardware key.
- The key uses `.privateKeyUsage` + `.userPresence`, with `WhenUnlockedThisDeviceOnly`. This intentionally allows device-passcode fallback; it is not a biometric-only claim. The opaque enclave blob and baseline public point are stored in a non-synchronizing, app-private generic-password Keychain item. The blob is not an exportable private scalar; the report never exports it.
- **Reload** restores the stored blob and checks the restored public point against the stored public point. It never creates. Compare the displayed full OpenSSH public key with a baseline saved **outside the app**; that external comparison is essential for uninstall/team-change experiments.
- **Approve one probe signature** shows the fixed exact payload and namespace, requires a confirmation, then signs once using a fresh `LAContext`, invalidated afterward. It does not separately preauthenticate or reuse a session-wide context. Keychain/enclave work runs in `Task.detached`, not on the main UI actor. A busy guard rejects duplicate starts. Backgrounding or view dismissal revokes pending approval, invalidates the context, and discards late results; there is no automatic retry. `.inactive` alone does not revoke because authentication UI may cause that state. Cancellation cannot undo an enclave signature or key creation that has already completed internally; the harness prevents publishing a revoked result, but real-device races/prompt behavior remain to be tested. Lifecycle cancellation does not delete a key created during a race—reload it explicitly.
- **Export** shares only a textual public report and, after a successful signing operation, a signature. Payload bytes must be extracted exactly, retaining the final LF, for external verification. Save that signature as a `.sig` file and construct an allowed-signers file from the exported public key to run the same `ssh-keygen` check. CI never runs this hardware path.
- Failures replace the displayed result with an error, not a generated identity or stale success. A disappeared identity still requires an explicit new-key action in this disposable probe. **Production must distinguish first enrollment from missing prior enrollment and must never permit silent rotation.** This probe is not a production identity store.

### Pending device matrix (all results currently unverified)

Use a disposable identity. Record device/model, iOS version, Xcode version, bundle ID, actual Team/application-identifier prefix, signed keychain access groups, provisioning UUID/expiry, install method, public key before/after, and exact errors. Do not record secrets or opaque key blobs.

| Experiment | Required observation |
| --- | --- |
| Baseline create → sign → force quit → reload → sign | Identical full public key; both signatures verify externally |
| Reboot and lock/unlock | Same key; locked/cancelled operations fail safely; no rotation |
| Overwrite app update, same bundle/team/groups | Restore and sign with baseline public key |
| Refresh Personal Team provisioning, same identity | Restore and sign; record actual profile lifetime, do not assume one |
| Re-sign IPA and overwrite install | Record signer/entitlement differences; same key only where identity access permits |
| Let profile expire, then refresh/reinstall | Observe launch restrictions and recovery; no claim this already works |
| Uninstall/reinstall, same bundle/team | Observe Keychain availability; do not assume persistence or deletion |
| Change bundle ID | Expect different access identity may prevent lookup; observe without implicit creation |
| Change Team or keychain access group | Record loss of access/errors; never interpret inaccessible key as successful persistence |
| Cancel biometric/passcode, lock during approval, rapid double tap | No successful unapproved signature; no extra signing or replacement |

Personal Team registration/signing on a developer Mac and unsigned CI builds are different capabilities. Automated free-account profile refresh on hosted CI, Linux/Windows installation with `pymobiledevice3`, and fresh signed IPA generation are **still feasibility questions**, not implemented pipelines. Installation tooling cannot bypass provisioning. A change in team/access-group identity may make a surviving blob inaccessible. Do not add entitlements merely to force a test to pass.

## P1: authentication findings and proposal

Apple's CryptoKit enclave key API accepts Security access control and a `LocalAuthentication.LAContext`; the probe uses these platform APIs directly because upstream's enclave service is app-local. The prepared probe requires user presence for each explicit signature. Actual prompt frequency, fallback, cancellation, background/foreground, lock behavior, and interaction with SwiftUI must be tested on-device. No performance or session-caching result has been measured.

Proposed initial production policy: show information derived from exact payload bytes, require explicit **Sign per commit**, use fresh user-presence authentication per signature, fail closed on cancellation/background transitions, and never turn a QR scan into approval. A short authenticated session may be evaluated later against plan §8; that requires explicit lifetime/invalidation rules and device UX evidence. `.biometryCurrentSet` is an alternative, not silently interchangeable with `.userPresence`: enrollment changes and passcode fallback have different recovery implications. No session-wide unlock or batch authorization is implemented.

## P1: TLS findings and proposal (no handshake probe yet)

The upstream package provides SSHSIG primitives, not DDD TLS/session transport. Apple Network.framework is a candidate for direct LAN TCP/TLS: `NWProtocolTLS.Options`, its `securityProtocolOptions`, and `sec_protocol_options_set_verify_block` permit custom peer authentication. Another candidate is URLSession with a server-trust challenge for an HTTP-shaped protocol. These are API/design proposals; neither has been implemented or interoperated with the Windows endpoint here.

Agree a single fingerprint definition with Windows first. Prefer SHA-256 of the exact leaf certificate DER for an ephemeral per-session identity; public-key/SPKI pinning is a distinct format and must not be confused with CryptoKit x963 or `SecKey` external representation. If SPKI is selected, use a reviewed library/API, not ad hoc DER assembly. Extract the peer leaf certificate through Security trust APIs, check exact pin equality, and complete verification **only** on the intended identity. A self-signed endpoint needs explicit pinned-trust handling, not accidental acceptance via public CA evaluation, nor an accept-all callback. Send the QR bearer token only after TLS peer authentication succeeds. Reject redirects/identity changes, do not fall back to unpinned HTTP, and avoid logging tokens.

Pending tests: valid/mismatched pin, replaced certificate with same/different key, self-signed chain behavior, missing trust object, TLS version negotiation, wrong/replayed/expired token, connection/approval timeouts, LAN permission denial, IPv4/IPv6 endpoints, hotspot/client isolation, and connection loss during approval. A native LAN client will need a truthful `NSLocalNetworkUsageDescription` when transport is added; this app performs no network access and does not request that permission. Strict mTLS is not proposed as v1's prerequisite. Payload-size/session-lifetime limits remain to be agreed and measured with the Windows workstream.

## P1: CBOR actual source findings and proposal

[Upstream FIDO/CBOR.swift](https://github.com/norsehorse-dev/AgePonyiOS/blob/33731a8d875493a466fc0668d8d9e9aec55f0005/Sources/AgePonyCore/FIDO/CBOR.swift) exposes `CBOR`, `encoded()`, and `CBORReader.decode(_:)`. It supports integers, byte/text strings, arrays/maps, booleans and null, and rejects tags/floats/indefinite lengths. Encoding sorts map keys by encoded length then bytes (CTAP2-style). Decoding preserves pair order, rejects top-level trailing bytes, allows duplicate map keys, accepts non-minimal integer encodings, and maps both undefined and null to `.null`.

More importantly, source inspection shows unchecked `UInt64` → `Int` length/count conversions, unchecked `offset + count`, count-based `reserveCapacity`, and recursive decoding without depth/item bounds. Hostile bytes can trap or exhaust resources before schema validation. A small outer byte-size cap alone does **not** fix attacker-declared counts or recursion. Its FIDO codec must not be treated as a hardened network decoder based on public API availability. This probe deliberately does not feed it arbitrary network data or adopt it as DDD's protocol codec.

Proposal: a small agreed integer-key/byte-string schema and a maintained bounded Swift CBOR decoder, or upstream hardening with checked lengths/counts, depth/item limits, duplicate-key rejection and explicit undefined/canonical policy before adoption. Do not invent a DDD parser or vendor the upstream codec. Agree golden encoded bytes and rejection vectors with C# before selecting a library; no new CBOR dependency is chosen yet. Test oversized declared lengths/counts, excessive depth, duplicate/unknown keys, wrong field types, trailing bytes, unsupported versions, invalid UTF-8, non-minimal forms, and maximum QR size. Deterministic encoding is not itself authorization; sign exact raw Git bytes through SSHSIG, not a CBOR re-encoding.

## Validation boundary

Follow-up Linux checks passed: workflow YAML parses with independent jobs and protocol triggers; the shared fixture has 268 bytes, no CR, and final LF. After the POSIX shell adapter replacement, ordinary shell commands validated its syntax, unchanged signature/input/argv bytes, paths containing spaces, and rejection of wrong argument count, namespace, public key, and payload. A shell-only timeout smoke check showed the TERM-ignoring child-free loop being killed after escalation. Both command sessions subsequently reported the known terminal TTY error, so they are not clean overall command exits. The Swift timeout implementation itself remains unrun. A real Git 2.51.0 scratch-repository run reproduced the fixture exactly via unsigned `commit-tree` and exercised the adapter's actual file-based argv/input and Git-generated `gpgsig` header using a deliberately invalid placeholder signature. This validates harness mechanics only, **not** AgePony signing, OpenSSH/Git cryptographic success, or the Swift subprocess timeout implementation. Scratch metadata was removed; no private key was generated or written. Swift tests (including timeout escalation) and iOS build/device checks remain unrun locally.

`.github/workflows/ios.yml` is triggered by iOS probe paths **and `protocol/**`**, and uses a read-only GitHub token. It runs independent macOS Debug/Release tests and an independent unsigned generic-iOS Release build without secrets. Local YAML/structure checks are possible on Linux; actual Swift/Xcode/SSHSIG results must come from CI or a Mac. Device P0 persistence and P1 authentication/TLS/CBOR interoperability remain open until results are recorded; do not mark them complete merely because the harness exists.
