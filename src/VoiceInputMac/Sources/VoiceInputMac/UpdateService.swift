import AppKit
import CryptoKit
import Foundation

struct AvailableUpdate: Equatable {
    let tag: String
    let version: [Int]
    let assetURL: URL
    let sha256: String
}

enum UpdateCheckResult: Equatable {
    case upToDate
    case available(AvailableUpdate)
    case failed(String)
}

/// Manual GitHub release updates. An update is accepted only when GitHub supplies a SHA-256
/// digest and the downloaded app has this app's bundle identifier and signing Team ID.
final class UpdateService {
    static let repository = "fafa-npu/VoiceInput"
    static let releasePage = URL(string: "https://github.com/\(repository)/releases/latest")!

    private let session: URLSession

    init(session: URLSession = .shared) { self.session = session }

    func check() async -> UpdateCheckResult {
        var request = URLRequest(
            url: URL(string: "https://api.github.com/repos/\(Self.repository)/releases/latest")!)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("gujiguji-mac-updater", forHTTPHeaderField: "User-Agent")
        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
                return .failed("GitHub did not return the latest release.")
            }
            let release = try JSONDecoder().decode(GitHubRelease.self, from: data)
            guard let latest = Self.parseVersion(release.tagName) else {
                return .failed("The release version is invalid.")
            }
            guard Self.compare(latest, Self.currentVersion) == .orderedDescending else {
                return .upToDate
            }
            let names = ["gujiguji-mac.zip", "gujiguji-macos.zip", "gujiguji.app.zip"]
            guard let asset = release.assets.first(where: { names.contains($0.name.lowercased()) }),
                  let digest = Self.parseDigest(asset.digest) else {
                return .failed("The macOS release or its SHA-256 digest is missing.")
            }
            return .available(AvailableUpdate(
                tag: release.tagName,
                version: latest,
                assetURL: asset.browserDownloadURL,
                sha256: digest))
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    /// Downloads and validates the update, then starts a detached helper that swaps the app after
    /// this process exits. The caller should terminate NSApplication only after this returns true.
    func stageAndApply(_ update: AvailableUpdate) async throws -> Bool {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("gujiguji-update-\(UUID().uuidString)", isDirectory: true)
        let archive = root.appendingPathComponent("gujiguji.zip")
        let expanded = root.appendingPathComponent("expanded", isDirectory: true)
        try FileManager.default.createDirectory(at: expanded, withIntermediateDirectories: true)

        do {
            let (downloaded, response) = try await session.download(from: update.assetURL)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
                throw SpeechFault(.network, "The update download failed.")
            }
            try FileManager.default.moveItem(at: downloaded, to: archive)
            guard try Self.sha256(archive).caseInsensitiveCompare(update.sha256) == .orderedSame else {
                throw SpeechFault(.service, "The update failed its SHA-256 integrity check.")
            }

            try Self.run("/usr/bin/ditto", ["-x", "-k", archive.path, expanded.path])
            guard let candidate = Self.findApp(in: expanded) else {
                throw SpeechFault(.service, "The update does not contain gujiguji.app.")
            }
            try Self.verify(candidate, expectedVersion: update.version)
            guard let destination = Self.installedAppURL else {
                throw SpeechFault(.service, "Run gujiguji from the Applications folder to install updates.")
            }

            let helper = root.appendingPathComponent("apply-update.zsh")
            try Self.helper.write(to: helper, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: helper.path)
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/bin/zsh")
            let healthToken = UUID().uuidString
            let expectedVersion = update.version.map(String.init).joined(separator: ".")
            process.arguments = [helper.path, String(ProcessInfo.processInfo.processIdentifier),
                                 candidate.path, destination.path, root.path,
                                 healthToken, expectedVersion]
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            try process.run()
            return true
        } catch {
            try? FileManager.default.removeItem(at: root)
            throw error
        }
    }

    static var currentVersion: [Int] {
        parseVersion(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
                     ?? "0.2.15") ?? [0]
    }

    static func parseVersion(_ value: String?) -> [Int]? {
        guard let value else { return nil }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
        let pieces = trimmed.split(separator: ".")
        guard !pieces.isEmpty, pieces.allSatisfy({ Int($0) != nil }) else { return nil }
        return pieces.map { Int($0)! }
    }

    static func compare(_ left: [Int], _ right: [Int]) -> ComparisonResult {
        for index in 0..<max(left.count, right.count) {
            let a = index < left.count ? left[index] : 0
            let b = index < right.count ? right[index] : 0
            if a < b { return .orderedAscending }
            if a > b { return .orderedDescending }
        }
        return .orderedSame
    }

    static func parseDigest(_ digest: String?) -> String? {
        guard let digest, digest.lowercased().hasPrefix("sha256:") else { return nil }
        let hash = String(digest.dropFirst(7))
        guard hash.count == 64, hash.allSatisfy({ $0.isHexDigit }) else { return nil }
        return hash.lowercased()
    }

    private static func sha256(_ url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hash = SHA256()
        while let data = try handle.read(upToCount: 1024 * 1024), !data.isEmpty { hash.update(data: data) }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private static func findApp(in directory: URL) -> URL? {
        let keys: [URLResourceKey] = [.isDirectoryKey]
        let enumerator = FileManager.default.enumerator(
            at: directory, includingPropertiesForKeys: keys,
            options: [.skipsHiddenFiles, .skipsPackageDescendants])
        return enumerator?.compactMap { $0 as? URL }.first(where: {
            $0.pathExtension == "app" && $0.lastPathComponent.caseInsensitiveCompare("gujiguji.app") == .orderedSame
        })
    }

    private static var installedAppURL: URL? {
        var url = Bundle.main.bundleURL
        guard url.pathExtension == "app", url.path.hasPrefix("/Applications/") else { return nil }
        url.resolveSymlinksInPath()
        return url
    }

    private static func verify(_ app: URL, expectedVersion: [Int]) throws {
        guard let bundle = Bundle(url: app), bundle.bundleIdentifier == "com.gujiguji.voiceinput" else {
            throw SpeechFault(.service, "The update has the wrong bundle identifier.")
        }
        guard let candidateVersion = parseVersion(
            bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String),
              candidateVersion == expectedVersion else {
            throw SpeechFault(.service, "The update bundle version does not match the GitHub release.")
        }
        let runningTeam = try teamIdentifier(Bundle.main.bundleURL)
        let updateTeam = try teamIdentifier(app)
        guard !runningTeam.isEmpty, runningTeam == updateTeam else {
            throw SpeechFault(.service, "The update is not signed by the current gujiguji developer.")
        }
        try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])
        try run("/usr/sbin/spctl", ["--assess", "--type", "execute", "--verbose=2", app.path],
                captureError: true)
    }

    private static func teamIdentifier(_ app: URL) throws -> String {
        let output = try run("/usr/bin/codesign", ["-dvv", app.path], captureError: true)
        return output.split(separator: "\n")
            .first(where: { $0.hasPrefix("TeamIdentifier=") })
            .map { String($0.dropFirst("TeamIdentifier=".count)) } ?? ""
    }

    @discardableResult
    private static func run(_ executable: String, _ arguments: [String], captureError: Bool = false) throws -> String {
        let process = Process()
        let pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.standardOutput = pipe
        process.standardError = captureError ? pipe : FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        let output = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        guard process.terminationStatus == 0 else {
            throw SpeechFault(.service, "Update validation failed.", detail: output)
        }
        return output
    }

    /// Acknowledges an updater-launched build only after the app has completed
    /// startup and kept its main run loop alive for the caller's grace period.
    static func acknowledgeUpdateLaunchIfRequested(healthy: Bool) {
        let arguments = CommandLine.arguments
        guard let rootIndex = arguments.firstIndex(of: "--update-health-dir"),
              let tokenIndex = arguments.firstIndex(of: "--update-health-token"),
              let versionIndex = arguments.firstIndex(of: "--update-health-version"),
              arguments.indices.contains(rootIndex + 1),
              arguments.indices.contains(tokenIndex + 1),
              arguments.indices.contains(versionIndex + 1) else { return }

        let root = URL(fileURLWithPath: arguments[rootIndex + 1], isDirectory: true)
            .standardizedFileURL
        let temporaryRoot = FileManager.default.temporaryDirectory.standardizedFileURL.path
        guard root.path.hasPrefix(temporaryRoot + "/gujiguji-update-"),
              root.lastPathComponent.hasPrefix("gujiguji-update-") else { return }

        let token = arguments[tokenIndex + 1]
        let expectedVersion = arguments[versionIndex + 1]
        let actualVersion = Bundle.main.object(
            forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
        guard !token.isEmpty, actualVersion == expectedVersion else { return }

        let payload = "\(token)\n\(actualVersion)\n\(ProcessInfo.processInfo.processIdentifier)\n"
        let name = healthy ? "ready" : "launched"
        let temporary = root.appendingPathComponent("\(name).tmp")
        let destination = root.appendingPathComponent(name)
        do {
            try payload.write(to: temporary, atomically: true, encoding: .utf8)
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: temporary, to: destination)
        } catch {
            AppLog.write("could not acknowledge updater health check: \(error.localizedDescription)")
        }
    }

    private struct GitHubRelease: Decodable {
        let tagName: String
        let assets: [Asset]
        enum CodingKeys: String, CodingKey { case tagName = "tag_name", assets }
    }

    private struct Asset: Decodable {
        let name: String
        let browserDownloadURL: URL
        let digest: String?
        enum CodingKeys: String, CodingKey {
            case name, digest
            case browserDownloadURL = "browser_download_url"
        }
    }

    private static let helper = #"""
