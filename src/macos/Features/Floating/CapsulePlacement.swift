import Foundation
import CoreGraphics

/// All geometry is expressed in AppKit screen points (positive Y upwards).
/// This layer has no screen cache, timers, mouse access, or window side effects.
enum CapsuleDockEdge: String, Codable, CaseIterable { case left, right, top, bottom }

struct CapsuleScreenGeometry {
    let id: String
    let frame: CGRect
    let visibleFrame: CGRect
}

struct CapsulePlacement: Equatable {
    var compact: CGRect
    var detail: CGRect
    let opensRight: Bool
    let opensUp: Bool

    func translated(x: CGFloat, y: CGFloat) -> CapsulePlacement {
        CapsulePlacement(compact: compact.offsetBy(dx: x, dy: y), detail: detail.offsetBy(dx: x, dy: y),
                         opensRight: opensRight, opensUp: opensUp)
    }

    static func fitted(_ rect: CGRect, in work: CGRect) -> CGRect {
        let width = min(max(0, rect.width), max(0, work.width))
        let height = min(max(0, rect.height), max(0, work.height))
        return CGRect(x: min(max(rect.minX, work.minX), work.maxX - width),
                      y: min(max(rect.minY, work.minY), work.maxY - height), width: width, height: height)
    }

    static func resolve(compact: CGRect, work: CGRect, size: CGSize = CGSize(width: 336, height: 410), margin: CGFloat = 12) -> CapsulePlacement {
        // Do not spend the last available points on margins on a tiny display.
        let insetX = work.width >= size.width + margin * 2 ? margin : 0
        let insetY = work.height >= size.height + margin * 2 ? margin : 0
        let safe = work.insetBy(dx: insetX, dy: insetY)
        let width = min(size.width, safe.width), height = min(size.height, safe.height)
        let right = compact.maxX - width < safe.minX && compact.minX + width <= safe.maxX
        let up = compact.maxY - height < safe.minY && compact.minY + height <= safe.maxY
        let target = CGRect(x: right ? compact.minX : compact.maxX - width,
                            y: up ? compact.minY : compact.maxY - height, width: width, height: height)
        // Margins guide direction selection, but must not move the anchored
        // edge of a docked pill. At an edge the margin is intentionally relaxed.
        return CapsulePlacement(compact: fitted(compact, in: work), detail: fitted(target, in: work), opensRight: right, opensUp: up)
    }

    static func screen(for rect: CGRect, pointer: CGPoint? = nil, screens: [CapsuleScreenGeometry], preferredID: String? = nil) -> CapsuleScreenGeometry? {
        if let pointer = pointer, let screen = screens.first(where: { $0.frame.contains(pointer) }) { return screen }
        let overlaps = screens.map { screen -> (CapsuleScreenGeometry, CGFloat) in
            let intersection = screen.frame.intersection(rect)
            return (screen, intersection.isNull ? 0 : intersection.width * intersection.height)
        }
        if let best = overlaps.max(by: { $0.1 < $1.1 }), best.1 > 0 { return best.0 }
        return screens.first(where: { $0.id == preferredID }) ?? screens.first
    }

    /// Called after release, once the pointer has selected the destination
    /// screen. Shared seams dock exactly like outer edges; dragging stays free.
    static func dockingEdge(compact: CGRect, screen: CapsuleScreenGeometry, threshold: CGFloat = 16) -> CapsuleDockEdge? {
        let work = screen.visibleFrame
        let distances: [(CapsuleDockEdge, CGFloat)] = [(.left, abs(compact.minX - work.minX)), (.right, abs(compact.maxX - work.maxX)),
                                                     (.top, abs(compact.maxY - work.maxY)), (.bottom, abs(compact.minY - work.minY))]
        return distances.filter { $0.1 <= threshold }
            .min(by: { $0.1 < $1.1 })?.0
    }

    /// Expanded drops use the panel boundary, not its (possibly far-away)
    /// compact anchor. Rank corners by pointer distance on the selected screen.
    static func expandedDropEdge(panel: CGRect, pointer: CGPoint, screen: CapsuleScreenGeometry) -> CapsuleDockEdge? {
        let work = screen.visibleFrame
        let candidates: [(CapsuleDockEdge, Bool, CGFloat)] = [
            (.left, panel.minX <= work.minX + 16, abs(pointer.x - work.minX)),
            (.right, panel.maxX >= work.maxX - 16, abs(pointer.x - work.maxX)),
            (.top, panel.maxY >= work.maxY - 16, abs(pointer.y - work.maxY)),
            (.bottom, panel.minY <= work.minY + 16, abs(pointer.y - work.minY))]
        return candidates.sorted { $0.2 < $1.2 }.first { $0.1 }?.0
    }

    static func docked(_ compact: CGRect, edge: CapsuleDockEdge, work: CGRect) -> CGRect {
        var result = fitted(compact, in: work)
        switch edge {
        case .left: result.origin.x = work.minX
        case .right: result.origin.x = work.maxX - result.width
        case .top: result.origin.y = work.maxY - result.height
        case .bottom: result.origin.y = work.minY
        }
        return result
    }

    static func tab(compact: CGRect, edge: CapsuleDockEdge, work: CGRect, showsMonitor: Bool = false) -> CGRect {
        let size = edge == .left || edge == .right
            ? CGSize(width: 44, height: showsMonitor ? 96 : 68)
            : CGSize(width: showsMonitor ? 124 : 88, height: 28)
        let centered = CGRect(x: compact.midX - size.width / 2, y: compact.midY - size.height / 2, width: size.width, height: size.height)
        return docked(centered, edge: edge, work: work)
    }
}

/// The versioned preference stores a relative position for a docked window and
/// a separate free origin. Legacy absolute positions remain a one-time input.
struct CapsuleSavedPosition: Codable, Equatable {
    var version = 1
    var screenID: String
    var edge: CapsuleDockEdge?
    var fraction: Double
    var freeX: Double
    var freeY: Double

    init(compact: CGRect, screen: CapsuleScreenGeometry, edge: CapsuleDockEdge?, freeOrigin: CGPoint) {
        screenID = screen.id; self.edge = edge; freeX = Double(freeOrigin.x); freeY = Double(freeOrigin.y)
        let vertical = edge == .left || edge == .right, work = screen.visibleFrame
        let distance = vertical ? compact.minY - work.minY : compact.minX - work.minX
        let span = vertical ? work.height - compact.height : work.width - compact.width
        fraction = Double(min(1, max(0, span > 0 ? distance / span : 0)))
    }

    func restored(screens: [CapsuleScreenGeometry], size: CGSize = CGSize(width: 76, height: 76)) -> (CGRect, CapsuleDockEdge?)? {
        guard version == 1, fraction.isFinite, freeX.isFinite, freeY.isFinite else { return nil }
        let free = CGRect(x: freeX, y: freeY, width: size.width, height: size.height)
        guard let screen = screens.first(where: { $0.id == screenID }) ?? CapsulePlacement.screen(for: free, screens: screens) else { return nil }
        let work = screen.visibleFrame
        guard let edge = edge else { return (CapsulePlacement.fitted(free, in: work), nil) }
        var rect = CapsulePlacement.fitted(free, in: work)
        let relative = CGFloat(min(1, max(0, fraction)))
        if edge == .left || edge == .right { rect.origin.y = work.minY + max(0, work.height - rect.height) * relative }
        else { rect.origin.x = work.minX + max(0, work.width - rect.width) * relative }
        rect = CapsulePlacement.docked(rect, edge: edge, work: work)
        return (rect, edge)
    }
}
