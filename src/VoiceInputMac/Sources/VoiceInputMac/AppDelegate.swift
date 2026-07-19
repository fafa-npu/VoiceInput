import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var controller: AppController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        UpdateService.acknowledgeUpdateLaunchIfRequested(healthy: false)
        let controller = AppController()
        self.controller = controller
        controller.start()
        DispatchQueue.main.asyncAfter(deadline: .now() + 4) {
            UpdateService.acknowledgeUpdateLaunchIfRequested(healthy: true)
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationWillTerminate(_ notification: Notification) {
        controller?.shutdown()
        controller = nil
    }
}
