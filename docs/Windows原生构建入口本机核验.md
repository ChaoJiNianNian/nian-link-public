# Windows 原生源码构建入口：本机隔离核验

本记录核验私有源码提交 `53949a8ee0beac304f3fb2279c13b9b6dad42873` 的 `scripts/github_native_clean_build.py`。它是本机隔离构建证据，不是 GitHub 全新运行器、完整手动工作流、安装包或发行验收。

## 环境和输入

- 从该提交的 Git archive 创建新的短路径源码副本；归档 SHA-256 为 `d1dd036795f3a7885748b31f8fa99c5ab6dd7e35b109f4dead4f326a00e84c48`。构建后与归档逐项比对，1307 个输入文件相同、0 缺失、0 改变。
- Windows x64 本机使用既有离线工具缓存手工配置 MSVC、SDK 的 include/lib 和 PATH，`VSCMD_ARG_TGT_ARCH` 在本次进程中手工设为 `x64`，没有执行 Visual Studio 的 VsDevCmd。`VSCMD_VER`、`WindowsSDKVersion`、`VisualStudioVersion` 均未提供，不能将此环境写成标准开发者命令环境或 GitHub 运行器。
- 工具版本：MSVC cl 19.44.35229 x64、NMake 14.44.35229、Perl 5.34.3、CMake 4.4.1、Ninja 1.13.2、Git 2.53.0.windows.1。SDK include、rc 与 mt 路径使用本机现有的 10.0.26100.0 相关材料；lib 路径没有版本号，不能仅凭路径确认库版本。完整工具链不属于全新系统。
- 原生入口先后真实下载四份固定源，成功记录的 SHA-256：Deskflow `8493dfa58dbeac161a8b25d5b3cb7cb8546b5f4d99a216fcd65296b220a864f0`、QtBase `56001b905601bb9023d399f3ba780d7fa940f3e4861e496a7c490331f49e0b80`、QtTools `02a4e219248b94f1333df843d25763f35251c1074cdc4fb5bda67d340f8c8b3a`、OpenSSL `603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a`。当前下载的 OpenSSL 归档另经[官方签名核验](OpenSSL源码签名核验.md)，但构建入口本身不自动验签。
- 首次完整入口尝试在取得 Deskflow 和 QtBase 后下载失败，未生成完整来源包或开始编译；错误信息未保留底层原因。随后 QtTools 单项隔离下载通过，并在另一份全新源码副本重新运行完整入口，不能把单项重试当成首次完整下载成功。

## 实际构建结果

第二次运行 `python -B scripts/github_native_clean_build.py`，执行工具报告退出码 0，产出的 `clean-build-evidence.json` 中 `complete` 为 true；没有另存独立退出码文件。OpenSSL 3.5.9、QtBase 6.8.3、QtTools 6.8.3 和四补丁 Deskflow 核心依次在隔离目录构建；QtBase 与 QtTools 日志分别显示 1904/1904、657/657。OpenSSL 使用 `shared no-asm no-tests`，未执行 OpenSSL 测试。

独立计算得到核心大小 1,118,208 字节、SHA-256 `6fbb6214eed103d6f5c2607d396a7c5a324b4561ccf4cd5a8a3a081b27faa217`，与本次构建证据一致；QtBase 的 OpenSSL 缓存条目和 QtTools 的 Qt 缓存条目检查分别为 4 项和 18 项。当次构建结束时未运行或安装核心，摘要不证明与历史核心等价。

日志按 `warning` 加 MSVC 编号模式统计，OpenSSL 编译有 **1751 条匹配**；QtBase 和 QtTools 配置日志各有 2 条 CMake 警告。计数仅覆盖指定模式，不代表所有日志无其他警告。原始 JSON、命令行和日志包含本机路径，保留在隔离目录，不随仓库上传；本次原始 JSON 的 SHA-256 为 `f00a40d8f6d76589bd68d5fd2c6adbb67abb1180934978e799df78e1be9d836b`。

原始 JSON 的固定限制文本称工具链来自 runner 镜像，这句针对 CI 预期环境，**不适用于本次**；本次实际使用本机手工配置的离线工具缓存。该字段不足以证明环境来源，以上面的实际记录为准。

## 后续本机版本探针（2026-11-15）

将本次生成的核心、同次构建的 Qt6Core/Gui/Network/Widgets、OpenSSL libcrypto/libssl 和 qoffscreen 复制到新的隔离目录，复制后逐项比较 SHA-256；在独立 USERPROFILE、APPDATA、LOCALAPPDATA 和 TEMP、最小 System32 PATH、`QT_QPA_PLATFORM=offscreen` 下运行 `deskflow-core.exe --version`，设置 15 秒超时。环境及超时来自操作记录，未写入 JSON。退出码为 0，输出版本 1.27.0、协议 1.8，标准错误为空。核心 SHA-256 为 `6fbb6214eed103d6f5c2607d396a7c5a324b4561ccf4cd5a8a3a081b27faa217`；原始记录包含本机路径，保留在隔离目录，不随仓库上传，记录 SHA-256 为 `4d0c65d3b5dee4f1b4b0e1a59449d89727ab5b9e200502c25f1aa71a3a609c68`。

读取该版本源码确认版本分支在 server/client、IPC 和共享内存之前退出；初始化仍会加载 Winsock 并设置本进程错误模式。运行后隔离 profile、appdata、localappdata、temp 目录为空，未见残留核心进程；这只是事后快照，并非系统级文件或网络追踪。**该探针只证明版本命令能在本机运行**：系统 DLL 与本机 VC++ 运行库仍参与；未证明每个复制的 DLL 均被加载，也未启动键鼠共享或测试 TLS、完整连接、真实设备和干净系统。

## 摘要工具和未完成项

构建后另外运行 `scripts/print_native_clean_evidence.py` 与 `scripts/qt_config_summary.py`，均返回非零：前者拒绝缺失的 `VSCMD_VER`，后者依赖同一严格的构建证据验证。该严格检查服务于标准 CI 环境；本机手工环境没有足够证据补造这些字段，因此保留失败结果，不绕过检查。**主构建脚本完成不等于包含这两个摘要步骤的工作流已通过。**

随后对摘要工具增加显式 `--local-manual` 模式，在另一个隔离副本中读取本次原始构建记录和两份 Qt 配置摘要，两个本机摘要均返回 0、使用独立的 local schema；未提供的三个 VS/SDK 版本输出为 `null`，并明确 x64 标志由调用方提供。它们只检查 JSON 中的记录及 Qt 配置摘要，不重新计算核心或补丁、不独立确认原构建进程退出码。严格默认模式继续拒绝缺失版本，`GITHUB_ACTIONS=true` 时显式本机模式也拒绝；这一环境变量检查用于防误用，不是针对恶意调用者的防篡改证明。这是后加工具对既有记录的复核，不是当次 GitHub 工作流成功。

摘要异常输出不回显路径或原始异常；版本提取对四段均在 0–255 范围的数字保守拒绝，以免把 IPv4 当作版本输出，这也可能拒绝形似地址的合法四段版本，且不能保证去除所有数字型敏感资料。原生报告原有固定的“runner 镜像”描述已改为“执行环境提供工具链”；本次旧记录本身仍保留原样。

除上述后续版本命令外，本次未启动核心服务、制作安装器或测试 Mac 原生部分；历史二进制对应、运行依赖、Qt 分发条件、Visual C++ / WebView2 实际交付、签名公证、干净设备安装和真实设备验收仍待完成。此前云端运行失败、当前修订尚未在 GitHub Actions 复跑。
