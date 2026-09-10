import AppKit

/// Never enter AppKit's terminateLater nested loop from a main-queue menu action.
/// That loop can starve the main-queue cleanup completion it is waiting for.
final class AsyncTermination {
    private var pending = false
    private var ready = false
    func request(_ app: NSApplication, cleanup: (@escaping () -> Void) -> Void) -> NSApplication.TerminateReply {
        if ready { return .terminateNow }
        if pending { return .terminateCancel }
        pending = true
        cleanup {
            DispatchQueue.main.async {
                self.ready = true
                app.terminate(nil)
            }
        }
        return .terminateCancel
    }
}
