# Mac 原生组件源码与隔离构建

本文供开发和私人审查使用，不是可直接分发的安装教程。请在 Apple 芯片 Mac 的**独立源码副本**操作，不使用现用正式应用目录或真实配对资料。需要 macOS 14 或以上、Python 3、Xcode 或已安装的 Xcode Command Line Tools。工具版本与 SDK 会影响结果，应保留实际构建记录。

## 源码与职责

- `src/DesktopToolbox.Mac/Native/DeskLinkMedia.swift`、`RealtimeCapture.swift`、`RealtimeCaptureOutput.swift`：媒体助手；`build-media.sh` 编译并执行自检，自检使用专门的合成剪贴板。
- `src/DesktopToolbox.Mac/Native/DeskLinkWeb.swift`：WebKit 桥；`build-web.sh` 编译为动态库。
- `src/NianLink.MacDiscoveryNative/`：Wi-Fi 证据桥的 C 与 Objective-C 源码。其独立编译、合成规则测试和导出符号检查见 [Wi-Fi 原生桥计划](Mac-WiFi原生证据桥接计划.md)。系统的 CoreWLAN、SystemConfiguration、Foundation、AppKit、WebKit 和媒体框架不是项目自带源码；使用系统 SDK 构建，发行时须另行核对 Apple 条款。
- Mac 键鼠连接还需单独准备与实际测试包对应的 Deskflow；此处的原生媒体构建不包含或替代 Deskflow。Avalonia、SkiaSharp、HarfBuzzSharp 和 .NET 等托管/本机运行依赖须按实际包及许可证另行核对。

## 媒体和 WebKit 助手

在独立的源码根目录检查当前系统、Python 与开发工具，再执行：

```sh
uname -m
sw_vers
python3 --version
xcrun swiftc --version
python3 scripts/mac_native_build.py
```

入口只接受 Apple Silicon Mac，会在源码副本下新建 `.artifacts/mac-native/native-<随机标识>`，不会覆盖已有目录。它先复制并冻结七个构建输入，在副本内运行两个既有构建脚本和媒体自检；检查输出是否为预期类型的 arm64 Mach-O，确认源文件构建前后没有变化，并生成 `build-evidence.json`，记录源码及两个产物的 SHA-256。它不签名、不公证、不启动现有应用。失败时保留新目录供排查，不会生成成功证据；不要把失败当成构建成功。

如需在 Windows 上使用 `scripts/build-mac-test.ps1`，将整个新建原生目录通过可信方式复制到 Windows 项目副本的 `.artifacts/mac-native/`，保留原目录名，且不要覆盖已有目录。该脚本核对目录名、7 个源码和 2 个产物的哈希，并要求 .NET SDK 10.0.404 与指定 NuGet 缓存；发现候选还需要单独构建的 Wi-Fi 桥、其固定 SHA-256 及相应参数。具体开关、路径校验及模式互斥条件以脚本为准。**不要手写或复用不匹配的证据来绕过校验**。

哈希证明本次输入和文件与记录一致，不单独证明与历史测试二进制等价、供应链可信、许可完整或构建可复现；真实的原生编译、签名、App 权限、安装与双机实测须分别验收。

## 本次验证范围

2026-11-14 用户在 Apple 芯片 Mac 的独立源码副本运行本入口：媒体助手编译、自检和 WebKit 桥编译均成功，Apple Swift 版本为 6.1.2。已将独立结果取回，并逐项核对证据中的 7 个源码 SHA-256 与当时维护工程及冻结源码一致，2 个产物 SHA-256 和 arm64 Mach-O 头与预期一致；这不证明完整二进制签名或行为正确。构建产物保存在本机独立 `.artifacts/mac-native-validation/` 目录，未替换现用软件。Windows 合成测试覆盖不覆盖、输入变化、架构拒绝和打包接收校验。此结果不包含独立 Wi-Fi 桥重建、完整安装包、签名、公证、屏幕权限或真实双机验收。

