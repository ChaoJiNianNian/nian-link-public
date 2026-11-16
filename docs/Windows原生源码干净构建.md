# 手动 GitHub Windows 原生源码构建（审查草案）

这是一份待审查的手动 CI 草案，**尚未在 GitHub 全新运行器成功完成**。它只编译 OpenSSL、QtBase、QtTools 和 Deskflow Windows x64 四补丁测试核心；不会运行核心、安装到系统、生成安装包、上传二进制或证明历史二进制等价。GitHub 私有工作流使用账户的 Actions 额度，实际时间取决于运行器和网络。

## 范围与工具

工作流只声明 `workflow_dispatch`，使用 Windows Server 2022 托管 runner（`windows-2022`）进行源码构建试验、只读 token、禁止 checkout 持久化凭证；不触发 push/PR、不调用缓存与 artifact/release API。VS2022 由 `vswhere` 寻找并在同一 cmd step 加载 x64 Native Tools，整个链条在子进程中继承环境。Python、Git、CMake、Ninja、Perl、nmake、MSVC 与 Windows SDK 由运行器提供；运行器镜像会升级，脚本记录工具版本与执行文件 SHA-256、MSVC/SDK 环境和操作系统信息，不将工具链当作可复现地固定版本。此前本机独立源码构建记录使用 MSVC 19.44 与 SDK 10.0.26100；云端版本可能不同。

下载器仅使用仓库固定的四项官方 URL 与 SHA-256，重新联网获取且无构建/下载缓存。Qt 官方重定向若不在已核验域名名单则失败，不可绕过；该入口本身不执行 OpenSSL PGP 验签；项目留存的一份 3.5.9 归档已另做[签名核验](OpenSSL源码签名核验.md)。下载归档写入新的 `.artifacts/upstream-sources/source-<随机标识>/`，解包与 OpenSSL/Qt 构建写入新的 `.artifacts/native-src/run-<随机标识>/`，Deskflow 子构建另写入新的 `.artifacts/native-core-build/build-<随机标识>/`；三者相互独立。失败时不删除目录，禁止复用已存在目录或通过缓存恢复。两个摘要工具为防止混淆，要求同一源码副本恰好有一个原生构建工作目录；本机失败后重试应使用新的独立源码副本，保留旧证据。GitHub 每次作业使用新的运行器。GitHub runner 一次工作结束后销毁；产生的 GitHub 日志仅含 runner 的构建材料，失败时显示最后 32 KiB 日志用于排查，需审查后再对外分享。

脚本解包固定的 OpenSSL / QtBase / QtTools 归档并验证哈希、成员总数、解包路径、类型及大小：拒绝任何归档链接、路径穿越、同名大小写冲突或特殊文件。三份归档分别完整含 5,779 / 21,968 / 4,756 个普通文件。Deskflow 源码和四个补丁由仓库的隔离核心构建入口单独固定哈希验证；其特殊的 18 个图标链接处理与核心入口文档一致，不能用于声称 GUI 资源完整。

按顺序以 MSVC 构建 OpenSSL 3.5.9（`no-asm no-tests shared`，NMake 单线程编译及 install_sw）、QtBase 6.8.3（Release、Ninja、2 并发、OpenSSL 运行时模式）、QtTools 6.8.3（Release、Ninja、2 并发）和 Deskflow 核心。QtBase/QtTools 的 CMakeCache 路径及核心构建入口各自核对选中的库、头文件位于本次目录；这不能覆盖所有运行时动态加载或全部配置差异。构建设定和实际日志应在本次运行后逐项检查，特别是 GitHub 镜像自带 LLVM 可能改变 QtTools 功能。NMake/Qt 脚本失败时保留结果，不尝试自动绕过错误。

成功执行后，工作区 `clean-build-evidence.json` 包含 `complete: true`、四项来源 SHA-256、系统工具版本及可执行文件哈希、MSVC/SDK 环境、CMake 路径检查数、核心哈希和未完成项；仅在**整个脚本返回 0 且 JSON 完整**时有效。工作流在成功后用专门的白名单摘要工具输出经格式及固定清单检查的记录值，包括来源哈希、工具版本与哈希、MSVC/SDK 版本、依赖检查数和核心哈希到私有 Actions 日志；摘要工具不重新读取核心、补丁或来源文件，也不能独立确认构建进程退出码；工具不输出原始工具文字或本机路径；工作结束 VM 会销毁，日志仍受仓库访问与保留策略约束。文件不上传成 Release。禁止从用户电脑复制证书、个人配对资料或现有正式版进入 runner。

## Windows 路径长度

入口在创建任何输出和下载前，按此前 QtBase 6.8.3 实测较长对象路径进行 UTF-16 长度预检；超过 250 个单元时立即失败，请将**独立源码副本**放在更短的路径后重试。测试中的 GitHub 典型工作区映射约为 243 单元；实际 runner 的路径和 Qt/CMake 生成规则可能变化，这只是对已知问题的保守防护，不保证所有依赖或其他版本的路径安全。之前一次本机长目录构建在 Qt 编译报 C1083，同一对象仅改变工作目录为短路径后成功，该反例不代表整条构建通过。旧目录和日志均保留。

## 本地验证

2026-11-15 在新短路径源码副本重新联网下载并核验四份固定源，当时的主构建脚本完成本机原生编译；不过本机是手工配置工具链，缺少 VsDevCmd 环境字段，后续两个严格摘要工具均拒绝，不能称完整工作流通过。后加的显式本机摘要模式已在另一个隔离副本读取该次记录，不改变严格 CI 校验。构建警告和详细范围见[当前入口本机核验](Windows原生构建入口本机核验.md)。

已在隔离目录对三份固定归档逐文件解包并 SHA-256 对照，共 32,503 个文件一致（5,779 + 21,968 + 4,756）；17 项合成测试覆盖链接、穿越、重复、篡改哈希、空间不足、依赖路径越界和工具版本采集等。已在本机以新源码与输出目录、事先验证的四份下载包和本机系统工具链完成一次全链构建；该次使用本地下载 stub 与更短的独立输出目录，未运行核心，且新版版本采集脚本尚未再完成全链。详情见[本机全链证据](Windows原生源码本机全链证据.md)，不能替代 GitHub 全新 VM 的首次成功运行。

后续仍需完整载荷 SBOM、Qt 分发与第三方许可、VC++ 运行库、动态插件、Mac 原生组件、签名公证、全新设备安装及实体设备验收。上游构建文档：[OpenSSL 3.5.9 Windows](https://github.com/openssl/openssl/blob/openssl-3.5.9/NOTES-WINDOWS.md)、[Qt 6.8 Windows](https://doc.qt.io/qt-6.8/windows-building.html)、[GitHub Windows 2022 镜像](https://github.com/actions/runner-images/blob/main/images/windows/Windows2022-Readme.md)。
