import AppKit

/// Floating control bar shown during Preview Day: play/pause, a 24-hour scrubber, the time, and Done.
/// The wallpaper windows themselves are click-through (and sit under the desktop icons), so scrubbing
/// happens here and is pushed to the scene.
final class PreviewPanel: NSPanel {
    /// Called with minutes since local midnight (0..<1440) while the user drags the scrubber.
    var onScrub: ((Double) -> Void)?
    var onTogglePlay: (() -> Void)?
    var onDone: (() -> Void)?

    private let slider = NSSlider(value: 0, minValue: 0, maxValue: 1439, target: nil, action: nil)
    private let timeLabel = NSTextField(labelWithString: "00:00")
    private let playButton = NSButton(frame: .zero)
    private let doneButton = NSButton(frame: .zero)
    private(set) var isScrubbing = false

    init() {
        super.init(contentRect: NSRect(x: 0, y: 0, width: 600, height: 52),
                   styleMask: [.borderless, .nonactivatingPanel],
                   backing: .buffered,
                   defer: false)
        isFloatingPanel = true
        level = .floating
        hidesOnDeactivate = false
        isMovableByWindowBackground = true
        isReleasedWhenClosed = false
        backgroundColor = .clear
        isOpaque = false
        hasShadow = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]

        let blur = NSVisualEffectView(frame: NSRect(x: 0, y: 0, width: 600, height: 52))
        blur.material = .hudWindow
        blur.blendingMode = .behindWindow
        blur.state = .active
        blur.wantsLayer = true
        blur.layer?.cornerRadius = 14
        blur.layer?.masksToBounds = true
        blur.autoresizingMask = [.width, .height]
        contentView = blur

        playButton.bezelStyle = .regularSquare
        playButton.isBordered = false
        playButton.imagePosition = .imageOnly
        playButton.target = self
        playButton.action = #selector(playPressed(_:))
        setPlaying(true)

        slider.target = self
        slider.action = #selector(sliderChanged(_:))
        slider.isContinuous = true
        slider.numberOfTickMarks = 25
        slider.allowsTickMarkValuesOnly = false

        timeLabel.font = NSFont.monospacedDigitSystemFont(ofSize: 13, weight: .medium)
        timeLabel.alignment = .right

        doneButton.title = "Done"
        doneButton.bezelStyle = .rounded
        doneButton.target = self
        doneButton.action = #selector(donePressed(_:))

        let stack = NSStackView(views: [playButton, slider, timeLabel, doneButton])
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 12
        stack.edgeInsets = NSEdgeInsets(top: 0, left: 16, bottom: 0, right: 12)
        stack.translatesAutoresizingMaskIntoConstraints = false
        blur.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: blur.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: blur.trailingAnchor),
            stack.topAnchor.constraint(equalTo: blur.topAnchor),
            stack.bottomAnchor.constraint(equalTo: blur.bottomAnchor),
            playButton.widthAnchor.constraint(equalToConstant: 22),
            timeLabel.widthAnchor.constraint(equalToConstant: 48),
            slider.widthAnchor.constraint(greaterThanOrEqualToConstant: 380),
        ])
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not supported")
    }

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    /// Place the bar just above the Dock on the main screen.
    func positionOnMainScreen() {
        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let vf = screen.visibleFrame
        let size = frame.size
        setFrameOrigin(NSPoint(x: vf.midX - size.width / 2, y: vf.minY + 28))
    }

    func setMinutes(_ minutes: Double) {
        guard !isScrubbing else { return }
        slider.doubleValue = minutes
        updateLabel(minutes)
    }

    func setPlaying(_ playing: Bool) {
        let name = playing ? "pause.fill" : "play.fill"
        playButton.image = NSImage(systemSymbolName: name, accessibilityDescription: playing ? "Pause" : "Play")
        playButton.toolTip = playing ? "Pause" : "Play"
    }

    private func updateLabel(_ minutes: Double) {
        let m = Int(minutes.rounded(.down)) % 1440
        timeLabel.stringValue = String(format: "%02d:%02d", m / 60, m % 60)
    }

    @objc private func sliderChanged(_ sender: NSSlider) {
        // Only treat it as an active drag while the mouse is actually down; anything else ends it.
        let type = NSApp.currentEvent?.type
        isScrubbing = (type == .leftMouseDown || type == .leftMouseDragged)
        updateLabel(sender.doubleValue)
        onScrub?(sender.doubleValue)
    }

    @objc private func playPressed(_ sender: NSButton) {
        onTogglePlay?()
    }

    @objc private func donePressed(_ sender: NSButton) {
        onDone?()
    }
}