Apple 参考：[安装 Xcode 命令行工具](https://developer.apple.com/documentation/xcode/installing-the-command-line-tools)、[开发者条款与 Xcode/SDK 协议](https://developer.apple.com/support/terms/)。

同日又在独立源码副本使用此真实 Mac 产物、.NET SDK 10.0.402 和已有 NuGet 缓存，成功交叉打包 Mac 多设备测试候选。ZIP 完整性检查通过，包含媒体助手、WebKit 桥及 SkiaSharp、HarfBuzzSharp 动态库；构建状态明确为内部测试、未签名、未公证且不含 Deskflow。此包未安装或运行，不是干净机重建、最终发行或双机验收。

## 2026-11-15 da067e6 修订的隔离内测候选

以私有源码提交 `da067e6dca377383c5c0d05724ee81d4194d5988` 创建新隔离源码归档，导出时显式禁用 Git 自动换行转换，以满足原生输入的字节哈希检查；归档内 1314 项与构建后的源码副本逐项一致。与另一默认设置下导出的 Git archive 比较时，832 项有 CRLF/LF 原始字节差异，统一换行后 1314 项一致，不应将两个归档称为逐字节相同。媒体、WebKit 及 Wi-Fi 桥继续使用此前 Mac 真机编译的证据和产物，两个证据各自列出的 7 项输入分别与此次源码匹配；不代表全部原生源码或历史软件已经重建。

旧缓存中有四个 nupkg 不是有效 ZIP，已停止使用；另从既有隔离缓存寻找有效替代，核验包身份、CRC 和本机 `dotnet nuget verify`。首次使用新的离线 feed 构建因缺两个运行包报 NU1101，未产出成功包；补齐后第二次成功。最终离线 feed 共 29 个包完成 CRC、包身份及本机签名检查，其中替代和补充的六包另与 NuGet 官方 v2 元数据 SHA-512 匹配；本机验签依赖本机信任配置。本次使用已有缓存构建，不是全新设备。

生成的内部 App ZIP SHA-256 为 `265f84a740388af29e5a0dd892bf4ae505979daee49ae91b02cc0275a03b443d`，47,256,156 字节；独立读取 266 个 ZIP 文件，CRC 通过且路径无重复。`runtimeconfig.json` 标示 .NET 10.0.15，Info.plist 标示内测身份和最低 macOS 14；媒体、WebKit 和 Wi-Fi 三项原生文件的哈希与相应证据一致。包状态为内测、未签名、未公证、不含 Deskflow、未在 Mac 真机验证。

另外用既有内测引导脚本生成六成员传输 ZIP，SHA-256 `aeb34d608393da60764ad26c2d658bee0e546ddcdd3c083640ed318ba0e7de5a`，内层 App ZIP 和引导脚本已按哈希/内容核对。引导入口要求 Apple 芯片、macOS 14 及以上、Python 3 与另行安装的 Deskflow，使用独立测试目录并对测试 App 临时签名；此处的构建和文件检查不证明 Mac 已安装或运行、权限已授予、设备已连接或可以正式发行。原始构建日志含本地路径，保留在隔离目录，不随仓库上传。

## 无人值守的包内依赖与通知对照

对上述 Mac 内测 ZIP 的 `Contents/MacOS` 234 个文件，以不区分大小写的基本文件名在本次 29 个隔离 NuGet 包中找候选，再比较解压内容 SHA-256：215 个不同路径与至少一个包内文件匹配，19 个未匹配，包含项目程序集、页面资源、独立原生组件、App 主机及运行配置。统计覆盖该目录，不涵盖整个 App；未匹配不等于错误，也不证明所有匹配文件的分发许可已满足。限定 dll、dylib、json、dat、exe 后，.NET Core 10.0.15 包对应的 185 项经独立复核；不限定后缀时还包括 `createdump`，共 186 项。

App 内 `package-notices/` 的 21 个许可和通知文件，已分别与离线 feed 中对应 12 个 NuGet 包的同名原文逐字节对照，并经独立复核一致。对照说明文件内容与指定本机包相同，不等于 Apple 签名公证、完整分发条件或全部动态依赖已经验收。原始比较清单保留在本机隔离目录；本次未安装或运行 Mac 应用。

对包内程序集另做静态元数据读取：首次尝试 `Assembly.LoadFile` 因 arm64 与本机 x64 不兼容报错，没有执行产品代码；随后使用 `System.Reflection.Metadata.PEReader` 检查，`NianDeskLink.dll` 的 `NianFilelessPairingCandidate=true`、`DesktopToolbox.DeskLink.dll` 的 `NianFilelessPairingIsolatedTest=true`，且发现程序集记录的 Wi-Fi 桥摘要与包内 `libNianWifiEvidence.dylib` 一致。此为不加载产品程序集的静态检查，不代表 Mac 应用已运行、权限、TLS 或签名公证已验证。

## 2026-11-15：Mac 测试身份与画面过滤源码增量

静态核对发现，原 `RealtimeReturnWindowPolicy.ownBundles` 不包含当前打包生成的 `com.niannian.nianlink.multidevice-test`、`com.niannian.nianlink.discovery-test` 和 `com.niannian.nianlink.fileless-test`。如果 ScreenCaptureKit 如实报告窗口拥有者的 bundle ID，原代码无法将这些身份的归还浮窗选为采集例外；其他并行测试版也不会仅因其 bundle ID 被加入自身应用排除集。实际 Mac 窗口枚举和画面行为尚未复现。Apple 的 [SCContentFilter 说明](https://developer.apple.com/documentation/screencapturekit/sccontentfilter)与 [SCWindow owningApplication](https://developer.apple.com/documentation/screencapturekit/scwindow/owningapplication)描述了所用过滤和窗口拥有者接口，但不证明本应用的运行结果。

现将三个实际打包身份以精确值加入集合；未改原有父 PID、已排除、非零窗口 ID、精确标题与 bundle、唯一匹配及允许例外等检查。这个集合同时用于排除自身不同版本，因此改动也扩大了对这三个已知测试版的排除范围；不是基于签名的身份认证。Swift 源码的 `selfTest` 为每个新增 ID 加入有效、错 PID、父进程未排除、重复窗口、禁用例外、相似 ID 六种情况，连同原七项将成功输出计数改为 25。

Python 测试调用真实打包身份函数并静态对照 Swift 显式集合：独立复核以旧源码复现失败、当前源码通过；私有审查副本整份身份测试 11 项通过、1 项因未恢复 Mac 项目依赖而跳过，Mac 原生构建脚本测试 4 项和输入验证 6 项通过。静态测试没有执行 Swift、ScreenCaptureKit 或 25 项 Swift 自检；本机没有 Swift 编译器。改动使先前 7 项原生输入证据中的 `RealtimeCapture.swift` 和 `DeskLinkMedia.swift` 摘要失配，必须在 Mac 重新编译、自检并生成新证据；此前传输的 Mac 测试包仍含旧助手，不能称为已修复。归还浮窗关闭竞态、键鼠专用应急协议、原生队列安全及真实双机验收仍未解决。
