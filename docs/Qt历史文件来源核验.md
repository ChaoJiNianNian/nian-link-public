# 历史 Qt 二进制来源核验（内部审查）

核验对象是某一份 Windows 免文件便携测试包 `Keyboard` 中的 4 个 Qt 动态库和 2 个平台插件。它们并非对所有历史版本或最终发行载荷的核验。此记录不包含用户数据或实际二进制文件，也不等于发行许可结论。

## 上游来源与源码线索

- Qt 官方的 [6.8.3 MSVC 2022 x64 QtBase 包信息](https://download.qt.io/online/qtsdkrepository/windows_x86/desktop/qt6_683/qt6_683/qt.qt6.683.win64_msvc2022_64/6.8.3-0-202503201308qtbase-Windows-Windows_11_23H2-MSVC2022-Windows-Windows_11_23H2-X86_64.7z.mirrorlist)标注该二进制归档大小 39,484,220 字节、SHA-256 为 `41688269fac0565db956c66d9eecae777d16197e0c02cd81b88640c1f5d73d3f`。本轮重新从其官方地址下载并验证，哈希也与旧工具链锁定记录一致。
- 官方归档内 `sbom/qtbase-6.8.3.spdx.json` SHA-256 为 `73b4dee169c8f3a1c0c2e2f904d0679cc156ce7e9320f7c866bb723dd4faac68`，与留存的旧开发缓存 SPDX 逐字节相同。该 SPDX 记载上游 qtbase 源码提交为 `c07c2d5a527a644d36e7853d55132ae38921682f`、编译器 MSVC 19.39.33520.0；本轮查询 [Qt 官方 qtbase 源码仓库](https://github.com/qt/qtbase/tree/c07c2d5a527a644d36e7853d55132ae38921682f)，`v6.8.3` 标签解引用恰为该提交。这支持版本和上游源码来源，尚未逐项核验二进制所需的全部源码树和构建配置。
- 同一官方二进制归档 `mkspecs/qconfig.pri` 记录 Qt 6.8.3、`x86_64` 及 `shared`、`debug_and_release`、`openssl` 等配置；官方 SPDX 中这些文件标为 `RelWithDebInfo`。这些是部分构建配置线索，不能代替全部编译选项、依赖或可替换条件的验证。
- 单独收集的 [QtBase 6.8.3 源码归档与许可材料](../vendor/notices/qtbase-6.8.3/README.md)使用官方归档，SHA-256 为 `56001b905601bb9023d399f3ba780d7fa940f3e4861e496a7c490331f49e0b80`。以官方 GitHub 的固定提交 tar.gz 与官方发布 tar.xz 对照常规文件内容、长度及链接目标：Git 归档的 21,966 项全部对应且相同，0 项内容不同、0 项仅存在于 Git 归档；发布 tar.xz 额外含 `.QT-ENTERPRISE-LICENSE-AGREEMENT`、`.release-timestamp` 两项。Git 归档 SHA-256 为 `6460660324c1fbe608af6d1ab029321de4d713baf1eb143f2000ec41fb7e5483`；对照范围不包括不同归档容器元数据、可执行权限或子模块外部内容。

## 历史文件逐字节对照

从固定官方二进制归档读取以下 6 个条目，与历史测试包逐字节比对，均完全相同。下表 SHA-256 属于实际官方文件；后两列分别为实际文件 SHA-1 与官方包自带 SPDX 所记 SHA-1。

| 官方归档条目 | 文件 SHA-256 | 实际文件 SHA-1 | SPDX 记录 SHA-1 |
|---|---|---|---|
| `bin/Qt6Core.dll` | `0cc62e841959fb8f6d366ea116c6f6bbc273e30f0ef90d8aa2e4adeb81fd63f8` | `0cabf6343f2201cd3f1478114cecd922e0ffac49` | `cc3fbaad8f4e94786fb6e9bc1db5758ae30b5568` |
| `bin/Qt6Gui.dll` | `ca9f20d9b59d5bc321a87005b926b853ebcd6a2394823e14d3c8b5fc5702fa8c` | `9c6f1f495ec340231b448421020bb36b0178a093` | `511a65e6d155e32a90855c8cd2016f880173f70a` |
| `bin/Qt6Network.dll` | `2c2f65c70989646f06d668883c031f06fac3f8de1f459278c94cc50dfe89988d` | `fe9a11c62ffc25aecbf92b9c4827588593aaa062` | `fdc2193d69a4e9244b6b66a764beb4c36e338c66` |
| `bin/Qt6Widgets.dll` | `d34ef78b3d3b5506ee8279ce6efbfc263d773b74860af76e66b0715346897a3a` | `6c22c9a847d2c2ddb6804eeafe9f00829bf3c5bf` | `244e72908b8f2612602622d5ae3afb0f2cf2edec` |
| `plugins/platforms/qoffscreen.dll` | `733d193144fb9c8a00b6d5e96c730668c86aef16db63754914d11e135d56e5a2` | `3354d2ae8417c283892d2432af6ff28723af384c` | `2ee537d701b932c5bd0c908cc8be6341959292e2` |
| `plugins/platforms/qwindows.dll` | `12577a7c4f2230bbea53f279573d7e3d1a08d631197855481e1e1580f61bb877` | `ef3929adbc16acf0d0824b8c47fc6a9e3ec5a120` | `600dd00633e84f366f9c1a7fb171462aeef23a80` |

**六项实际签名文件的 SHA-1 均与官方 SPDX 所载值不一致。** 严格检查每个 PE32+ x64 文件后，在内存中移除末尾证书表，并将 PE CheckSum 字段和 Security Directory 条目置零，所得字节的 SHA-1 对六项均恰好等于 SPDX 的记录。实际文件未修改。该字节关系支持 SPDX 对应签名前状态的解释，但不能单凭它证明 Qt 的实际签名流程或先后顺序。旧缓存与官方归档的 SPDX 相同，不匹配不能简单归因为缓存串版。Qt 官方对[构建 SBOM 的说明](https://www.qt.io/blog/qt-6.8-software-bill-of-materials)称其包含二进制 SHA-1；应使用上表 SHA-256 验证这些实际分发文件，不要直接用 SPDX 里的 SHA-1 校验签名后文件。

## 复核方法

准备固定上游 QtBase `.7z` 和历史测试包中解压出的 `Keyboard` 目录，使用可读取 7z 的 `tar`，在独立工程中执行：

```text
python scripts/verify_qt_official_archive.py <官方QtBase归档路径> <历史Keyboard目录路径>
python -B -m unittest discover -s tests -p test_verify_qt_official_archive.py
```

工具先核对固定归档 SHA-256，再通过 `tar -xOf` 将固定条目读取到内存，不安装或运行二进制、不写入待核目录；核对原始 SPDX SHA-256、六项历史文件内容及 SPDX 数据并输出 JSON。六项均逐字节相同时退出码为 0；即使 SPDX 六项不匹配也会单独在输出中标明，不能把退出码当成许可证通过。文件缺失、归档或 SPDX 哈希不对则失败。合成回归不下载文件。请在可信目录使用，避免其他进程并发替换待核归档或文件。

此外可对每个已经核验的 Qt DLL 或插件执行：

```text
python scripts/qt_pe_unsigned_hash.py <Qt文件路径>
python -B -m unittest discover -s tests -p test_qt_pe_unsigned_hash.py
```

该工具验证 PE32+ x64、节区、证书表对齐及末尾位置、证书记录边界后，仅在内存中截去末尾证书表并将上述两个头部字段清零，输出归一化 SHA-1；拒绝无法安全解析的输入。它不验证签名或证明签名前的唯一原文件，也不修改目标文件。六组负向/正向合成测试通过，历史六项均与 SPDX 记录相符。

Qt 的实际许可选择、构建参数、第三方依赖、源码与安装脚本、替换或重链接条件及最终载荷通知仍需按实际分发方式核对。历史 Deskflow 核心与本项目四项补丁源码的关系、Mac 版来源、干净设备构建和运行验证均不由该结果证明。
