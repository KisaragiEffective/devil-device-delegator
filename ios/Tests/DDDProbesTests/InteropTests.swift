import AgePonyCore
import CryptoKit
import DDDProbes
import Foundation
import XCTest

final class InteropTests: XCTestCase {
    #if os(macOS)
    private var repositoryRoot: URL {
        URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
    }

    private func artifacts(_ name: String) throws -> URL {
        let directory = repositoryRoot.appendingPathComponent("ios/.build/probe-artifacts/\(name)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    private func exportAndVerify(_ payload: Data, in directory: URL) throws {
        let fixture = try SigningProbe.softwareFixture(message: payload)
        try payload.write(to: directory.appendingPathComponent("payload.bin"))
        try Data((fixture.publicKey + "\n").utf8).write(to: directory.appendingPathComponent("software.pub"))
        try Data(fixture.signature.utf8).write(to: directory.appendingPathComponent("payload.sig"))
        try Data("ddd-probe namespaces=\"git\" \(fixture.publicKey)\n".utf8)
            .write(to: directory.appendingPathComponent("allowed_signers"))

        func verify(_ bytes: Data, namespace: String = "git", identity: String = "ddd-probe") throws -> Int32 {
            try BoundedProcess.run("/usr/bin/ssh-keygen", [
                "-Y", "verify", "-f", directory.appendingPathComponent("allowed_signers").path,
                "-I", identity, "-n", namespace, "-s", directory.appendingPathComponent("payload.sig").path
            ], in: directory, input: bytes).status
        }
        XCTAssertEqual(try verify(payload), 0)
        XCTAssertNotEqual(try verify(payload + Data([0])), 0)
        XCTAssertNotEqual(try verify(payload, namespace: "agepony"), 0)
        XCTAssertNotEqual(try verify(payload, identity: "wrong-principal"), 0)
    }
    #endif

    func testSoftwareP256WithOpenSSH() throws {
        #if os(macOS)
        try exportAndVerify(SigningProbe.payload, in: artifacts("simple"))
        #else
        throw XCTSkip("OpenSSH Process interop requires macOS")
        #endif
    }

    func testSharedCommitFixtureWithOpenSSHAndGit() throws {
        #if os(macOS)
        let payload = try Data(contentsOf: repositoryRoot.appendingPathComponent(
            "protocol/fixtures/compat/commit-payload-v1.txt"))
        XCTAssertFalse(payload.contains(13), "Checkout must preserve LF; do not normalize signed bytes")
        XCTAssertEqual(payload.last, 10, "Shared fixture requires its final LF")
        let directory = try artifacts("shared-commit")
        try exportAndVerify(payload, in: directory)

        // Hermetic scratch repository: no developer Git config, hooks or inherited Git environment.
        let repo = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: repo) }
        var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
        environment.merge([
            "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null", "GIT_TERMINAL_PROMPT": "0",
            "GIT_EDITOR": "true", "GIT_PAGER": "cat",
            "GIT_AUTHOR_NAME": "DDD Test", "GIT_AUTHOR_EMAIL": "ddd@example.invalid",
            "GIT_COMMITTER_NAME": "DDD Test", "GIT_COMMITTER_EMAIL": "ddd@example.invalid",
            "GIT_AUTHOR_DATE": "1700000000 +0000", "GIT_COMMITTER_DATE": "1700000000 +0000",
            "DDD_PROBE_ARTIFACTS": directory.path
        ], uniquingKeysWith: { _, new in new })
        func git(_ arguments: [String], input: Data = Data()) throws -> BoundedProcess.Result {
            try BoundedProcess.checked("/usr/bin/git", ["--no-pager"] + arguments,
                                       in: repo, input: input, environment: environment)
        }
        _ = try git(["init", "--object-format=sha1", "--template="])
        let tree = try git(["mktree"]).text
        let separator = try XCTUnwrap(payload.range(of: Data("\n\n".utf8)))
        let message = Data(payload[separator.upperBound...])
        let unsigned = try git(["-c", "commit.gpgsign=false", "commit-tree", tree], input: message).text
        let unsignedBytes = try git(["cat-file", "commit", unsigned]).stdout
        XCTAssertEqual(unsignedBytes, payload, "Git's exact unsigned payload must match the common fixture")
        guard unsignedBytes == payload else { return }

