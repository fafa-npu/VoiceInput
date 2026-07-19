import AppKit
import Foundation

/// Minimal OAuth device-code client for Azure Cognitive Services. Refresh tokens live in Keychain;
/// the browser/code prompt therefore normally appears only on the first sign-in for a tenant.
actor EntraTokenProvider {
    static let cognitiveServicesScope = "https://cognitiveservices.azure.com/.default"
    // Microsoft's public developer-sign-on client, also used by Azure Identity's default
    // InteractiveBrowserCredential path in the Windows app.
    private static let clientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"

    private let tenant: String
    private let session: URLSession
    private var cachedToken: String?
    private var expiresAt = Date.distantPast
    private var inFlight: PendingRequest?

    init(tenantId: String, session: URLSession = .shared) {
        let trimmed = tenantId.trimmingCharacters(in: .whitespacesAndNewlines)
        tenant = trimmed.isEmpty ? "organizations" : trimmed
        self.session = session
    }

    func prewarm(interactive: Bool = false) async throws {
        _ = try await accessToken(interactive: interactive)
    }

    /// Returns a cached/refreshed token. Device-code sign-in is only allowed when
    /// explicitly initiated from Settings, never from an active dictation path.
    func accessToken(interactive: Bool = false) async throws -> String {
        if let cachedToken, expiresAt.timeIntervalSinceNow > 90 { return cachedToken }
        if let inFlight {
            guard interactive || !inFlight.interactive else { throw Self.signInRequired }
            return try await inFlight.task.value
        }
        let task = Task { [weak self] () throws -> String in
            guard let self else { throw CancellationError() }
            return try await self.obtainToken(interactive: interactive)
        }
        inFlight = PendingRequest(task: task, interactive: interactive)
        do {
            let token = try await task.value
            inFlight = nil
            return token
        } catch {
            inFlight = nil
            throw error
        }
    }

    private func obtainToken(interactive: Bool) async throws -> String {
        if let refresh = KeychainStore.data(keychainAccount).flatMap({
            try? JSONDecoder().decode(SavedToken.self, from: $0)
        })?.refreshToken {
            do { return try await exchange(["grant_type": "refresh_token", "refresh_token": refresh]).accessToken }
            catch is OAuthInvalidRefresh { KeychainStore.remove(keychainAccount) }
        }
        guard interactive else { throw Self.signInRequired }
        return try await signInWithDeviceCode()
    }

    private static var signInRequired: SpeechFault {
        SpeechFault(.authentication, "Azure sign-in is required. Open Settings, save the Azure configuration, and complete sign-in before dictating.")
    }

    private func signInWithDeviceCode() async throws -> String {
        let response: DeviceCode = try await post(
            path: "devicecode",
            parameters: ["client_id": Self.clientId,
                         "scope": "\(Self.cognitiveServicesScope) offline_access openid profile"])
        guard let verificationURL = URL(string: response.verificationURI) else {
            throw SpeechFault(.authentication, "Microsoft returned an invalid sign-in URL.")
        }
        await MainActor.run {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(response.userCode, forType: .string)
            NSWorkspace.shared.open(verificationURL)
            let alert = NSAlert()
            alert.messageText = "Sign in to Azure"
            alert.informativeText = "Enter code \(response.userCode) in the browser. The code has been copied."
            alert.alertStyle = .informational
            alert.addButton(withTitle: "Continue")
            alert.runModal()
        }

        var interval = max(2, response.interval ?? 5)
        let deadline = Date().addingTimeInterval(TimeInterval(response.expiresIn))
        while Date() < deadline {
            try await Task.sleep(for: .seconds(interval))
            do {
                let token = try await exchange([
                    "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
                    "device_code": response.deviceCode,
                ])
                return token.accessToken
            } catch let fault as OAuthPending where fault.code == "authorization_pending" {
                continue
            } catch let fault as OAuthPending where fault.code == "slow_down" {
                interval += 5
            }
        }
        throw SpeechFault(.authentication, "Azure sign-in timed out. Open Settings and try again.")
    }

    private func exchange(_ grant: [String: String]) async throws -> TokenResponse {
        var fields = grant
        fields["client_id"] = Self.clientId
        fields["scope"] = "\(Self.cognitiveServicesScope) offline_access openid profile"
        let token: TokenResponse
        do { token = try await post(path: "token", parameters: fields) }
        catch let pending as OAuthPending { throw pending }
        cachedToken = token.accessToken
        expiresAt = Date().addingTimeInterval(TimeInterval(max(60, token.expiresIn)))
        if let refresh = token.refreshToken {
            let saved = SavedToken(refreshToken: refresh)
            if let data = try? JSONEncoder().encode(saved) { try? KeychainStore.set(data, for: keychainAccount) }
        }
        return token
    }

    private func post<T: Decodable>(path: String, parameters: [String: String]) async throws -> T {
        let escapedTenant = tenant.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? "organizations"
        let url = URL(string: "https://login.microsoftonline.com/\(escapedTenant)/oauth2/v2.0/\(path)")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        var components = URLComponents()
        components.queryItems = parameters.sorted(by: { $0.key < $1.key })
            .map { URLQueryItem(name: $0.key, value: $0.value) }
        request.httpBody = components.percentEncodedQuery?.data(using: .utf8)
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw SpeechFault(.network, "Azure sign-in returned no HTTP response.")
        }
        if (200..<300).contains(http.statusCode) { return try JSONDecoder().decode(T.self, from: data) }
        let problem = (try? JSONDecoder().decode(OAuthProblem.self, from: data))
        if let code = problem?.error, code == "authorization_pending" || code == "slow_down" {
            throw OAuthPending(code: code)
        }
        if problem?.error == "invalid_grant" { throw OAuthInvalidRefresh() }
        throw SpeechFault(.authentication, problem?.errorDescription ?? "Azure sign-in failed.")
    }

    private var keychainAccount: String { "entra-token-\(tenant.lowercased())" }

    private struct PendingRequest {
        let task: Task<String, Error>
        let interactive: Bool
    }

    private struct DeviceCode: Decodable {
        let deviceCode: String
        let userCode: String
        let verificationURI: String
        let expiresIn: Int
        let interval: Int?
        enum CodingKeys: String, CodingKey {
            case deviceCode = "device_code", userCode = "user_code"
            case verificationURI = "verification_uri", expiresIn = "expires_in", interval
        }
    }

    private struct TokenResponse: Decodable {
        let accessToken: String
        let refreshToken: String?
        let expiresIn: Int
        enum CodingKeys: String, CodingKey {
            case accessToken = "access_token", refreshToken = "refresh_token", expiresIn = "expires_in"
        }
    }

    private struct SavedToken: Codable { let refreshToken: String }
    private struct OAuthPending: Error { let code: String }
    private struct OAuthInvalidRefresh: Error { }
    private struct OAuthProblem: Decodable {
        let error: String?
        let errorDescription: String?
        enum CodingKeys: String, CodingKey { case error; case errorDescription = "error_description" }
    }
}
