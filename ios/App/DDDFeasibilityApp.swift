import DDDProbes
import SwiftUI

private enum ProbeOperation: Sendable { case create, reload, sign }

@main
@MainActor
struct DDDFeasibilityApp: App {
    var body: some Scene { WindowGroup { ProbeView() } }
}

@MainActor
private struct ProbeView: View {
    @State private var output = "No key is created or loaded automatically. Save the public key outside this app before persistence experiments."
    @State private var busy = false
    @State private var confirmCreate = false
    @State private var confirmSign = false
    @State private var pendingApproval: ProbeApproval?
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    Text("P0 disposable device probe").font(.headline)
                    Text("No transport, no batch signing, no production keys. A missing or unreadable identity is an error, not a reason to rotate.")
                    Button("Create disposable probe identity…") { confirmCreate = true }
                    Button("Reload existing identity (never create)") { run(.reload) }
                    Text("Exact payload to approve (UTF-8, final LF):")
                    Text(String(decoding: SigningProbe.payload, as: UTF8.self)).font(.system(.body, design: .monospaced))
                    Button("Approve one probe signature…") { confirmSign = true }
                    ShareLink("Export public report / signature", item: output)
                    Text(output).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                }
                .padding()
                .disabled(busy)
            }
            .navigationTitle("DDD Feasibility")
            .confirmationDialog("Create a new disposable Secure Enclave identity? Never use this key for production.",
                                isPresented: $confirmCreate, titleVisibility: .visible) {
                Button("Create probe key") { run(.create) }
                Button("Cancel", role: .cancel) {}
            }
            .confirmationDialog("Sign exactly the displayed payload once in namespace git? Device user presence is also required.",
                                isPresented: $confirmSign, titleVisibility: .visible) {
                Button("Sign this payload once") { run(.sign) }
                Button("Cancel", role: .cancel) {}
            }
        }
        .onChange(of: scenePhase) { _, phase in
            // Authentication UI can make the scene inactive; only background revokes.
            if phase == .background { cancelPending() }
        }
        .onDisappear { cancelPending() }
    }

    private func cancelPending() {
        confirmCreate = false
        confirmSign = false
        pendingApproval?.cancel()
        output = "Approval revoked; any in-flight result will be discarded. Reload explicitly after returning."
    }

    private func run(_ operation: ProbeOperation) {
        guard !busy, scenePhase == .active else { return }
        busy = true
        output = "Probe running; no previous result is available for export."
        let approval = ProbeApproval()
        pendingApproval = approval
        Task {
            let result = await Task.detached {
                // Keychain and synchronous authentication/signing never block the UI actor.
                do {
                    try approval.check()
                    switch operation {
                    case .create: return try ProbeIdentity.createExplicitly()
                    case .reload: return try ProbeIdentity.reload()
                    case .sign: return try ProbeIdentity.signOneApprovedPayload(approval: approval)
                    }
                } catch {
                    return "PROBE FAILED (no fallback, no rotation): \(error)"
                }
            }.value
            do {
                try approval.check()
                if scenePhase == .active {
                    output = result
                } else {
                    output = "Scene not active; result discarded. Reload or approve again explicitly."
                }
            } catch {
                output = "Approval revoked; late result discarded. No automatic retry or rotation."
            }
            approval.cancel()
            pendingApproval = nil
            busy = false
        }
    }
}
