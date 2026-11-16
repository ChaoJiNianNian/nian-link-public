# Windows Deskflow 核心：源码准备与手动构建草案

本文供审阅和后续独立验证。固定上游源码与 `0001` 补丁已使用既有 Windows 工具链在隔离目录成功编译，但**尚未在干净环境完整执行，也未证明产物与 0.1.9 现有二进制相同**。新编译核心的 SHA-256 为 `d68d557aff2b2e0160dfdbc491e7cf6e001bc2139e5111d15b1dcbc99aba14e7`，与现有核心不同；代码节及只读数据节也不同，不能只归因于构建时间。此次未进行运行或设备功能验证。它不构建年年互联应用或安装包，不替换正在使用的软件。修改版核心仅有 Windows 构建记录；Mac 包中的官方 Deskflow 是另一项，不能据此推定这份补丁在 macOS 可编译或对应 Mac 二进制。

进一步只读比较发现，两份核心的文件大小、PE 资源段与代码节长度一致；重建版内两处编译源码路径各长 64 字节，只读数据节虚拟长度合计多 128 字节，链接时间戳也不同。这些事实支持路径与链接布局造成部分差异，但不证明全部差异的原因或功能等价。历史构建目录在原始编译后还被修改过，不能作为当时完整源码快照。

## 原材料与范围

- 上游：Deskflow 1.27.0，提交 `081f6478e654a112168879e67a41ce7d7e4ae3af`。对应源码归档来源：<https://codeload.github.com/deskflow/deskflow/zip/081f6478e654a112168879e67a41ce7d7e4ae3af>。归档 SHA-256：`8493dfa58dbeac161a8b25d5b3cb7cb8546b5f4d99a216fcd65296b220a864f0`。
- 使用本项目 `vendor/deskflow/metadata/0001-current-user-native-control.patch`，SHA-256：`4553424fcf97b368c4356d647d5ed25b7e529daba1b00fe4cebd6a8f047e5324`。其适用性已对指定上游源码执行过 `git apply --check`；完整编译和运行尚未独立验证。
- 不应用历史 `0002-preview-input-isolation.patch`：没有证据证明它属于此 0.1.9 核心。旧脚本默认的 `PreviewIsolation` 模式也不能拿来作为此处的构建入口。
- 源码包另记录 QtBase 6.8.3（SHA-256 `56001b905601bb9023d399f3ba780d7fa940f3e4861e496a7c490331f49e0b80`）和 OpenSSL 3.5.9（SHA-256 `603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a`）。编译还需要与 MSVC x64 匹配、包含开发文件的 Qt 和 OpenSSL；仅有源码归档或运行时 DLL 不够。

这些原始源码和许可证可以从上游获取，或从单独提供的经校验源码材料包中取出；请自行核对哈希。`scripts/prepare_deskflow_source.py` 只从既有本机材料生成候选包，不会下载，也不是对外下载器。发布前仍需确认材料包可用、完整且与发布的二进制对应。

## 本机前提

准备独立的 Windows x64 构建环境：Git、CMake 3.24 或更新版本、Ninja、MSVC C/C++ 及 Windows SDK、Qt 6.8.3 MSVC x64 开发套件（含 `windeployqt.exe`）和 OpenSSL 3.5.9 开发文件。上游工程要求 C++20、Qt Core/Widgets/Network 和 OpenSSL；其他依赖以实际 CMake 配置结果为准。请从可信来源安装并独立审查依赖和许可。

历史记录是 MSVC `19.44.35229.0`、SDK `10.0.26100.0`、Qt 6.8.3 和 OpenSSL 3.5.9（shared、no-asm、no-tests）；工具链锁文件记录的 SDK 包版本为 `10.0.26100.9169`，两者命名及准确对应关系仍需确认。历史版本仅供调查，不保证装上这些版本就能生成相同文件。Qt 的准确构建参数、全部运行依赖及分发条件也有待核实。

## 1. 校验并准备干净源码

下例在 **x64 Native Tools Command Prompt for VS 2022 启动的 PowerShell** 中执行。先将占位路径替换成自己机器上的绝对路径；`$work` 必须是不存在的新目录，不能位于已安装软件、原源码、现有工程或用户数据目录。归档及补丁用可信来源的上述精确文件。整个过程不会自动下载或执行旧脚本。

