import AppKit

/// Lull's menu bar icon: two slanted slats of sunlight, which also read as the "ll" in Lull.
/// Drawn in code as a template image, so it stays crisp at any scale and adapts to light/dark menu bars.
enum MenuBarIcon {
    static func make() -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { _ in
            NSColor.black.setFill()
            // Tall slat on the left.
            slat([NSPoint(x: 7.4, y: 15.5), NSPoint(x: 10.0, y: 15.5),
                  NSPoint(x: 6.9, y: 2.5), NSPoint(x: 4.3, y: 2.5)]).fill()
            // Shorter slat on the right, same slant.
            slat([NSPoint(x: 13.2, y: 12.0), NSPoint(x: 15.8, y: 12.0),
                  NSPoint(x: 13.5, y: 2.5), NSPoint(x: 10.9, y: 2.5)]).fill()
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "Lull"
        return image
    }

    private static func slat(_ points: [NSPoint]) -> NSBezierPath {
        let path = NSBezierPath()
        path.move(to: points[0])
        for point in points.dropFirst() { path.line(to: point) }
        path.close()
        return path
    }
}
