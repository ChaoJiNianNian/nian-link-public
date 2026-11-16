# Windows 四补丁 Deskflow 核心隔离构建入口

该入口仅构建**Windows x64 多设备测试核心**，不会安装、运行或替换现用软件，也不生成完整安装包。新核心不能因编译成功就视为历史核心等价、可发行或已完成设备测试。Mac Deskflow 不在此入口范围内。

## 前置材料

1. 在独立的 Windows x64 源码副本内运行；使用 Python 3、Git、CMake、Ninja、MSVC C/C++ 和 Windows SDK。请从 x64 Native Tools 环境启动，确保 `VSCMD_ARG_TGT_ARCH=x64` 且上述命令在 PATH。
2. 运行仓库的 `scripts/fetch_upstream_sources.py` 获取固定上游源码；本入口使用其 `verified` 目录中的 `deskflow-v1.27.0-source.zip`，并再次固定核对 SHA-256。调用者自行准备 Qt 6.8.3 的 QtBase 和 QtTools MSVC x64 开发前缀、OpenSSL 3.5.9 x64 开发前缀。仅运行时 DLL 不足以构建。脚本检查必需文件但**不证明这些外部前缀的版本、来源、配置或许可**，调用者须独立核验；它不构建 Qt 或 OpenSSL。
3. 源码仓库的 `vendor/deskflow/metadata/0001...0004...patch` 必须齐全；脚本逐项核对固定 SHA-256，复制到新工作区并按顺序逐条 `git apply --check`、`git apply`。补丁不针对 macOS 官方 Deskflow。

## 使用

在独立源码副本根目录、已配置 x64 MSVC 的环境运行，将尖括号替换为自己已有的绝对路径：

```text
python scripts/build_deskflow_core.py --archive <已验证Deskflow源码ZIP> --qt <Qt开发前缀> --openssl <OpenSSL开发前缀>
python -B -m unittest discover -s tests -p test_native_core_build.py
```

每次生成新的 `.artifacts/native-core-build/build-<随机标识>/`，禁止复用和覆盖原目录，不做递归删除。失败时保留隔离日志与部分产物供检查；`build-evidence.json` 最后写入，仅在脚本成功退出且文件是完整 JSON、`complete` 为 true 时可作为完成记录；它记录固定源码和补丁摘要、核心哈希及限制。配置后会检查 CMake 所记 Qt6 目录，以及 OpenSSL 头文件、applink.c 和库路径均位于传入前缀；该路径检查不证明前缀的来源、完整性或版本。编译依次在新目录完成，`TEMP`、`TMP` 也指向新目录。工具会读取传入的外部 Qt/OpenSSL 和系统编译环境；如与其他同用户进程并发篡改路径，脚本无法保证不受影响，请使用用户可控的可信目录。日志可能包含本机路径，不要未经审查公开。

脚本针对固定 SHA-256 的官方源码 ZIP 限制成员根、路径、大小、重复项及不支持的文件类型。官方 ZIP 内有 18 个已逐一审核的图标符号链接；Windows 核心构建将它们的目标字符串作为普通文件存储，不创建文件系统链接，与既有隔离核心构建的处理一致。只接受这 18 个精确名称与目标，其他链接拒绝；该处理**不能用于证明图形界面图标资源完整**。

## 已验证范围

此前在独立工作目录将本入口用于实际四补丁核心编译，使用从源码构建的 QtBase 6.8.3、QtTools 6.8.3、OpenSSL 3.5.9 前缀和本机 MSVC/Windows SDK，成功生成核心；CMakeCache 中 17 项 Qt 与 OpenSSL 相关路径（含 applink.c 和可选 debug 库）位于传入前缀，其中 OpenSSL 配置记录为 3.5.9。核心未启动、未安装、未验证实体设备。实际构建证据保留在本地隔离工作区，不能代替全新电脑的完整构建和发布载荷许可审查。脚本变更后的测试及实际编译应另行记录，不应用历史成果替代。

下一步仍需对 Qt/OpenSSL 从获取到编译的过程建立独立、可重复的环境，按实际完整载荷核对 Qt、OpenSSL、VC++ 和其他第三方许可及分发条件，并在干净设备测试安装、签名和设备功能。