#!/bin/zsh
set -euo pipefail
pid="$1"; source="$2"; target="$3"; root="$4"; token="$5"; version="$6"
backup="${target}.backup"
ready="$root/ready"
launched="$root/launched"
launched_pid=""
swapped=0

rollback() {
  set +e
  if [[ "$launched_pid" == <-> ]] && kill -0 "$launched_pid" 2>/dev/null; then
    kill "$launched_pid" 2>/dev/null
    for _ in {1..50}; do
      kill -0 "$launched_pid" 2>/dev/null || break
      sleep 0.1
    done
  fi
  if (( swapped )); then
    rm -rf "$target"
    [[ -d "$backup" ]] && mv "$backup" "$target"
    open "$target" >/dev/null 2>&1 || true
    swapped=0
  fi
  rm -rf "$root"
}
trap rollback EXIT INT TERM HUP

for _ in {1..100}; do
  kill -0 "$pid" 2>/dev/null || break
  sleep 0.1
done
rm -rf "$backup"
mv "$target" "$backup"
swapped=1
if ditto "$source" "$target" && \
   open -n "$target" --args \
     --update-health-dir "$root" \
     --update-health-token "$token" \
     --update-health-version "$version"; then
  for _ in {1..250}; do
    if [[ -z "$launched_pid" && -f "$launched" ]]; then
      launched_token="$(sed -n '1p' "$launched")"
      launched_version="$(sed -n '2p' "$launched")"
      candidate_pid="$(sed -n '3p' "$launched")"
      if [[ "$launched_token" == "$token" && "$launched_version" == "$version" && "$candidate_pid" == <-> ]]; then
        launched_pid="$candidate_pid"
      fi
    fi
    if [[ -z "$launched_pid" ]]; then
      candidate_pid="$(pgrep -f -- "$target/Contents/MacOS/gujiguji-mac" | head -n 1 || true)"
      [[ "$candidate_pid" == <-> ]] && launched_pid="$candidate_pid"
    fi
    if [[ -f "$ready" ]]; then
      ready_token="$(sed -n '1p' "$ready")"
      ready_version="$(sed -n '2p' "$ready")"
      ready_pid="$(sed -n '3p' "$ready")"
      if [[ "$ready_token" == "$token" && "$ready_version" == "$version" && "$ready_pid" == <-> && \
            ( -z "$launched_pid" || "$ready_pid" == "$launched_pid" ) ]] && \
         kill -0 "$ready_pid" 2>/dev/null; then
        swapped=0
        trap - EXIT INT TERM HUP
        rm -rf "$backup" "$root"
        exit 0
      fi
    fi
    sleep 0.1
  done
fi
exit 1
"""#
}
