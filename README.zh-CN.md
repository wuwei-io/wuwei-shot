<p align="right"><a href="README.md">English</a> · <b>中文</b></p>

<p align="center">
  <img src="logo_256.png" width="104" alt="AltSnip">
</p>

<h1 align="center">AltSnip</h1>

<p align="center">
  <b>按 <code>Alt&nbsp;+&nbsp;A</code>,拖个框,截图就进了剪贴板。</b><br>
  一个极简、快速、零依赖的 Windows 截图 + 标注工具,全部装在一个约 50&nbsp;KB 的 <code>.exe</code> 里。
</p>

<p align="center">
  <img src="https://img.shields.io/github/v/release/KehuiPang/AltSnip?color=f4b740" alt="release">
  <img src="https://img.shields.io/badge/platform-Windows-0078D6" alt="platform">
  <img src="https://img.shields.io/badge/license-MIT-2ebe6e" alt="license">
  <img src="https://img.shields.io/badge/dependencies-none-e0762a" alt="no dependencies">
</p>

<p align="center">
  <img src="docs/demo.png" width="720" alt="AltSnip 演示 — 框选、箭头、文字、马赛克、复制">
</p>

<p align="center">
  <sub>↑ 动图为 APNG,现代浏览器均可播放</sub>
</p>

---

## 为什么做它

某天微信卡死,我想截个图都截不了,一怒之下自己写了这个。发现一个真正好用的截图工具也就一个 C# 文件的量,于是开源出来,免费给所有人用。

- ⚡ **秒起** —— 全局热键 `Alt + A`,在哪都能唤起,不用找、不用点菜单。
- 🪶 **极轻** —— 单文件约 50 KB,没有安装包、不下运行时、无需配置,用的是 Windows 自带的 .NET Framework。
- 🎯 **真好用** —— 标注、打码、复制或保存,松手之前一气呵成。

## 功能

- **一个热键** —— `Alt + A` 冻结并压暗屏幕,选区保持清晰,实时显示像素尺寸。
- **调整选框** —— 框内拖动整体移动,拖 8 个控制点缩放,框外拖动重新框选。截歪了不用重来。
- **标注** —— 箭头、直线、方框、文字、马赛克,无边框极简工具条。
- **颜色和粗细** —— 7 种预设颜色 + 3 档线宽,一点即换。
- **马赛克打码** —— 框住手机号、人脸、密钥一拖即打码,分享前遮好。
- **文字(支持输入法)** —— 点一下出红色光标直接打字,中文照常,背景透明。
- **复制或保存** —— ✓(或 `Enter`)复制到剪贴板,保存按钮导出 PNG。
- **多种取消** —— ✗、`Esc`、右键,或再按一次 `Alt + A`。
- **多显示器** —— 覆盖所有屏幕,工具条始终留在可见屏幕内。
- **托盘常驻** —— 双击托盘图标截图,右键退出。

## 快速开始

1. 从 [最新发布](../../releases/latest) 下载 `Snip.exe`。
2. 双击运行,它会待在系统托盘里。
3. 按 `Alt + A` 拖框即可。

想开机自启:把 `Snip.exe` 的快捷方式放进启动文件夹 —— `Win + R` 输入 `shell:startup`,拖进去即可。

> 提醒:微信的截图默认快捷键也是 `Alt + A`。AltSnip 用底层键盘钩子拦截,谁先抢注都无效,不用改任何设置。

## 快捷键

| 按键 / 操作 | 作用 |
| --- | --- |
| `Alt + A` | 开始截图(已打开则取消) |
| 拖动 | 框选区域 |
| 框内拖动 / 控制点 | 移动 / 缩放选框 |
| `Enter` 或 ✓ | 复制到剪贴板 |
| 保存按钮 | 导出 PNG |
| `Ctrl + Z` / 撤销 | 删除上一笔标注 |
| `Esc` · 右键 · ✗ | 取消 |

## 平台

稳定版是 **Windows**(Windows 8 及以上)—— 就是上面那个单文件 `.exe`。

面向 **Windows / macOS / Linux** 的**跨平台重写**(基于 Avalonia)正在
[`cross-platform`](../../tree/cross-platform) 分支推进,早期二进制放在
[`cross-preview`](../../releases/tag/cross-preview) 预发布里。功能已完整(框选、标注、马赛克、复制、保存),
Windows 版已冒烟测试;macOS/Linux 还需真机测试。详见 [cross/README.md](cross/README.md)。

## 从源码编译

不需要 Visual Studio,Windows 自带 C# 编译器:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

图标由 `tools/IconGen.cs` 生成,演示动画由 `tools/DemoGen.cs` 生成。

## 工作原理

按下热键时,整个虚拟屏幕(所有显示器)被复制成一张位图。遮罩把这张冻结图压暗显示,选区内以原亮度画回;标注画在上层,确认时烧进最终图。因为背景是冻结的,你的任何操作都不会打扰底层程序。`Alt + A` 用 `WH_KEYBOARD_LL` 钩子捕获 —— 只判断这一个组合键,不记录任何东西。

## 许可协议

[MIT](LICENSE) —— 随便用。
