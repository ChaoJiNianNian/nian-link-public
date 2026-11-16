# Mac 托管发布的 MicroCom 与 Tmds 许可材料

本目录补充隔离 `osx-arm64` 托管发布的两项依赖的来源清单与核验证据。两份许可原文此前已保存在 `src/DesktopToolbox.Mac/Assets/ThirdParty/`；本次 vendor 副本与既有文件及固定上游提交的原文均逐字节相同，原有文件不代表最终发行载荷已完整附带。本文不是最终安装包 SBOM 或发行许可结论。核查候选为 .NET 10.0.15 自包含发布；对应 NuGet nuspec 均声明 MIT，且均未在包根包含许可证原文。

- MicroCom.Runtime 0.11.6：包的 nuspec 指向固定上游提交 `76785efcafd91b5902fd19dd11145f6dd655b7b4`；[许可原文](MicroCom.Runtime-0.11.6/LICENSE.txt)逐字节来自[该提交的 LICENSE](https://github.com/kekekeks/MicroCom/blob/76785efcafd91b5902fd19dd11145f6dd655b7b4/LICENSE)，[原始 nuspec](MicroCom.Runtime-0.11.6/package.nuspec.txt)逐字节来自 NuGet 原始包。
- Tmds.DBus.Protocol 0.94.1：包的 nuspec 指向固定上游提交 `b4a7fed0b878f74cb54f7cca84d2889af4e596ba`；[许可原文](Tmds.DBus.Protocol-0.94.1/LICENSE.txt)逐字节来自[该提交的 COPYING](https://github.com/tmds/Tmds.DBus/blob/b4a7fed0b878f74cb54f7cca84d2889af4e596ba/COPYING)，[原始 nuspec](Tmds.DBus.Protocol-0.94.1/package.nuspec.txt)逐字节来自 NuGet 原始包。为了作为文本纳入源码审查，COPYING 保存为 LICENSE.txt；内容未经更改。

[来源清单](managed-runtime-license-source-manifest.json)固定两份上游源码 ZIP 的 SHA-256、完整 NuGet 归档 SHA-512、原文及发布 DLL 的 SHA-256。两个上游归档通过 ZIP CRC；两个 NuGet 包通过本机 `dotnet nuget verify`，该验签依赖主机信任和吊销配置。两份 DLL 均与相应 NuGet 归档选定条目逐字节一致；哈希不证明构建产物安全或许可证义务已全部完成。

此外，从该 `osx-arm64` 发布的 deps 中抽取 17 个 NuGet 包的 29 个 runtime/native 条目，并将每个发布文件与对应 NuGet 归档条目逐字节比较，全部一致；Windows 托管候选中的 WebView2 包 1 个条目也一致。这仅覆盖本次托管发布里的这些条目，不涵盖 .NET 自包含运行时、动态加载、Mac 原生媒体、Deskflow、Qt、操作系统依赖、资源和最终安装包。包来源及各原始许可证仍需按最终载荷核对。
