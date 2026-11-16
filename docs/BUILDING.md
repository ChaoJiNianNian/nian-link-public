# 构建和测试（开发状态）

此文档如实记录当前入口，不是已验证的公开版重建指南。部分打包流程仍需要预先准备的 Deskflow 文件；在全新机器上直接运行可能失败。不要把未知二进制或本机缓存直接纳入发布物。

## 技术栈与工具

- Windows：.NET SDK 10.0.404、WPF、WebView2 Runtime；内部测试安装器使用经过校验的 Inno Setup 和 PowerShell 7。
- Mac：.NET 10、Avalonia、Apple 芯片 macOS、Swift/Xcode 工具链。截图预览使用本机编译的 Swift 媒体组件；Mac 键鼠依赖 Deskflow 1.27.0 ARM64。
- 共享界面测试使用 Node.js；部分打包验证使用 Python 和 Pillow。精确依赖版本及第三方通知以实际构建清单为准。

## 当前构建入口

托管源码可使用不依赖旧工程路径的入口（需要 Python 3、.NET SDK 10；Windows 项目需要 Windows）：

```text
python scripts/build-managed.py --target windows --with-tests
python3 scripts/build-managed.py --target mac
```

Mac 真机上可另传 `--with-mac-tests`，该路径尚未做完整真机验收。可用 `--dotnet` 和 `--packages` 指定 SDK 与 NuGet 缓存。隔离候选源码已在 Windows 使用现有 SDK 和缓存通过 Windows 构建与共享测试、Mac 托管代码交叉构建；这不是干净机器构建验证，也不会生成 Mac Swift 媒体组件、Deskflow 核心或安装包。

- `scripts/stage-windows-dependencies.ps1` 准备并校验 Windows Deskflow 运行材料。
- `scripts/build-mac-test.ps1` 接收已在 Mac 本机编译、带哈希证据的原生组件，交叉发布内部测试版；`scripts/build-mac-export-test.ps1` 生成可导出的 Mac 测试组合包。
- `scripts/build-multidevice-windows-test.ps1` 制作 Windows 多设备隔离测试包；目前依赖专门编译和核验过的试验键鼠运行材料，不是独立源码重建流程。
- `scripts/build-multidevice-installer-test.ps1` 将经过清单校验的隔离候选制作成独立 Windows 内测安装器；候选可选包含经模式和哈希校验的 Mac 多设备测试包，不得作为正式发行物。安装器使用的简体中文文件及许可位于 `installer/languages`。
- `scripts/build-windows-installer.ps1` 构建内部测试安装器；可选嵌入经过校验的 Mac 测试包。必须显式指定工具、版本、缓存路径和测试模式；脚本本身禁止将此流程当正式发行。
- 原开发机曾使用本机专用脚本对已核对哈希的测试安装器进行隔离安装卸载检查；该工具依赖本机路径，不包含在源码候选中，也不等同于真实双机功能测试。

打包脚本使用本项目的 `scripts/BuildArtifactLock.psm1` 协调构建；试验键鼠运行材料和 Mac 原生组件仍需事先准备，尚不足以支持干净机器独立重建。源码归档、所有必需材料及公开构建流程仍需核验；请勿通过关闭校验或复制用户私有配置来绕过缺失依赖。

## 验证范围

Windows 构建回归还包含 MultiPeerTrust.Tests，使用当前用户 DPAPI 和隔离目录验证多设备信任持久化；该测试仅在 Windows 运行。

Windows `--with-tests`（包括多设备候选与只读 CI）还运行 `ConditionalCommitIsolation.Tests`：它只在测试程序集编译条件提交实验，使用合成凭证和临时目录，不接入产品授信。Mac 构建不会运行这组依赖 Windows DPAPI 的测试；本组通过不代表真实设备配对或崩溃恢复已经完成。

Windows 构建回归还包含 MultiDevice.Windows.Tests：使用短暂的本地 PowerShell 测试进程验证隔离显示器辅助进程的取消与退出，不访问显示器硬件。

`tests/native-multipeer` 和 `tests/native-peer-bindings` 是多设备试验核心的独立原生测试工程，需要 Qt 6.7 及 CMake，`DESKFLOW_SOURCE` 指向依次应用本项目 0001 至 0004 补丁的隔离 Deskflow 源码目录。它们分别验证条件切换协议和设备指纹绑定解析，不会证明真实网络连接或实体键鼠可用。试验补丁和历史安装二进制之间的对应关系尚未证明，不能当作正式发行的对应源码。

多设备隔离候选的托管代码可以显式使用 `python scripts/build-managed.py --target windows --multi-device --with-tests` 或 `--target mac --multi-device` 构建。它使用独立界面与资料目录，不能替代已安装版本；这两个命令不制作安装包，也不构建 Mac 原生媒体或 Deskflow 核心。笔记本、台式机和 Mac 已完成基本互联实测；显示器联动、四台远端和全新用户配对尚未验收。

独立项目包含 `tests/DeskLink.Tests`、`tests/StandaloneSettings.Tests`、`tests/PortOwnership.Tests`、`tests/PairingExport.Tests`、`tests/LanAddress.Tests`、`tests/Shutdown.Tests` 及 `tests/ui` 等测试。已有结果仅证明对应版本、平台和测试边界；Mac 测试在 Windows 文件权限上存在已记录失败，不能写成全部通过。开发时请先检查对应项目与测试脚本，使用合成数据，并确保构建互斥和隔离目录。

绝不要把配对 JSON、证书、私钥、token、用户剪贴板、收件文件、真实局域网地址、日志或个人目录打入源码包。测试应使用独立身份和合成数据。实际签名、公证、版权及完整对应源码的核验是单独的发布前工作。
