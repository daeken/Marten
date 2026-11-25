import SwiftUI

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

    var body: some View {
        Group {
            if let api = connections.connected {
                WorkspaceView(api: api) { connections.showServers = true }
                    .id(connections.workspaceID)
            } else {
                ServerScreen(connections: connections)
            }
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
                    Label("New device pairing is not available in this build.", systemImage: "qrcode")
                }
                if !connections.endpoints.isEmpty {
                    Section("Saved Servers") {
                        ForEach(connections.endpoints, id: \.self) { endpoint in
                            HStack {
                                Button {
                                    connections.connect(endpoint)
                                } label: {
                                    HStack {
                                        Text(endpoint.origin).lineLimit(2)
                                        Spacer()
                                        if connections.connected?.endpoint == endpoint {
                                            Image(systemName: "checkmark").accessibilityLabel("Connected")
                                        }
                                    }
                                    .frame(minHeight: 44)
                                    .contentShape(Rectangle())
                                }
                                .buttonStyle(.plain)
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
                if connections.busy { ProgressView("Connecting…").padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12)) }
            }
        }
        .interactiveDismissDisabled(connections.busy)
        #if DEBUG
        .sheet(isPresented: $showDev) {
            LoopbackForm(connections: connections)
                #if os(macOS)
                .frame(width: 460, height: 250)
                #endif
        }
        #endif
        .alert("Forget this server?", isPresented: Binding(get: { forget != nil }, set: { if !$0 { forget = nil } }), presenting: forget) { endpoint in
            Button("Forget", role: .destructive) { connections.forget(endpoint) }
            Button("Cancel", role: .cancel) {}
        } message: { endpoint in
            Text("Remove the local credential for \(endpoint.origin)? This does not revoke it on the server.")
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