```powershell
$archive = 'D:\inputs\deskflow-v1.27.0-source.zip'   # 自行修改
$patch = 'D:\inputs\0001-current-user-native-control.patch' # 自行修改
$work = 'D:\build-work\deskflow-019-new'           # 自行修改为不存在的目录

if ((Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash -ne '8493dfa58dbeac161a8b25d5b3cb7cb8546b5f4d99a216fcd65296b220a864f0') { throw 'Deskflow archive hash mismatch' }
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $patch).Hash -ne '4553424fcf97b368c4356d647d5ed25b7e529daba1b00fe4cebd6a8f047e5324') { throw 'Patch hash mismatch' }
if (Test-Path -LiteralPath $work) { throw 'Choose a new work directory' }
New-Item -ItemType Directory -Path $work -ErrorAction Stop | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $work -ErrorAction Stop
$source = Join-Path $work 'deskflow-081f6478e654a112168879e67a41ce7d7e4ae3af'
if (!(Test-Path -LiteralPath (Join-Path $source 'CMakeLists.txt'))) { throw 'Unexpected archive layout' }
git -C $source -c core.autocrlf=false apply --check -- $patch
if ($LASTEXITCODE -ne 0) { throw 'Patch check failed' }
git -C $source -c core.autocrlf=false apply -- $patch
if ($LASTEXITCODE -ne 0) { throw 'Patch application failed' }
```

解压只针对哈希匹配的上述归档；不要对未知归档照搬此步骤。保留原始归档不变，所有更改只发生在新解压副本。`git apply` 不要求源码目录是 Git 仓库；若遇到换行或补丁错误，应停下检查，不要跳过校验。

## 2. 配置并编译（待独立验证）

下列参数取自历史 Windows 构建记录，改用手动提供的工具链路径和新输出目录。把路径改为上述同架构工具链的实际位置，Qt 路径的 `bin` 内应有 `windeployqt.exe`，OpenSSL 路径应有头文件和库。上游 CMake 在配置 Windows 时可能在**源码副本**生成文件，因此不能把只读原档当构建目录。每个原生命令失败时停下，检查 CMake 输出。

```powershell
$qtRoot = 'D:\toolchains\Qt\6.8.3\msvc2022_64' # 自行修改
$opensslRoot = 'D:\toolchains\openssl-3.5.9-x64'   # 自行修改
$build = Join-Path $work 'build'
if (!(Test-Path -LiteralPath (Join-Path $qtRoot 'bin\windeployqt.exe'))) { throw 'Qt deployment tool missing' }
if (!(Test-Path -LiteralPath (Join-Path $opensslRoot 'include\openssl\ssl.h'))) { throw 'OpenSSL development headers missing' }
$env:PATH = (Join-Path $qtRoot 'bin') + ';' + $env:PATH
cmake -S $source -B $build -G Ninja -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl -DCMAKE_BUILD_TYPE=Release -DCMAKE_RC_FLAGS=/c65001 -DBUILD_TESTS=OFF -DBUILD_INSTALLER=OFF "-DCMAKE_PREFIX_PATH=$qtRoot" "-DOPENSSL_ROOT_DIR=$opensslRoot"
if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed' }
cmake --build $build --target deskflow-core --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $build 'bin\deskflow-core.exe')
```

这里没有部署 Qt/OpenSSL DLL、制作运行包或连接任何设备。要实际运行，还必须独立核实依赖、许可证、运行配置和安全机制；不要覆盖正式程序。输出哈希若与记录值 `6d06f16034edad54e6d51aad3df45e1e90e0c1d88b34489a69ff1cd5bd39b751` 不同，不代表功能一定错误；也不能仅凭相同版本、补丁和编译成功就声称已复现原二进制。

## 公开前尚需完成

在无本机缓存的隔离环境执行整套步骤，记录完整编译器、SDK、依赖来源、Qt/OpenSSL 构建配置和结果；针对产物做功能、安全及来源核对。分别核查修改版 Windows 核心、Qt/OpenSSL 和其他依赖、Mac 官方 Deskflow 的许可证与对应源码要求。上游版权、许可证与 OpenSSL 例外应随相应源码完整保留。本说明和材料清单均不是法律合规证明。
