import AppKit
import SwiftTerm
import SwiftUI

enum Theme: String, CaseIterable, Identifiable {
    case black, dark, graphite, midnight, dracula, nord, oneDark, gruvbox, solarizedDark, light, solarizedLight

    var id: String { rawValue }

    var label: String {
        switch self {
        case .oneDark: return "One Dark"
        case .solarizedDark: return "Solarized Dark"
        case .solarizedLight: return "Solarized Light"
        default: return rawValue.capitalized
        }
    }

    static var current: Theme {
        Theme(rawValue: UserDefaults.standard.string(forKey: Prefs.theme) ?? "") ?? .dark
    }

    var isLight: Bool { self == .light || self == .solarizedLight }
    var colorScheme: ColorScheme { isLight ? .light : .dark }

    /// background, chrome (sidebar/header), foreground
    private var colors: (bg: UInt32, chrome: UInt32, fg: UInt32) {
        switch self {
        case .black: return (0x000000, 0x0F0F0F, 0xE6E6E6)
        case .dark: return (0x1C1C1F, 0x252529, 0xE6E6E6)
        case .graphite: return (0x2B2B2B, 0x363636, 0xE6E6E6)
        case .midnight: return (0x0D1117, 0x161B22, 0xC9D1D9)
        case .dracula: return (0x282A36, 0x21222C, 0xF8F8F2)
        case .nord: return (0x2E3440, 0x3B4252, 0xD8DEE9)
        case .oneDark: return (0x282C34, 0x21252B, 0xABB2BF)
        case .gruvbox: return (0x282828, 0x1D2021, 0xEBDBB2)
        case .solarizedDark: return (0x002B36, 0x073642, 0x839496)
        case .light: return (0xFBFBFB, 0xEFEFF1, 0x1F1F1F)
        case .solarizedLight: return (0xFDF6E3, 0xEEE8D5, 0x657B83)
        }
    }

    var background: NSColor { NSColor(hex: colors.bg) }
    var chrome: NSColor { NSColor(hex: colors.chrome) }
    var foreground: NSColor { NSColor(hex: colors.fg) }

    /// 16 ANSI colours (normal 0-7, bright 8-15).
    var palette: [SwiftTerm.Color] {
        let hex: [UInt32]
        switch self {
        case .midnight:
            hex = [0x484F58, 0xFF7B72, 0x3FB950, 0xD29922, 0x58A6FF, 0xBC8CFF, 0x39C5CF, 0xB1BAC4,
                   0x6E7681, 0xFFA198, 0x56D364, 0xE3B341, 0x79C0FF, 0xD2A8FF, 0x56D4DD, 0xF0F6FC]
        case .dracula:
            hex = [0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C, 0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2,
                   0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5, 0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF]
        case .nord:
            hex = [0x3B4252, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x88C0D0, 0xE5E9F0,
                   0x4C566A, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x8FBCBB, 0xECEFF4]
        case .oneDark:
            hex = [0x282C34, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xABB2BF,
                   0x5C6370, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xFFFFFF]
        case .gruvbox:
            hex = [0x282828, 0xCC241D, 0x98971A, 0xD79921, 0x458588, 0xB16286, 0x689D6A, 0xA89984,
                   0x928374, 0xFB4934, 0xB8BB26, 0xFABD2F, 0x83A598, 0xD3869B, 0x8EC07C, 0xEBDBB2]
        case .solarizedDark, .solarizedLight:
            hex = [0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
                   0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3]
        case .light:
            hex = [0x000000, 0xC91B00, 0x00A600, 0x9A7B00, 0x0225C7, 0xCA30C7, 0x00A2B2, 0xBFBFBF,
                   0x686868, 0xFF6E67, 0x2EB84A, 0xC7A500, 0x6871FF, 0xFF77FF, 0x1BA8B5, 0xFFFFFF]
        case .black, .dark, .graphite:
            hex = [0x000000, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
                   0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF]
        }
        return hex.map { v in
            SwiftTerm.Color(red: UInt16((v >> 16) & 0xFF) * 257,
                            green: UInt16((v >> 8) & 0xFF) * 257,
                            blue: UInt16(v & 0xFF) * 257)
        }
    }
}

extension NSColor {
    convenience init(hex: UInt32) {
        self.init(calibratedRed: CGFloat((hex >> 16) & 0xFF) / 255,
                  green: CGFloat((hex >> 8) & 0xFF) / 255,
                  blue: CGFloat(hex & 0xFF) / 255,
                  alpha: 1)
    }
}

enum Fonts {
    /// Installed fixed-pitch families, for the Settings picker.
    static let monospaceFamilies: [String] = NSFontManager.shared.availableFontFamilies
        .filter { NSFont(name: $0, size: 12)?.isFixedPitch == true }
        .sorted()

    /// Terminal font from Settings; empty family means the system monospaced font.
    static func terminal() -> NSFont {
        let family = UserDefaults.standard.string(forKey: Prefs.fontFamily) ?? ""
        let stored = UserDefaults.standard.double(forKey: Prefs.fontSize)
        let size = stored > 0 ? stored : 13
        return (family.isEmpty ? nil : NSFont(name: family, size: size))
            ?? NSFont.monospacedSystemFont(ofSize: size, weight: .regular)
    }
}
