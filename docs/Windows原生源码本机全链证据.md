# Windows 原生源码本机全链构建记录

这是一份本机隔离编译记录，配套的 [`windows-native-local-evidence.json`](windows-native-local-evidence.json) 保存输入、构建脚本、十份顶层日志和输出核心的 SHA-256。记录供复核，不能作为发布包、系统干净镜像或 GitHub Actions 构建成功的证明。

## 实际执行

在新的隔离目录中，按固定 SHA-256 校验的 Deskflow、QtBase 6.8.3、QtTools 6.8.3 和 OpenSSL 3.5.9 源码依次构建，Deskflow 依次应用四份固定补丁。OpenSSL 配置为 `shared no-asm no-tests`。QtBase 构建日志显示 1904/1904，QtTools 显示 657/657；核心构建记录的 `complete` 为真，核心 SHA-256 和文件大小见 JSON。

下载模块在本机测试副本中替换为返回事先下载并校验的源码归档的 stub，因此本次没有测试联网下载。编译结果不复用之前的构建产物。为避免本机已复现的 MSVC 长路径错误，该次局部构建脚本使用了较短的 `.artifacts/nc/b-<uuid>` 输出布局；仓库当前入口使用 `.artifacts/native-src/run-<uuid>` 并另有路径长度预检。两份脚本字节并不相同，JSON 分别记录本次脚本、stub 和核心脚本哈希。

该次命令执行工具报告退出码 0，且生成了完整 JSON；没有另存独立退出码文件。机器、系统工具链和本地归档并非全新虚拟机。执行记录中的架构为 x64；`VSCMD_VER`、`WindowsSDKVersion` 和 `VisualStudioVersion` 为空。CMake 记录的 SDK include 及 rc/mt 工具路径带有 10.0.26100.0，lib 路径没有版本号，无法仅凭路径确认 lib 的版本；这些信息不能代替缺失的环境变量。机器上 `cl` 和 `nmake` 把版本横幅放在 stderr，原始记录将其排在 stdout 帮助文字之后；本记录从完整保留的原始文本中提取了版本，不代表当时的公开摘要脚本能够正确读取它们。

## 日志和配置边界

十份顶层日志的字节数、哈希和计数见 JSON：OpenSSL 编译日志中按 `warning` 加 MSVC 警告编号格式匹配到 1751 次，QtBase 和 QtTools 配置日志各出现两次 `CMake Warning`。十份日志没有 NUL 字节，未匹配到 `fatal error` 字符串；这些统计不是对所有类型警告或嵌套核心构建日志的完整审计。Qt 配置摘要原文哈希分别记录在 JSON，配置和许可仍需按实际发行物核查。

OpenSSL 的配置目录及 provider 默认路径指向隔离构建前缀；从此目录复制 DLL 并不构成可迁移发行验证。未启动新核心，未做设备、TLS、权限、功能或安装测试，未制作、签名或发布完整安装包。历史二进制与新编译物是否等价及第三方发行义务均不能仅凭本记录确定。GitHub 全新运行器完整源码构建尚无成功证据。
