import AppKit

struct StatusMenuPlacement {
    let anchor: NSPoint
    let confinement: NSRect
}

/// All geometry here is in Cocoa screen coordinates, whose positive Y points up.
enum StatusMenuGeometry {
    static func screenIndex(button: NSRect, screens: [NSRect]) -> Int? {
        let center = NSPoint(x: button.midX, y: button.midY)
        if let index = screens.firstIndex(where: { $0.contains(center) }) { return index }
        guard let index = screens.indices.max(by: {
            let a = screens[$0].intersection(button), b = screens[$1].intersection(button)
            return (a.isNull ? 0 : a.width * a.height) < (b.isNull ? 0 : b.width * b.height)
        }), !screens[index].intersection(button).isEmpty else { return nil }
        return index
    }
    static func placement(button: NSRect, screen: NSRect, visible: NSRect, menuWidth: CGFloat, menuBarHeight: CGFloat) -> StatusMenuPlacement {
        let intersection = screen.intersection(visible)
        let usable = intersection.isEmpty ? screen : intersection
        let gap: CGFloat = 4
        // visibleFrame handles a visible menu bar/notch. The current button and
        // measured bar height also cover a temporarily revealed, auto-hidden bar.
        let barBottom = min(button.minY, usable.maxY, screen.maxY - max(0, menuBarHeight))
        let top = max(usable.minY, barBottom - gap)
        let left = usable.minX + min(gap, usable.width / 2)
        let right = usable.maxX - min(gap, usable.width / 2)
        let available = NSRect(x: left, y: usable.minY, width: max(0, right - left), height: max(0, top - usable.minY))
        let x = min(max(button.minX, left), max(left, right - max(0, menuWidth)))
        return StatusMenuPlacement(anchor: NSPoint(x: x, y: top), confinement: available)
    }
}

private func statusMenuPlacement(button: NSStatusBarButton, menuWidth: CGFloat, screen requestedScreen: NSScreen? = nil) -> StatusMenuPlacement? {
    guard let window = button.window else { return nil }
    // Converting the whole bounds rect avoids assuming whether the button is flipped.
    let bounds = window.convertToScreen(button.convert(button.bounds, to: nil))
    let screens = NSScreen.screens
    let index = StatusMenuGeometry.screenIndex(button: bounds, screens: screens.map { $0.frame })
    guard let screen = requestedScreen ?? index.map({ screens[$0] }) ?? window.screen else { return nil }
    let barHeight = max(NSStatusBar.system.thickness, bounds.height)
    return StatusMenuGeometry.placement(button: bounds, screen: screen.frame, visible: screen.visibleFrame,
                                        menuWidth: menuWidth, menuBarHeight: barHeight)
}

func popUpStatusMenu(_ menu: NSMenu, from button: NSStatusBarButton) {
    menu.update()
    guard let placement = statusMenuPlacement(button: button, menuWidth: menu.size.width) else { return }
    // Apple documents nil item as the menu content frame's TOP LEFT, and nil view
    // as screen coordinates: https://developer.apple.com/documentation/appkit/nsmenu/popup(positioning:at:in:)
    menu.popUp(positioning: nil, at: placement.anchor, in: nil)
}

func statusMenuConfinement(from button: NSStatusBarButton?, on screen: NSScreen?) -> NSRect {
    guard let button = button else { return .zero }
    // Give tall menus the area below the bar, so AppKit can scroll the menu there.
    // AppKit may override confinement when the available area is too small.
    return statusMenuPlacement(button: button, menuWidth: 0, screen: screen)?.confinement ?? .zero
}
