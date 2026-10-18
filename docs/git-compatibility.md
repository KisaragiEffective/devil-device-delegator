# Git / OpenSSH compatibility contract

## Evidence and scope

Locally observed: Debian 13, Git 2.51.0, OpenSSH_10.0p2 Debian-7+deb13u4, .NET SDK 10.0.301/runtime 10.0.9. The real Git tests are also configured for Git for Windows in CI; Windows results are **not yet observed**. These tests establish the adapter contract, not the final iPhone signing implementation.

Production uses only the adapter and fails closed until its iPhone backend exists. A separate executable under `windows/tests/` delegates software P-256 signing and verification to OpenSSH. No private test key is committed, and no SSHSIG crypto stack is reimplemented.

## Observed Git invocation

With `gpg.format=ssh`, `user.signingkey=<public-key-file>`, `gpg.ssh.program=<executable-path>`:

```text
argv[0..]: -Y sign -n git -f <public-key-file> <temporary-payload-file>
input:     exact bytes in the temporary file (not stdin in the observed Git version)
output:    armored SSHSIG in <temporary-payload-file>.sig
stdout:    empty in file mode
exit:      zero only after writing the signature
```

The test normalizes only the public-key path and random temporary payload path before comparing `git-sign-argv-v1.json`. It separately compares the captured payload byte-for-byte with the commit fixture. Git removes its temporary payload and signature after consuming them; the test checks both are absent.

`gpg.ssh.program` is an executable path, **not** a quoted command line. Spaces and Unicode in the executable and public-key paths are exercised by the test. In `.gitconfig`, normal config-file string escaping still applies.

The adapter additionally supports OpenSSH's `-` stdin mode: raw input bytes in, armored SSHSIG on stdout. It accepts optional `-q`. File mode refuses to overwrite an existing `.sig`; partial newly created output is removed on write failure. It never normalizes line endings or decodes signing bytes as text.

Not supported in this milestone: `-Y verify`, `find-principals`, `check-novalidate`, arbitrary namespaces, multiple input files, agent-only `-U`, inline public-key configuration, or generic OpenSSH operations. Signed tags and other Git versions require separate observation; accepting argv is not proof that their payload semantics are supported.

## Verification configuration

A minimal signing adapter is not a complete replacement for `ssh-keygen`. Git verification also uses `gpg.ssh.program`; use ordinary OpenSSH for verification explicitly:

```sh
git -c gpg.ssh.program=ssh-keygen verify-commit HEAD
```

An allowed-signers file must contain the expected public key and principal, preferably restricted to `namespaces="git"`. Compatibility tests configure this in a disposable repository and require ordinary Git to verify the generated commit.

Host-side signature validation in the test backend uses the expected public key, **original payload**, and `git` namespace before returning anything. Production host verification will need the same contract, without recursively invoking DDD itself.

## Payload / SSHSIG boundary (P0)

Git sends the unsigned commit body, not the object framing `commit <length>\0`, and not a Windows-provided summary. The fixture contains the tree, author, committer and message with exact LF bytes. Git inserts the returned signature into the final commit's `gpgsig` header; that header is not part of the unsigned input fixture.

SSHSIG then applies its specified domain-separated signed-data construction over those original message bytes:

```text
Git payload M
→ SSHSIG message hash H(M), e.g. SHA-512
→ SSHSIG signed-data preimage (magic, namespace, reserved, hash algorithm, message hash)
→ P-256 ECDSA signing
→ SSHSIG encoding / armor
```

The SSHSIG message hash and P-256 signature's digest are distinct layers. DDD must pass the original message to AgePony's message-signing API, or use its `signedData` helper and an external enclave signature exactly as the upstream API requires. It must not prehash twice, reconstruct a commit from displayed text, or sign a client summary. The iOS probes exercise both the software API and the enclave assembly path (the latter still requires real-device validation).

This milestone establishes the byte boundary, **not a production Git commit parser**. Parser handling of continuation headers, encoding, object-ID formats, duplicates and malformed data remains a Phase 5 gate. Unknown or malformed payloads must never be approved through a misleading trusted UI.

## Fixtures and negative cases

`protocol/fixtures/compat/` contains:

- `commit-payload-v1.txt`: fixed UTF-8 unsigned commit bytes, including final LF.
- `git-sign-argv-v1.json`: normalized invocation observed in Git.
- `p256-public-v1.pub` / `p256-signature-v1.sshsig`: initial OpenSSH-generated public-only golden.
- `provenance-v1.json`: payload SHA-256 and generation source.

The golden was generated once using an ephemeral software key and independently verified with OpenSSH before retaining only its public key and signature. Do not auto-update it to make failing tests pass. Add versioned fixtures after review. `.gitattributes` preserves LF on Windows; future binary fixtures are excluded from text conversion.

Tests cover altered payload, wrong namespace, wrong public key, malformed signature, rejection, cancellation, oversized/empty input, existing signature preservation, unsupported invocation, secret-free errors, and Git abort without creating a commit. Production is tested to ignore the software signer's test environment.

## Windows CI portability follow-up

`gh run view` confirms run [36873826171](https://github.com/KisaragiEffective/devil-device-delegator/actions/runs/36873826171) passed Ubuntu and failed Windows (`compatibility (windows-2025)`, job `110408054374`). Downloading the run/job logs with the current gh authentication returned HTTP 403; the detailed failures below are from the supplied Windows log, not a successful log download.

- The child rejected an invocation without reading stdin, so the parent's concurrent pipe write/close failed. The tool runner now preserves the rejecting child's nonzero exit code and captured diagnostics after draining output. An input-delivery error with exit code zero still throws; it cannot be mistaken for successful signing. Large-input regression tests force both paths rather than relying on a one-byte race.
- Git loose objects were read-only, causing Windows recursive directory deletion to fail in test teardown. Cleanup now clears only the ReadOnly attribute of files in the disposable test tree before deletion. It does not suppress access/sharing errors or change the actual repository. A read-only-object regression test covers cleanup.
- TLS tests failed before their pin callback ran; see [TLS feasibility](tls-feasibility.md) for the Schannel-compatible certificate import and newly exposed client/server diagnostics. The original inner TLS exception and a successful Windows rerun are not yet available.

After these changes, local Linux Release tests report **33 passed, 0 failed, 0 skipped**. Windows rerun remains required; Linux success is not Windows execution evidence.
