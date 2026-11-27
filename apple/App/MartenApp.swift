import SwiftUI
import UniformTypeIdentifiers

@main
struct MartenApp: App {
    @State private var connections = Connections()

    var body: some Scene {
        WindowGroup {
            RootView(connections: connections)
                #if os(macOS)
                .frame(minWidth: 380, minHeight: 440)
                #endif
        }
        #if os(macOS)
        .defaultSize(width: 650, height: 720)
        #endif
    }
}

struct RootView: View {
    @Bindable var connections: Connections
    @Environment(\.scenePhase) private var phase
    @State private var foregroundReady = false

    var body: some View {
        Group {
            if let api = connections.connected {
                WorkspaceView(api: api, showServers: { connections.showServers = true },
                              endPairedSession: connections.pairingEnabled && api.endpoint.url.scheme == "https"
                                  ? { connections.endRejectedPairedSession(api) } : nil,
                              routeID: connections.transportGeneration,
                              networkActive: phase == .active && foregroundReady && !connections.busy && connections.routeUsable,
                              composers: connections.composers)
                    .id(connections.workspaceID)
            } else {
                ServerScreen(connections: connections)
            }
        }
        .onChange(of: phase) { if phase != .active { foregroundReady = false } }
        .task(id: phase) {
            guard phase == .active else { return }
            // A cancelled recovery may still be closing the old node.
            while connections.busy {
                do { try await Task.sleep(for: .milliseconds(100)) }
                catch { return }
            }
            guard !Task.isCancelled else { return }
            await connections.foreground()
            if !Task.isCancelled { foregroundReady = true }
        }
        .sheet(isPresented: $connections.showServers) {
            ServerScreen(connections: connections)
                #if os(macOS)
                .frame(width: 490, height: 530)
                #endif
        }
    }
}

struct ServerScreen: View {
    @Bindable var connections: Connections
    @Environment(\.scenePhase) private var phase
    @State private var pickQR = false
    @State private var deviceName = "Marten Device"
    #if os(iOS)
    @State private var scanQR = false
    #endif
    @State private var forget: Endpoint?
    #if DEBUG
    @State private var showDev = false
    #endif

