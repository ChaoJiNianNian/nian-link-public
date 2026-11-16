# QtBase 6.8.3 原文与上游归因材料

这些材料从固定版本 QtBase 6.8.3 源码归档逐字节提取，不是针对某个二进制已完成的发行许可结论。`LICENSES` 包含上游 SPDX 许可与例外原文；`attribution` 是上游全部 48 份归因文件；`referenced-license-texts` 收录其中 46 份不同的、上游 `LicenseFile` 明确引用的文件。为使源码审查工具识别文本，引用文件的存储名在原路径后加 `.txt`；Qt 原始归因文件内路径没有改写，因此它们之间的原生相对路径不会在本目录直接解析。请按上一级 `upstream-license-source-manifest.json` 的原归档路径和哈希映射查阅，也可查原始完整源码归档。

上游 37 份 SPDX 文本包含多种可选许可及商业许可引用；收录这些文本并不授予商业许可，也不代表实际构建使用了所有列出的组件。需要按实际 Qt DLL、插件、构建配置和分发方式逐项核对选择、版权、源代码与其他条件。

QtBase 来源：[官方源码归档](https://download.qt.io/archive/qt/6.8/6.8.3/submodules/qtbase-everywhere-src-6.8.3.tar.xz)，归档 SHA-256 `56001b905601bb9023d399f3ba780d7fa940f3e4861e496a7c490331f49e0b80`，与 Qt 官方对应校验值一致。

本机 Qt 安装缓存中另有 SPDX 文件，含构建环境路径；该文件保留在本地审查材料中，未纳入本源码候选。其 6 个 Qt DLL/平台插件 SHA-1 均与本次历史测试包中的对应文件不符；这些文件本身与该本机 Qt 缓存中的 DLL 完全一致，并显示有效的 The Qt Company Oy Authenticode 签名。对隔离副本去除签名后重算仍不匹配；原因未知，不能把此 SBOM 当作该批二进制的逐文件对应证明。
