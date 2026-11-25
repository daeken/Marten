import SwiftUI
import CoreImage.CIFilterBuiltins

struct PairedDevices: Decodable, Sendable {
    struct Device: Decodable, Identifiable, Sendable {
        let id: UUID
        let name: String
        let createdAt: String
    }
    let enabled: Bool
    let requiresTailscaleEnrollment: Bool
    let devices: [Device]
}

struct DeviceInvite: Sendable, CustomDebugStringConvertible, CustomStringConvertible {
    private let payload: String
    let expiresAt: Date
    var description: String { "Marten device invitation (redacted)" }
    var debugDescription: String { description }

    init(payload: String, expiry: String, mode: PairingMode, endpoint: Endpoint, key: String?, now: Date) throws {
        let offer = try PairOffer(payload, mode: mode, now: now)
        guard offer.endpoint == endpoint, offer.matchesEnrollmentKey(key), expiry == offer.expiryStamp else {
            throw ClientError.pairOffer
        }
        self.payload = payload
        expiresAt = offer.expiresAt
    }

    func image(at now: Date) -> CGImage? {
        guard now < expiresAt else { return nil }
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(payload.utf8)
        guard let output = filter.outputImage else { return nil }
        return CIContext().createCGImage(output, from: output.extent)
    }
}

@MainActor @Observable
final class DeviceManagementState {
    private(set) var listing: PairedDevices?
    private(set) var readError: String?
    private(set) var actionError: String?
    private(set) var busy = false
    private(set) var rejectedSession = false
    private var revision = UUID()
    private var needsRead = false

    var ready: Bool { listing != nil && readError == nil && !busy && !needsRead && !rejectedSession }

    func refresh(_ api: API) async {
        guard !busy else { return }
        await read(api)
    }

    func revoke(_ id: UUID, using api: API) async {
        guard ready, listing?.devices.contains(where: { $0.id == id }) == true, !Task.isCancelled else { return }
        busy = true
        needsRead = true
        revision = UUID()
        actionError = nil
        defer { busy = false }
        do { try await api.revokeDevice(id) }
        catch { actionError = "Revocation not confirmed. Check the refreshed device list before trying again. \(safeMessage(error))" }
        await read(api)
        if listing?.devices.contains(where: { $0.id == id }) == false && readError == nil { actionError = nil }
    }

    private func read(_ api: API) async {
        guard !Task.isCancelled, !rejectedSession else { return }
        let request = UUID()
        revision = request
        do {
            let fresh = try await api.pairedDevices()
            guard !Task.isCancelled, request == revision else { return }
            listing = fresh
            needsRead = false
            readError = nil
        } catch {
            guard !Task.isCancelled, request == revision else { return }
            readError = safeMessage(error)
            // #55 does not expose the active device ID. A management-route denial
            // alone is not conclusive; verify the same bearer on the ordinary API.
            if (error as? ClientError) == .unauthorized {
                do { _ = try await api.status() }
                catch ClientError.unauthorized {
                    guard !Task.isCancelled, request == revision else { return }
                    listing = nil
                    rejectedSession = true
                } catch { }
            }
        }
    }
}

struct DeviceManagementView: View {
    let api: API
    let active: Bool
    let route: UUID
    let endSession: () -> Void
    @State private var state = DeviceManagementState()
    @State private var revokeTarget: PairedDevices.Device?
    @State private var showingRevoke = false
    @State private var key = ""
    @State private var inviteRequest: UUID?
    @State private var inviteExpiry: Date?
    @State private var qr: CGImage?
    @State private var inviteNotice: String?

