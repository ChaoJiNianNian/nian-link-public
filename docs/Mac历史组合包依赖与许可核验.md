# Mac 历史组合包依赖与许可核验

本记录针对一份历史内部测试组合包开展只读核验，不是最终发行包的物料清单或发行许可结论。未安装、运行或修改现用软件，也未读取真实配对数据。

## 核验对象与方法

- 对象：`.artifacts/mac-export/test-ee5a7da5d61d47cdbfa877331ceb62ea/NianDeskLink-macOS-arm64.zip`；组合包 SHA-256 为 `fa3889f781ea29b481e099de505d92e26e22f9c29b37257e13f95335f907fe94`。ZIP 共有 268 个条目，本次逐项 CRC 检查未发现损坏；该包不随本记录提交。
- 包内一个 `deskflow-1.27.0-macos-arm64.dmg` 与官方 Release API 所报大小及 SHA-256 一致，详见[历史 Deskflow 镜像来源核验](Mac历史Deskflow镜像来源核验.md)。本轮没有重新下载或安装 DMG，也没有在 macOS 验证签名与公证。
- 只读比较 ZIP 内许可、通知及四个程序集与既有来源材料；下表的“逐字节一致”仅适用于具体比较对象，不涵盖未检查的动态加载组件、操作系统框架和其他载荷。

## 许可原文

| 组合包内文件 | 对照来源 | SHA-256 |
| --- | --- | --- |
| `Deskflow-LICENSE.txt` | [Deskflow 固定提交根 LICENSE](https://github.com/deskflow/deskflow/blob/081f6478e654a112168879e67a41ce7d7e4ae3af/LICENSE) | `d8c320ffc0030d1b096ae4732b50d2b811cf95e9a9b7377c1127b2563e0a0388` |
| `Deskflow-LICENSE_EXCEPTION.txt` | [同一提交的 LICENSES/LicenseRef-OpenSSL-Exception.txt](https://github.com/deskflow/deskflow/blob/081f6478e654a112168879e67a41ce7d7e4ae3af/LICENSES/LicenseRef-OpenSSL-Exception.txt) | `ee89f0560ee2e5ca5dee782611ec18e9bd472d1372b0631dd0537023ae9bf3a9` |
| App 内 `MicroCom.Runtime-0.11.6-LICENSE.txt` | [MicroCom 固定提交 LICENSE](https://github.com/kekekeks/MicroCom/blob/76785efcafd91b5902fd19dd11145f6dd655b7b4/LICENSE) | `6ee769c9ac4dac9abb16b98b1341e9528ff9f4ab685481410d3376d14148f3a9` |
| App 内 `Tmds.DBus.Protocol-0.94.1-COPYING.txt` | [Tmds 固定提交 COPYING](https://github.com/tmds/Tmds.DBus/blob/b4a7fed0b878f74cb54f7cca84d2889af4e596ba/COPYING) | `aca3b49277abcc7886ccb2f115603a9f5b15fc3f96019a10c00f2974098b210c` |

以上四份分别从组合包读取，并与指定官方固定提交的 HTTPS 原文逐字节比较一致，也与仓库内已保存的对应许可原文一致。Deskflow 组合包的例外文件名与上游路径不同，内容一致；其适用范围依上游各文件声明判断，不应推定涵盖整个项目。

App 内 HarfBuzzSharp.NativeAssets.macOS 8.3.1.3、SkiaSharp.NativeAssets.macOS 3.119.4 和 Microsoft.NETCore.App.Runtime.osx-arm64 10.0.13 各自的 LICENSE 与 THIRD-PARTY-NOTICES 共六份文件，也与仓库 `vendor/notices/mac-runtime-packages/` 的对应文件逐字节一致。此前材料的来源与范围见[托管依赖与许可核查](托管依赖与许可核查.md)。

## 载荷中的四项二进制

本次在内存中读取历史 App 和本机保留的同版本 NuGet 归档，四个载荷分别与下列归档条目逐字节一致：

| 包 | 匹配的归档条目 | App 内载荷 SHA-256 |
| --- | --- | --- |
| MicroCom.Runtime 0.11.6 | `lib/net8.0/MicroCom.Runtime.dll` | `2538ec6f3c24e901a90e33c37788b9a4e4b6ae8158cab7328a00a00bb28eab9e` |
| Tmds.DBus.Protocol 0.94.1 | `lib/net9.0/Tmds.DBus.Protocol.dll` | `60a1c5255731d8ac843cab53108be8ba1ed02759ce3db0da5b49939ebe97bd8a` |
| SkiaSharp.NativeAssets.macOS 3.119.4 | `runtimes/osx/native/libSkiaSharp.dylib` | `e09f07ae1df62ded475351f56d8dc8366cd679043f28b65d9dee597a5fd0da6c` |
| HarfBuzzSharp.NativeAssets.macOS 8.3.1.3 | `runtimes/osx/native/libHarfBuzzSharp.dylib` | `b2d7d39a46954b3d7310540bdf393f398ac4194ee029b995deeb1db0c161c43d` |

MicroCom 与 Tmds 的 NuGet 归档 SHA-512 与已保存[来源清单](../vendor/notices/managed-runtime-license-source-manifest.json)一致，ZIP CRC 检查通过；此前本机验签的信任与吊销条件见[来源审查](../vendor/notices/managed-runtime-publish-review.md)。另两项比较使用本机缓存归档，本轮未独立重下载或验签。匹配某个包内文件不能证明所有子依赖、来源或安全性质。

## 后续 DMG 内容只读检查（2026-11-15）

从上述同一个历史 ZIP 中将 `deskflow-1.27.0-macos-arm64.dmg` 单独复制到新的隔离文件，以 SHA-256 核对为 `dd21e79e566314394214906d3338c505d337af536fc580c022ee3fadffad0c97`，与既有官方来源记录一致；本次没有重新计算外层 ZIP 的摘要。使用 7-Zip 24.08 的 `l -slt` 查看镜像目录、`t` 检查归档，输出 `Everything is Ok`，列示 50 个目录和 86 个文件。

目录列出 Deskflow 和 deskflow-core、Qt 的 Core、DBus、Gui、Network、Svg、Widgets 六个 framework、10 个 Qt 插件及应用资源中的 LICENSE 和 LICENSE_EXCEPTION。镜像含 framework 符号链接；本次没有将应用提取到磁盘，也没有挂载、安装或运行。归档完整性与出现签名目录均不证明 Apple 签名或公证有效，也不证明每个组件的许可或动态依赖已完整核对。

## 仍待完成

该历史 App 的 `runtimeconfig.json` 记录自包含 .NET 运行时 10.0.13；它不是 10.0.15 的新托管发布，也不是当前产品发行物。此版本处于微软公告列出的受影响范围，实际暴露取决于应用路径，详见[载荷与许可审查](二进制载荷依赖与许可审查.md)；不能仅凭版本判断可利用性。

后续应针对确定的最终 Windows / Mac 发行载荷重新制作完整依赖及许可清单，核验 Deskflow DMG 的 Apple 签名、公证与镜像内部组件、各原生动态库及系统框架、对应源码与分发条件，并在干净设备上验证安装与更新。八份历史测试源仍未找回；上述核验不解决该问题，也不证明可直接公开发布。
