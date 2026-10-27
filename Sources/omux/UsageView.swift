import SwiftUI

/// One compact line of Claude limits: `5h 1%  Week 32%  Fable 35%`, each with a
/// hairline bar. Hovering a limit swaps its text for the reset time.
struct UsageView: View {
    @StateObject private var monitor = UsageMonitor()
    @State private var hovered: String?
    @AppStorage(Prefs.usageInterval) private var usageInterval = 300.0

    var body: some View {
        HStack(spacing: 10) {
            if monitor.limits.isEmpty {
                Text(monitor.error ?? "Claude usage…")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            ForEach(monitor.limits) { limit in
                VStack(alignment: .leading, spacing: 2) {
                    Text(hovered == limit.id ? limit.resetText : "\(limit.label) \(limit.percent)%")
                        .font(.caption2.monospacedDigit())
                        .foregroundStyle(hovered == limit.id ? .primary : .secondary)
                        .lineLimit(1)
                        .fixedSize()
                    GeometryReader { geo in
                        ZStack(alignment: .leading) {
                            Capsule().fill(.quaternary)
                            Capsule().fill(limit.tint)
                                .frame(width: geo.size.width * CGFloat(min(limit.percent, 100)) / 100)
                        }
                    }
                    .frame(height: 3)
                }
                .onHover { hovered = $0 ? limit.id : nil }
                .help(limit.resetText)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .onAppear { monitor.start() }
        .onDisappear { monitor.stop() }
        .onChange(of: usageInterval) { _, seconds in monitor.schedule(every: seconds) }
    }
}
