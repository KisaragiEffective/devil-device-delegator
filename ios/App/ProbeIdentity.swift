import CryptoKit
import DDDProbes
import Foundation
import LocalAuthentication
import Security

/// LAContext invalidation may race a synchronous enclave call; check revocation before
/// and after it, and never publish a late result. No lock is held during authentication.
/// The unchecked boundary is limited to LAContext: configure before publication,
/// use only in the worker, and invalidate from cancellation. Device testing is still required.
final class ProbeApproval: @unchecked Sendable {
    fileprivate let context: LAContext
    private let lock = NSLock()
    private var revoked = false

    init() {
        let context = LAContext()
        context.localizedReason = "Sign this one explicitly approved DDD feasibility payload"
        self.context = context
    }

    func cancel() {
        lock.lock()
        revoked = true
        lock.unlock()
        context.invalidate()
    }

    func check() throws {
        lock.lock()
        let cancelled = revoked
        lock.unlock()
        if cancelled { throw CancellationError() }
    }
}

/// Isolated disposable probe identity. No delete, update, or implicit creation path.
enum ProbeIdentity {
    struct Record: Codable {
        let opaqueEnclaveBlob: Data
        let publicX963: Data
    }

    enum Failure: Error {
        case keychain(OSStatus)
        case enclaveUnavailable
        case accessControl
        case publicKeyChanged
    }

    private static var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: "DDD.feasibility.enclave.v1",
         kSecAttrAccount as String: "disposable-probe-identity",
         kSecAttrSynchronizable as String: false]
    }

    static func load() throws -> Record {
        var request = query
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        guard status == errSecSuccess else { throw Failure.keychain(status) }
        guard let data = result as? Data else { throw Failure.keychain(errSecDecode) }
        return try JSONDecoder().decode(Record.self, from: data)
    }

    static func createExplicitly() throws -> String {
        guard SecureEnclave.isAvailable else { throw Failure.enclaveUnavailable }
        // Only errSecItemNotFound permits creation. Corruption/auth failures must not rotate.
        do {
            _ = try load()
            return "Identity already exists. Use Reload; it was not replaced."
        } catch Failure.keychain(let status) where status == errSecItemNotFound {
            // The UI has separately confirmed creation of a disposable probe key.
        }
        var error: Unmanaged<CFError>?
        guard let control = SecAccessControlCreateWithFlags(
            nil, kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
            [.privateKeyUsage, .userPresence], &error
        ) else { throw Failure.accessControl }
        let key = try SecureEnclave.P256.Signing.PrivateKey(accessControl: control)
        let record = Record(opaqueEnclaveBlob: key.dataRepresentation,
                            publicX963: key.publicKey.x963Representation)
        var request = query
        request[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        request[kSecValueData as String] = try JSONEncoder().encode(record)
        // Add only: a racing duplicate also fails closed, never overwrites the identity.
        let status = SecItemAdd(request as CFDictionary, nil)
        guard status == errSecSuccess else { throw Failure.keychain(status) }
        return report(record)
    }

    static func reload() throws -> String {
        let record = try load()
        let context = LAContext()
        defer { context.invalidate() }
        let key = try SecureEnclave.P256.Signing.PrivateKey(
            dataRepresentation: record.opaqueEnclaveBlob, authenticationContext: context)
        guard key.publicKey.x963Representation == record.publicX963 else {
            throw Failure.publicKeyChanged
        }
        return report(record)
    }

    static func signOneApprovedPayload(approval: ProbeApproval) throws -> String {
        try approval.check()
        let record = try load()
        // Fresh context per approved signature, also invalidated by lifecycle cancellation.
        let context = approval.context
        defer { context.invalidate() }
        try approval.check()
        let key = try SecureEnclave.P256.Signing.PrivateKey(
            dataRepresentation: record.opaqueEnclaveBlob, authenticationContext: context)
        guard key.publicKey.x963Representation == record.publicX963 else {
            throw Failure.publicKeyChanged
        }
        try approval.check()
        let signature = try SigningProbe.enclaveSignature(key: key)
        try approval.check()
        return report(record) + "\nPayload (UTF-8, final LF):\n"
            + String(decoding: SigningProbe.payload, as: UTF8.self) + "\n" + signature
    }

    private static func report(_ record: Record) -> String {
        "DISPOSABLE SECURE ENCLAVE PROBE — NOT A PRODUCTION IDENTITY\n"
            + SigningProbe.publicKeyLine(x963: record.publicX963)
            + "\nCompare the full public key against your externally saved baseline.\n"
            + "Bundle: \(Bundle.main.bundleIdentifier ?? "unknown")\n"
            + "OS: \(ProcessInfo.processInfo.operatingSystemVersionString)"
    }
}
