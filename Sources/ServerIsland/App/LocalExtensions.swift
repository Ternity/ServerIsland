import SwiftUI

/// 本机扩展：维护者本机上还在试验、不进仓库的功能（放在 Sources/ServerIsland/Private/，被 git 忽略）。
/// 没有这些文件时这里什么都不做，公开版本不受影响。
@MainActor
protocol LocalExtension: AnyObject {
    static func start(engine: MonitorEngine, store: ConfigStore)
    /// 设置窗口侧栏里单独的一页。
    static func settingsPage(store: ConfigStore) -> LocalSettingsPage
}

struct LocalSettingsPage {
    var title: String
    var subtitle: String
    var symbol: String
    var tint: Color
    var view: AnyView
}

@MainActor
enum LocalExtensions {
    static var type: LocalExtension.Type? {
        // 离屏预览（README、官网截图）里不出现。
        IslandPreview.active ? nil : NSClassFromString("SILocalExtension") as? LocalExtension.Type
    }
}
