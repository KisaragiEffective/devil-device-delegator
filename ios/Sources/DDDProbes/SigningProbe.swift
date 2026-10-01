import AgePonyCore
import CryptoKit
import Foundation

/// Feasibility fixtures only; this is not a production signing service.
public enum SigningProbe {
    public static let payload = Data("DDD P0 software P-256 SSHSIG probe\n".utf8)
    public static let namespace = "git"

    public static func publicKeyLine(x963: Data) -> String {
        let wire = SSHSig.ecdsaP256PublicKeyWire(x963Q: x963)
        return "ecdsa-sha2-nistp256 \(wire.base64EncodedString()) DDD-probe-only"
    }

    public static func softwareFixture(message: Data = payload) throws -> (publicKey: String, signature: String) {
        // Ephemeral software key: never persisted or used as a production fallback.
        let key = P256.Signing.PrivateKey()
        return (
            publicKeyLine(x963: key.publicKey.x963Representation),
            try SSHSigner.signECDSAP256(message: message, privateKey: key,
                                       namespace: namespace, hash: .sha512)
        )
    }

    public static func enclaveSignature(key: SecureEnclave.P256.Signing.PrivateKey) throws -> String {
        let signed = SSHSig.signedData(message: payload, namespace: namespace, hash: .sha512)
        let signature = try key.signature(for: signed)
        return try SSHSigner.assembleECDSAP256(
            rawRS: signature.rawRepresentation,
            publicKeyX963: key.publicKey.x963Representation,
            namespace: namespace, hash: .sha512
        )
    }
}