    var body: some View {
        List {
            if let error = state.readError { Label(error, systemImage: "exclamationmark.circle") }
            else if state.listing == nil { ProgressView("Loading devices…") }
            if let error = state.actionError { Label(error, systemImage: "exclamationmark.circle") }
            if state.busy { ProgressView("Revoking…") }
            if let listing = state.listing {
                Section("Paired Devices") {
                    if listing.devices.isEmpty { Text("No paired devices.") }
                    ForEach(listing.devices) { device in
                        VStack(alignment: .leading) {
                            Text(device.name).font(.headline)
                            InboxTime(stamp: device.createdAt)
                            Button("Revoke Device…", role: .destructive) {
                                clearInvite(); revokeTarget = device; showingRevoke = true
                            }.disabled(!active || !state.ready)
                        }
                    }
                }
                Section("Invite a Device") {
                    if !listing.enabled { Text("This server is not accepting new pairing invitations.") }
                    else if let expiry = inviteExpiry, let qr {
                        TimelineView(.periodic(from: .now, by: 1)) { context in
                            if context.date < expiry {
                                Image(decorative: qr, scale: 1).interpolation(.none).resizable().scaledToFit()
                                    .frame(maxWidth: 260, maxHeight: 260).padding(12).background(.white)
                                    .accessibilityLabel("Pairing QR code").privacySensitive()
                                LabeledContent("Expires") { InboxTime(stamp: expiry.ISO8601Format()) }
                            } else { Text("Invitation expired.") }
                        }
                        Button("Hide Invitation", action: clearInvite)
                    } else if inviteRequest != nil { ProgressView("Creating invitation…") }
                    else {
                        Text("Show the QR only to the intended device. Anyone who captures it may use it before it expires. Hiding it does not cancel it on the server.")
                            .font(.footnote).foregroundStyle(.secondary)
                        if listing.requiresTailscaleEnrollment {
                            SecureField("One-use Tailscale auth key", text: $key).autocorrectionDisabled()
                                #if os(iOS)
                                .textContentType(nil).textInputAutocapitalization(.never)
                                #endif
                        }
                        Button("Create Invite QR") { startInvite(listing.requiresTailscaleEnrollment) }
                            .disabled(!active || !state.ready || (listing.requiresTailscaleEnrollment && !PairOffer.validEnrollmentKey(key)))
                    }
                    if let inviteNotice { Text(inviteNotice).font(.footnote) }
                }
            }
        }
        .navigationTitle("Paired Devices")
        .toolbar { Button("Refresh", systemImage: "arrow.clockwise") { Task { await refresh() } }.disabled(!active) }
        .refreshable { await refresh() }
        .task(id: OperationsRefresh(route: route, active: active)) {
            guard active else { return }
            while !Task.isCancelled { await refresh(); try? await Task.sleep(for: .seconds(8)) }
        }
        .task(id: inviteExpiry) {
            guard let expiry = inviteExpiry else { return }
            do { try await Task.sleep(for: .seconds(max(0, expiry.timeIntervalSinceNow))) }
            catch { return }
            if inviteExpiry == expiry {
                clearInvite()
                inviteNotice = "Invitation expired."
            }
        }
        .onChange(of: state.rejectedSession) { if state.rejectedSession { endSession() } }
        .onChange(of: OperationsRefresh(route: route, active: active)) { reset() }
        .onDisappear { reset() }
        .alert("Revoke Paired Device?", isPresented: $showingRevoke, presenting: revokeTarget) { target in
            Button("Revoke", role: .destructive) {
                revokeTarget = nil
                guard active else { return }
                Task {
                    await state.revoke(target.id, using: api)
                    if state.rejectedSession { endSession() }
                }
            }
            Button("Cancel", role: .cancel) { revokeTarget = nil }
        } message: { target in
            Text("\(target.name) will lose access to Marten. If it is this device, your session ends. This does not remove a Tailscale node or revoke a Tailscale auth key.")
        }
    }

    private func startInvite(_ embedded: Bool) {
        guard active, state.ready, inviteRequest == nil, state.listing?.enabled == true else { return }
        var submitted: String? = embedded ? key : nil
        key = ""
        let request = UUID()
        inviteRequest = request
        inviteNotice = nil
        Task {
            guard inviteRequest == request else { submitted = nil; return }
            do {
                let created: DeviceInvite
                do {
                    defer { submitted = nil }
                    created = try await api.createDeviceInvite(key: submitted, mode: embedded ? .embedded : .direct)
                }
                guard inviteRequest == request else { return }
                guard let image = created.image(at: .now) else { throw ClientError.pairExpired }
                inviteExpiry = created.expiresAt
                qr = image
            } catch {
                guard inviteRequest == request else { return }
                inviteNotice = "No QR available. A request may still count against the server’s invitation limit until expiry. \(safeMessage(error))"
            }
            if inviteRequest == request { inviteRequest = nil }
        }
    }

    private func refresh() async { if active { await state.refresh(api) } }
    private func clearInvite() { key = ""; inviteExpiry = nil; qr = nil; inviteRequest = nil; inviteNotice = nil }
    private func reset() { clearInvite(); revokeTarget = nil; showingRevoke = false }
}
