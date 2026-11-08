// 部署前只需要改这里。
window.SI_CONFIG = {
  // 版本号的兜底值：页面会从 GitHub 最新 Release 读取版本号，读不到（网络问题、接口限流）时才显示这个。
  version: "0.5.0",
  // GitHub 仓库地址，例如 "https://github.com/<用户名>/serverisland"。
  // 填好后：GitHub 链接、Issues、下载按钮（指向 Releases 最新版）都会自动生效。
  repo: "https://github.com/zhyan0603/ServerIsland",
  // 可选：DMG 的直接下载地址。留空时下载按钮打开 Releases 最新版页面。
  //   例如 repo + "/releases/latest/download/ServerIsland.dmg"（Releases 里的文件要固定叫这个名字）
  downloadURL: "",
};