        // No private key on disk. This adapter returns the already-produced AgePony SSHSIG,
        // but only if Git supplies exactly the bytes that it covers. Git adds the gpgsig header.
        let adapter = directory.appendingPathComponent("return-signature.sh")
        let script = #"""
        #!/bin/sh
        set -eu
        : "${DDD_PROBE_ARTIFACTS:?Missing probe artifacts directory}"
        printf '%s\n' "$@" > "$DDD_PROBE_ARTIFACTS/git-sign-argv.txt"
        if [ "$#" -ne 7 ]; then
            printf '%s\n' 'Unexpected Git SSH signing argument count' >&2
            exit 1
        fi
        if [ "$1" != '-Y' ] || [ "$2" != 'sign' ] || [ "$3" != '-n' ] ||
           [ "$4" != 'git' ] || [ "$5" != '-f' ]; then
            printf '%s\n' 'Unexpected Git SSH signing invocation' >&2
            exit 1
        fi
        if ! cmp -s "$6" "$DDD_PROBE_ARTIFACTS/software.pub"; then
            printf '%s\n' 'Unexpected signing public key' >&2
            exit 1
        fi
        cat "$7" > "$DDD_PROBE_ARTIFACTS/git-request.bin"
        if ! cmp -s "$7" "$DDD_PROBE_ARTIFACTS/payload.bin"; then
            printf '%s\n' 'Refusing to return a signature for different bytes' >&2
            exit 1
        fi
        cat "$DDD_PROBE_ARTIFACTS/payload.sig" > "$7.sig"
        """#
        try Data((script + "\n").utf8).write(to: adapter)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: adapter.path)
        let signed = try git([
            "-c", "gpg.format=ssh", "-c", "gpg.ssh.program=\(adapter.path)",
            "-c", "user.signingkey=\(directory.appendingPathComponent("software.pub").path)",
            "commit-tree", "-S", tree
        ], input: message).text
        XCTAssertEqual(try Data(contentsOf: directory.appendingPathComponent("git-request.bin")), payload)
        let signedBytes = try git(["cat-file", "commit", signed]).stdout
        try signedBytes.write(to: directory.appendingPathComponent("signed-commit.txt"))
        try Data((signed + "\n").utf8).write(to: directory.appendingPathComponent("commit-id.txt"))
        _ = try git(["-c", "gpg.format=ssh", "-c", "gpg.ssh.program=/usr/bin/ssh-keygen",
                     "-c", "gpg.ssh.allowedSignersFile=\(directory.appendingPathComponent("allowed_signers").path)",
                     "verify-commit", signed])

        let modified = Data(String(decoding: signedBytes, as: UTF8.self)
            .replacingOccurrences(of: "These UTF-8 bytes", with: "Altered UTF-8 bytes").utf8)
        XCTAssertNotEqual(modified, signedBytes)
        let alteredID = try git(["hash-object", "-t", "commit", "-w", "--stdin"], input: modified).text
        let rejected = try BoundedProcess.run("/usr/bin/git", [
            "--no-pager", "-c", "gpg.format=ssh", "-c", "gpg.ssh.program=/usr/bin/ssh-keygen",
            "-c", "gpg.ssh.allowedSignersFile=\(directory.appendingPathComponent("allowed_signers").path)",
            "verify-commit", alteredID
        ], in: repo, environment: environment)
        XCTAssertNotEqual(rejected.status, 0, "Git must reject a changed signed commit")
        #else
        throw XCTSkip("Git/OpenSSH Process interop requires macOS")
        #endif
    }

    func testStaticGoldenWithOpenSSHAndAgePony() throws {
        #if os(macOS)
        let common = repositoryRoot.appendingPathComponent("protocol/fixtures/compat")
        let payload = try Data(contentsOf: common.appendingPathComponent("commit-payload-v1.txt"))
        let publicKey = try String(contentsOf: common.appendingPathComponent("p256-public-v1.pub"), encoding: .utf8)
        let signature = try String(contentsOf: common.appendingPathComponent("p256-signature-v1.sshsig"), encoding: .utf8)
        struct Provenance: Decodable {
            let payload: String
            let payloadSha256: String
            let namespace: String
            let publicKey: String
            let signature: String
            let privateKeyIncluded: Bool
        }
        let provenance = try JSONDecoder().decode(Provenance.self, from:
            Data(contentsOf: common.appendingPathComponent("provenance-v1.json")))
        XCTAssertEqual(provenance.payload, "commit-payload-v1.txt")
        XCTAssertEqual(provenance.publicKey, "p256-public-v1.pub")
        XCTAssertEqual(provenance.signature, "p256-signature-v1.sshsig")
        XCTAssertEqual(provenance.namespace, "git")
        XCTAssertFalse(provenance.privateKeyIncluded)
        let digest = SHA256.hash(data: payload).map { String(format: "%02x", $0) }.joined()
        XCTAssertEqual(digest, provenance.payloadSha256)
        XCTAssertFalse(payload.contains(13))
        XCTAssertEqual(payload.last, 10)

        // Decode only the text line's Base64 field, never SSH wire format ourselves.
        // Upstream SSHKey.parsePublicKey is Ed25519-only at the pinned revision.
        let fields = publicKey.split(whereSeparator: { $0.isWhitespace })
        XCTAssertEqual(fields.first, "ecdsa-sha2-nistp256")
        guard fields.count >= 2 else { return XCTFail("Malformed golden public key line") }
        let expectedWire = try XCTUnwrap(Data(base64Encoded: String(fields[1])))
        let verified = try SSHSigVerifier.verify(message: payload, armoredSignature: signature,
                                                 expectedNamespace: "git")
        XCTAssertEqual(verified.publicKeyWire, expectedWire, "Validity alone does not establish a trusted key")
        XCTAssertEqual(verified.keyType, "ecdsa-sha2-nistp256")
        XCTAssertEqual(verified.namespace, "git")
        XCTAssertEqual(verified.hash, .sha512)
        XCTAssertThrowsError(try SSHSigVerifier.verify(message: payload + Data([0]),
                                                      armoredSignature: signature, expectedNamespace: "git"))
        XCTAssertThrowsError(try SSHSigVerifier.verify(message: payload, armoredSignature: signature,
                                                      expectedNamespace: "agepony"))

        let directory = try artifacts("static-golden")
        let allowed = directory.appendingPathComponent("allowed_signers")
        try Data(("ddd-probe namespaces=\"git\" "
            + publicKey.trimmingCharacters(in: .whitespacesAndNewlines) + "\n").utf8).write(to: allowed)
        func verify(_ bytes: Data, namespace: String = "git") throws -> Int32 {
            try BoundedProcess.run("/usr/bin/ssh-keygen", [
                "-Y", "verify", "-f", allowed.path, "-I", "ddd-probe", "-n", namespace,
                "-s", common.appendingPathComponent("p256-signature-v1.sshsig").path
            ], in: directory, input: bytes).status
        }
        XCTAssertEqual(try verify(payload), 0)
        XCTAssertNotEqual(try verify(payload + Data([0])), 0)
        XCTAssertNotEqual(try verify(payload, namespace: "agepony"), 0)
        #else
        throw XCTSkip("Static golden file/OpenSSH probe requires macOS repository checkout")
        #endif
    }

    func testAllowedSignersNamespacePolicyWithPermissiveControl() throws {
        #if os(macOS)
        let key = P256.Signing.PrivateKey()
        let publicKey = SigningProbe.publicKeyLine(x963: key.publicKey.x963Representation)
        let signature = try SSHSigner.signECDSAP256(message: SigningProbe.payload, privateKey: key,
                                                    namespace: "agepony", hash: .sha512)
        let directory = try artifacts("namespace-policy")
        let signatureURL = directory.appendingPathComponent("agepony.sig")
        try Data(signature.utf8).write(to: signatureURL)
        let restricted = directory.appendingPathComponent("git_only_signers")
        let permissive = directory.appendingPathComponent("permissive_signers")
        try Data("ddd-probe namespaces=\"git\" \(publicKey)\n".utf8).write(to: restricted)
        try Data("ddd-probe \(publicKey)\n".utf8).write(to: permissive)
        func verify(allowed: URL) throws -> Int32 {
            try BoundedProcess.run("/usr/bin/ssh-keygen", [
                "-Y", "verify", "-f", allowed.path, "-I", "ddd-probe", "-n", "agepony",
                "-s", signatureURL.path
            ], in: directory, input: SigningProbe.payload).status
        }
        // Same signature, key, payload and correct namespace; only signer policy differs.
        XCTAssertEqual(try verify(allowed: permissive), 0)
        XCTAssertNotEqual(try verify(allowed: restricted), 0)
        #else
        throw XCTSkip("Allowed-signers policy probe requires macOS")
        #endif
    }

    func testProcessTimeoutEscalatesWithoutHanging() throws {
        #if os(macOS)
        let start = Date()
        // Shell builtins only: SIGKILL cannot orphan a sleep subprocess.
        XCTAssertThrowsError(try BoundedProcess.run("/bin/sh", [
            "-c", "trap '' TERM; while :; do :; done"
        ], in: FileManager.default.temporaryDirectory, timeout: 0.5)) { error in
            guard case BoundedProcess.Failure.timeout = error else {
                return XCTFail("Expected timeout, received \(error)")
            }
        }
        XCTAssertLessThan(Date().timeIntervalSince(start), 5)
        #else
        throw XCTSkip("Process timeout probe requires macOS")
        #endif
    }
}
