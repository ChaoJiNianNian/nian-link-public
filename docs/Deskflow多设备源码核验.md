# Deskflow 多设备核心源码核验记录

本记录仅涵盖 Windows 多设备测试核心，并非 0.1.9 单设备核心或 Mac 官方 Deskflow 的完整来源证明。未经完整发行与许可审查，不要据此公开发布程序。

## 来源及材料

多设备测试打包脚本固定的 Deskflow 核心 SHA-256 是 `04ce02fb644b2e3e80a23f50077aaca1168c5a1ea520e5d45e48aa68104cfffa`（忽略大小写）；历史隔离构建产物及测试运行材料两者一致。用户现用正式版没有被替换。

独立材料目录 `.artifacts/deskflow-multidevice-source-review/` 保存旧工程留存的源码归档及四个补丁，逐项比对既有固定来源记录中的 SHA-256 并复制后复验；它不自动加入 Git。**这一步仅证明本机归档与本机已有记录一致，不能单凭它证明文件最初来自官方或记录未被篡改。** 下表的链接是既有记录所列上游地址：

| 材料 | 上游来源 | SHA-256 |
| --- | --- | --- |
| Deskflow 1.27.0 固定提交 `081f6478e654a112168879e67a41ce7d7e4ae3af` | [Deskflow 上游归档](https://codeload.github.com/deskflow/deskflow/zip/081f6478e654a112168879e67a41ce7d7e4ae3af) | `8493dfa58dbeac161a8b25d5b3cb7cb8546b5f4d99a216fcd65296b220a864f0` |
| QtBase 6.8.3 | [Qt 上游源码](https://download.qt.io/archive/qt/6.8/6.8.3/submodules/qtbase-everywhere-src-6.8.3.tar.xz) | `56001b905601bb9023d399f3ba780d7fa940f3e4861e496a7c490331f49e0b80` |
| OpenSSL 3.5.9 | [OpenSSL 上游源码](https://github.com/openssl/openssl/releases/download/openssl-3.5.9/openssl-3.5.9.tar.gz) | `603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a` |

四个补丁保持原始字节，存放于 `vendor/deskflow/metadata/`，依次为：

1. `0001-current-user-native-control.patch`：`4553424fcf97b368c4356d647d5ed25b7e529daba1b00fe4cebd6a8f047e5324`
2. `0002-preview-input-isolation-experimental.patch`：`0c0f7937ddf61311415806da3f2fa37c310b3e089a98a7b5933e0daa16f4ffce`
3. `0003-multidevice-conditional-switch-experimental.patch`：`c25888d4f6d952dcb85872173b82414ac8e3d1abd20e2ebf6f59c671118e9d1c`
4. `0004-multidevice-identity-binding-experimental.patch`：`67bfc277bd5be9cd95eeeea664988aeccb9d01e8a9d3b34b4383a70339c0d58c`

## 官方来源校验

QtBase 6.8.3 的本地归档 SHA-256 与 [Qt 官方发布的校验值](https://download.qt.io/archive/qt/6.8/6.8.3/submodules/qtbase-everywhere-src-6.8.3.tar.xz.sha256) 完全一致；这项校验独立于本机旧记录，但不证明二进制由此源码及特定配置构建。OpenSSL 3.5.9 的本地归档 SHA-256 与 [Debian 源码包发布记录](https://tracker.debian.org/news/1820377/accepted-openssl-359-1deb13u1-source-into-proposed-updates/)中同版本原始归档的 SHA-256 和大小一致；这是独立的发行方记录；该次尚未核验官方签名，后续核验见[OpenSSL 源码签名核验](OpenSSL源码签名核验.md)。当时 Deskflow 的官方归档仍需独立核验；后续已独立获取并对照固定摘要，见[上游源码获取说明](上游源码获取说明.md)。

## 核验结果

在独立目录对上游归档校验 SHA-256 和 ZIP 完整性后，按上述顺序分别运行 `git apply --check` 和 `git apply`，四项全部成功。重建树的 825 个普通文件与历史构建源码树对应文件逐字节一致；归档中另有 18 个符号链接，历史 Windows 解压树把它们作为普通文件保存，其内容与归档中的链接目标字节一致。历史树另外多出生成的 `vcpkg.json`。随后按历史 Windows 方式将这 18 项链接内容写成普通文件，CMake 配置也生成 `vcpkg.json`；此时重建树与现存历史树的 844 项文件全部逐字节一致。这表明现存历史树与补丁序列相符，不能证明编译时该树未被更改。

上游归档本身保留原始链接及许可证；跨平台解压时需按系统正确还原链接，不应把 Windows 下的链接文本当成真正的符号链接。补丁构造检查不等于应用功能或可发行性验证。

独立原生构建：使用已存在的 MSVC 14.44.35207、Windows SDK 10.0.26100.0、Qt 和 OpenSSL 本机开发缓存，Ninja、Release、`BUILD_TESTS=OFF`、`BUILD_INSTALLER=OFF`，从新构造的四补丁源码完成配置及 `deskflow-core` 编译。新核心 SHA-256 为 `98ab7501e382af081d99976c3a02a18373ae820a23f8df4f5d9a06558d03a9f5`，与历史测试核心不同；Qt 离屏执行 `--version` 返回 Deskflow 1.27.0。该命令在设备连接和键鼠操作之前退出，不等于原核心的行为等价或设备测试；重建使用既有本机依赖缓存，亦不是空白机独立构建。

## 多设备核心：独立重建步骤

这套步骤只用于 **Windows 多设备测试核心**，按 `0001 → 0002 → 0003 → 0004` 顺序应用。旧的 [0.1.9 单设备构建草案](Deskflow手动构建.md)只使用 `0001`，两者的二进制、补丁组合与验收记录不能混用。本次验证复用了本机已安装的开发工具和依赖缓存，以下是可操作的重建入口，不是无缓存发行构建承诺。

在 Windows x64 上准备 Git、Python 3、CMake、Ninja、可用的 MSVC x64 C/C++ 编译器及 Windows SDK；准备 Qt 6.8.3 MSVC x64 **开发套件**和 OpenSSL 3.5.9 x64 **开发头文件及库**。只有 Qt/OpenSSL 源码归档或运行时 DLL 不够。工具版本及 Qt 构建参数会影响结果；本次使用的本地版本见上节，Qt 的完整构建配置和其他传递依赖仍需核实。

在 **x64 Native Tools Command Prompt for VS 2022 启动的 PowerShell** 中，切换到独立源码副本根目录。先核验并准备固定的 Deskflow 源码 ZIP：本机可使用 `.artifacts/deskflow-multidevice-source-review/` 的经核对副本；新的检出需要从前述上游链接单独获取，不能假设 Git 仓库包含压缩包。目录和补丁应位于可信的非链接路径。以下命令只创建新的 `.artifacts/deskflow-multi-<随机标识>`，不覆盖现用应用或配对资料：

```powershell
$ErrorActionPreference = 'Stop'
$project = (Get-Location).Path
$archive = Join-Path $project '.artifacts/deskflow-multidevice-source-review/deskflow-v1.27.0-source.zip'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash -ine '8493dfa58dbeac161a8b25d5b3cb7cb8546b5f4d99a216fcd65296b220a864f0') { throw 'Deskflow source archive hash mismatch.' }
$patches = @(
    @('0001-current-user-native-control.patch', '4553424fcf97b368c4356d647d5ed25b7e529daba1b00fe4cebd6a8f047e5324'),
    @('0002-preview-input-isolation-experimental.patch', '0c0f7937ddf61311415806da3f2fa37c310b3e089a98a7b5933e0daa16f4ffce'),
    @('0003-multidevice-conditional-switch-experimental.patch', 'c25888d4f6d952dcb85872173b82414ac8e3d1abd20e2ebf6f59c671118e9d1c'),
    @('0004-multidevice-identity-binding-experimental.patch', '67bfc277bd5be9cd95eeeea664988aeccb9d01e8a9d3b34b4383a70339c0d58c')
)
foreach ($row in $patches) {
    $patch = Join-Path $project ('vendor/deskflow/metadata/' + $row[0])
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $patch).Hash -ine $row[1]) { throw "Patch hash mismatch: $($row[0])" }
}
$work = Join-Path $project ('.artifacts/deskflow-multi-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $work) { throw 'Choose a new work directory.' }
New-Item -ItemType Directory -Path $work -ErrorAction Stop | Out-Null
& python -m zipfile -e $archive $work
if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed.' }
$source = Join-Path $work 'deskflow-081f6478e654a112168879e67a41ce7d7e4ae3af'
if (-not (Test-Path -LiteralPath (Join-Path $source 'CMakeLists.txt') -PathType Leaf)) { throw 'Unexpected source layout.' }
foreach ($row in $patches) {
    $patch = Join-Path $project ('vendor/deskflow/metadata/' + $row[0])
    & git -C $source -c core.autocrlf=false apply --check -- $patch
    if ($LASTEXITCODE -ne 0) { throw "Patch check failed: $($row[0])" }
    & git -C $source -c core.autocrlf=false apply -- $patch
    if ($LASTEXITCODE -ne 0) { throw "Patch application failed: $($row[0])" }
}
```

上述 ZIP 是经固定哈希核对的上游归档，不能把解压命令套用到来源不明的文件。Python 在本次 Windows 测试中将归档的符号链接项提取为包含链接目标文本的普通文件，复现了历史 Windows 源码树；这不是正确还原 Linux/macOS 符号链接的通用方法。补丁任一步失败立即停止，保留目录检查；不要跳过失败或改写正式软件。

在同一 PowerShell 继续，将下面两条路径替换为独立构建机实际安装的开发套件。以下仅编译核心，不部署运行库或生成安装包：

```powershell
$qtRoot = 'D:/toolchains/Qt/6.8.3/msvc2022_64'  # 自行修改
$opensslRoot = 'D:/toolchains/openssl-3.5.9-x64' # 自行修改
$build = Join-Path $work 'build'
if (-not (Test-Path -LiteralPath (Join-Path $qtRoot 'bin/windeployqt.exe') -PathType Leaf)) { throw 'Qt development tools missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $opensslRoot 'include/openssl/ssl.h') -PathType Leaf)) { throw 'OpenSSL development headers missing.' }
$env:PATH = (Join-Path $qtRoot 'bin') + ';' + $env:PATH
& cmake -S $source -B $build -G Ninja -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl -DCMAKE_BUILD_TYPE=Release -DCMAKE_RC_FLAGS=/c65001 -DBUILD_TESTS=OFF -DBUILD_INSTALLER=OFF "-DCMAKE_PREFIX_PATH=$qtRoot" "-DOPENSSL_ROOT_DIR=$opensslRoot"
if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed.' }
& cmake --build $build --target deskflow-core --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Deskflow core build failed.' }
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $build 'bin/deskflow-core.exe')
```

缺少库、编译器或 SDK 时应停止并核对安装，不要以未知预编译文件绕过检查。新核心哈希与历史值不同属于已观察现象，不能据此判定等价或不等价；构建成功后还需单独验证运行时依赖、许可证、对应源码、签名和真实设备。不要把新核心替换到现用正式版。

本次另外以 `python -m zipfile` 和以上补丁顺序在新的隔离目录实际执行解压和应用：843 个提取后文件与现存历史树对应文件均逐字节一致；历史树多出的 `vcpkg.json` 由后续 CMake 配置生成。实际编译验证使用同样的 CMake 参数及本机缓存，其范围见前述核验结果。

## 许可与未完成事项

Deskflow、QtBase 和 OpenSSL 源码原始归档均保留各自的许可证和版权文本；其中 QtBase 源码中的许可选择因文件和模块而异。Deskflow 的 OpenSSL 例外只适用于上游明确声明该例外的文件，不能扩展为项目整体例外。参考 [Qt 官方许可说明](https://doc.qt.io/qt-6.8/licensing.html)、[Deskflow 固定版本许可](https://github.com/deskflow/deskflow/blob/081f6478e654a112168879e67a41ce7d7e4ae3af/LICENSE)和 [OpenSSL 固定版本许可](https://github.com/openssl/openssl/blob/openssl-3.5.9/LICENSE.txt)。

当前 Qt 和 OpenSSL 的历史安装缓存、编译选项、第三方传递依赖及各二进制的对应源码仍需核对。仅有源码归档和补丁不等于具备完整发行对应源码；需核验构建/安装脚本、实际使用的 Qt 模块、可替换条件、许可通知和 Mac 官方镜像来源。重建产物即使与历史二进制哈希不同，也不能据此单独判断功能或源码是否对应。