    var body: some View {
        NavigationStack {
            List {
                if let error = connections.error {
                    Section("Connection Problem") {
                        Text(error)
                        Button("Dismiss") { connections.error = nil }
                    }
                }
                Section("Pair a Server") {
                    if !connections.pairingEnabled {
                        Label("New device pairing is not available in this build.", systemImage: "qrcode")
                    } else if let offer = connections.pendingOffer {
                        Text(offer.endpoint.origin).font(.headline)
                        Text("Confirm that this is your server before enrolling this device in Tailscale.")
                        TextField("Device Name", text: $deviceName)
                        Button("Confirm Pairing") {
                            let name = deviceName
                            Task { await connections.confirmOffer(deviceName: name) }
                        }
                        Button("Cancel", role: .cancel) { connections.cancelOffer() }
                    } else {
                        #if os(iOS)
                        Button("Scan Pairing QR…", systemImage: "qrcode.viewfinder") { scanQR = true }
                            .disabled(!QRScanner.available)
                        #endif
                        Button("Import QR Image…", systemImage: "photo") { pickQR = true }
                    }
                }
                if !connections.endpoints.isEmpty {
                    Section("Saved Servers") {
                        ForEach(connections.endpoints, id: \.self) { endpoint in
                            HStack {
                                Button {
                                    Task { await connections.select(endpoint) }
                                } label: {
                                    HStack {
                                        VStack(alignment: .leading) {
                                            Text(endpoint.origin).lineLimit(2)
                                            if connections.pendingForget.contains(endpoint) {
                                                Text("Local cleanup pending").font(.caption).foregroundStyle(.secondary)
                                            }
                                        }
                                        Spacer()
                                        if connections.connected?.endpoint == endpoint && connections.routeUsable {
                                            Image(systemName: "checkmark").accessibilityLabel("Connected")
                                        }
                                    }
                                    .frame(minHeight: 44)
                                    .contentShape(Rectangle())
                                }
                                .buttonStyle(.plain)
                                .disabled(connections.pendingForget.contains(endpoint))
                                Button(role: .destructive) { forget = endpoint } label: {
                                    Image(systemName: "trash").frame(minWidth: 44, minHeight: 44)
                                }
                                .buttonStyle(.borderless)
                                .accessibilityLabel("Forget \(endpoint.origin)")
                            }
                        }
                    }
                }
                if connections.connected != nil {
                    Button("Disconnect", role: .destructive) {
                        connections.showServers = false
                        connections.disconnect()
                    }
                }
                #if DEBUG
                Section("Developer") {
                    Button("Connect to Loopback…") { showDev = true }
                }
                #endif
            }
            .disabled(connections.busy)
            .navigationTitle("Marten Servers")
            .toolbar {
                if connections.connected != nil {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Done") {
                            connections.showServers = false
                        }.disabled(connections.busy)
                    }
                }
            }
            .overlay {
                if connections.busy { ProgressView("Working…").padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12)) }
            }
        }
        .interactiveDismissDisabled(connections.busy)
        .onDisappear { connections.cancelOffer() }
        .onChange(of: phase) {
            if phase == .background {
                connections.cancelOffer()
                pickQR = false
                #if os(iOS)
                scanQR = false
                #endif
            }
        }
        .onChange(of: connections.pendingOffer != nil) {
            if connections.pendingOffer == nil { deviceName = "Marten Device" }
        }
        .fileImporter(isPresented: $pickQR, allowedContentTypes: [.image]) { result in
            guard phase != .background, connections.pairingEnabled else { return }
            do { connections.scan(try QRImage.read(result.get())) }
            catch { connections.show(ClientError.image) }
        }
        #if os(iOS)
        .sheet(isPresented: $scanQR) {
            ScannerScreen { result in
                guard scanQR, phase != .background else { return }
                scanQR = false
                switch result {
                case .success(let payload): connections.scan(payload)
                case .failure: connections.show(ClientError.image)
                }
            }
        }
        #endif
        #if DEBUG
        .sheet(isPresented: $showDev) {
            LoopbackForm(connections: connections)
                #if os(macOS)
                .frame(width: 460, height: 250)
                #endif
        }
        #endif
        .alert("Forget this server?", isPresented: Binding(get: { forget != nil }, set: { if !$0 { forget = nil } }), presenting: forget) { endpoint in
            Button("Forget", role: .destructive) { Task { await connections.forget(endpoint) } }
            Button("Cancel", role: .cancel) {}
        } message: { endpoint in
            if (try? Endpoint(endpoint.origin)) == endpoint {
                Text("Remove the local credential and Tailscale node state for \(endpoint.origin)? This does not revoke the credential on the server or remove the device from your tailnet. To pair again, get a new QR.")
            } else {
                Text("Remove the local credential for \(endpoint.origin)? This does not revoke it on the server.")
            }
        }
    }
}

#if DEBUG
private struct LoopbackForm: View {
    @Bindable var connections: Connections
    @Environment(\.dismiss) private var dismiss
    @State private var origin = "http://127.0.0.1:17450"
    @State private var token = ""

    var body: some View {
        NavigationStack {
            Form {
                TextField("Loopback Origin", text: $origin).autocorrectionDisabled()
                SecureField("Bearer Token", text: $token)
                Text("Only localhost or 127.0.0.1. Use a scratch server, not a production token.")
                if connections.busy { ProgressView() }
            }
            .padding()
            .navigationTitle("Loopback Development")
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() }.disabled(connections.busy) }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Connect") {
                        Task {
                            let previous = connections.workspaceID
                            await connections.connectLoopback(origin, token: token)
                            token = ""
                            if previous != connections.workspaceID { dismiss() }
                        }
                    }.disabled(token.isEmpty || connections.busy)
                }
            }
            .disabled(connections.busy)
        }
        .interactiveDismissDisabled(connections.busy)
        .alert("Connection Problem", isPresented: Binding(get: { connections.error != nil }, set: { if !$0 { connections.error = nil } }), presenting: connections.error) { _ in
            Button("OK", role: .cancel) {}
        } message: { error in
            Text(error)
        }
    }
}
#endif
