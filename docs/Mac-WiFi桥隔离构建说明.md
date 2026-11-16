# Mac Wi‑Fi 证据桥：隔离构建

此流程构建内部测试用的 `libNianWifiEvidence.dylib`，不生成正式安装包。请使用 Apple 芯片 Mac、macOS 14 或以上、Python 3 和可用的 Xcode 命令行工具；在独立源码副本中操作，不使用现用正式版或用户配对资料。

源码副本需包含 `src/NianLink.MacDiscoveryNative/` 下 4 个 C/Objective-C 文件、`tests/MacWifiBridge/` 下 3 个测试源，以及 `scripts/mac_wifi_bridge_build.py`。在该副本根目录执行：

```sh
python3 scripts/mac_wifi_bridge_build.py
```

脚本在副本的 `.artifacts/mac-discovery-native/test-<随机标识>` 新建目录，将 7 个源码输入冻结到其中，编译并运行纯 C 规则测试与 Objective-C 字符串规则测试；随后编译 arm64 动态库，做 ad-hoc 临时签名和签名校验，检查唯一导出符号并记录签名后的 SHA-256。它**不会执行**需要 `--run` 的系统证据探针，也不打开网络 socket。所有编译及链接命令均显式指定 `arm64-apple-macosx14.0` 部署目标；这不等于已在 macOS 14 真机运行。失败时保留新目录供排查，不生成成功证据文件。

成功后，该目录内有 `libNianWifiEvidence.dylib` 和 `wifi-build-evidence.json`。记录包含源码和签后产物哈希、工具链版本及未执行网络观察的标记。哈希只能核对本次文件，不证明与历史产物等价、符合发行许可、适用所有 Mac 或具备可信发布签名；ad-hoc 签名不等同 Developer ID 签名或公证。

## 与 Windows 隔离打包衔接

通过可信途径将整个新建目录复制到 Windows 上**独立源码副本**的 `.artifacts/mac-discovery-native/`，保留目录名，不覆盖已有文件；比较复制前后桥文件的 SHA-256。准备另行构建并校验的媒体和 WebKit 原生目录后，运行 `scripts/build-mac-test.ps1` 的 `-TestingOnly -DiscoveryTesting` 模式：`-DiscoveryBridge` 指向复制的 `libNianWifiEvidence.dylib`，`-DiscoveryBridgeSha256` 传入该文件**签名后的 64 位小写 SHA-256**，其余 SDK、NuGet 缓存、媒体目录和版本参数参照脚本。脚本会核对桥文件的位置、哈希和 arm64 Mach-O，并将相同摘要固定到候选程序集。

2026-11-14 用户在 Apple 芯片 Mac 的独立源码副本运行此入口：Apple clang 17.0.0 编译并通过两组合成规则测试，流程完成 ad-hoc 临时签名、严格验签和唯一导出符号检查。回传后，7 个源码与冻结副本、维护源码的 SHA-256 均一致，桥文件 SHA-256 为 `7658587168b8d2f3dc2a430b706bd6aed8729c77419f34fc777063fb7cb47c65`，与日志及证据一致，文件头为 arm64 Mach-O 动态库。代码没有主动运行真实 Wi-Fi 观察；本次不是系统级网络审计。Windows 只核对哈希和文件头，不能独立验证 Mac 签名。

使用该桥和此前在 Mac 实机编译的媒体、WebKit 组件，在独立 Windows 源码副本、已有 NuGet 缓存中成功组装内部免文件发现测试包；包内桥文件哈希一致，独立 bundle identifier 为 `com.niannian.nianlink.fileless-test`，打包脚本核验程序集桥哈希绑定。该 App 未签名、未公证，包不含 Deskflow，未安装或运行。系统网络权限、真实发现、macOS 14 实机、完整 App 安装、可信签名公证与双机运行仍须分别验收。
