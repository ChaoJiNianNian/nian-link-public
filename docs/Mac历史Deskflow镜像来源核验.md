# Mac 历史 Deskflow 镜像来源核验（内部审查）

核验范围：一份历史年年互联多设备 Mac 内部测试组合包中的 Deskflow ARM64 安装镜像。此记录核对实际文件与 Deskflow 官方发布资产元数据的关系，不是镜像签名、公证、对应源码完整性或第三方发行许可结论，也不证明当前或未来安装包包含相同文件。

## 核验对象

- 本地历史组合包：项目内 `.artifacts/mac-export/test-ee5a7da5d61d47cdbfa877331ceb62ea/NianDeskLink-macOS-arm64.zip`。其同目录 `manifest.json` 标记 `0.2.0`、`internal-test`、`multi-device-test`；此目录仅为本机历史测试材料，并非仓库内的公开下载入口。
- 组合包 SHA-256：`fa3889f781ea29b481e099de505d92e26e22f9c29b37257e13f95335f907fe94`，本次对组合包实际文件计算，与其 manifest 声明一致。
- 被核验的唯一同名 ZIP 条目：`deskflow-1.27.0-macos-arm64.dmg`；解压后的大小为 **29,145,871 字节**，流式读取所得 SHA-256 为 `dd21e79e566314394214906d3338c505d337af536fc580c022ee3fadffad0c97`。读取过程中没有安装或运行镜像，也没有把镜像解压到磁盘。

## 上游发布元数据

于 **2026-11-15 05:25:06–05:25:07（香港时间，UTC+8）**，通过 HTTPS 读取 [Deskflow v1.27.0 官方 GitHub Release API](https://api.github.com/repos/deskflow/deskflow/releases/tags/v1.27.0)。响应的 `tag_name` 为 `v1.27.0`，同名资产的大小、`digest` 和下载链接分别为：

- `size`：`29145871`
- `digest`：`sha256:dd21e79e566314394214906d3338c505d337af536fc580c022ee3fadffad0c97`
- `browser_download_url`：[deskflow-1.27.0-macos-arm64.dmg](https://github.com/deskflow/deskflow/releases/download/v1.27.0/deskflow-1.27.0-macos-arm64.dmg)

对应 [Deskflow v1.27.0 发布页面](https://github.com/deskflow/deskflow/releases/tag/v1.27.0)显示发布版本与提交短标识 `081f647`。本次资产元数据的大小和 SHA-256 与历史组合包中的镜像逐项一致；组合包脚本 `scripts/build-mac-export-test.py` 内固定的镜像 SHA-256 也相同。

## 方法及限制

在项目目录只读打开上述组合包，检查恰有一个完全同名条目，并使用 Python `zipfile` 从该条目流式读取原始镜像数据计算 SHA-256 与字节数；另用 `Get-FileHash -Algorithm SHA256` 计算整个组合包。官方元数据通过 `Invoke-RestMethod` 对指定 Release API 按资产名称精确筛选取得，查询时提供普通 User-Agent，不输出账户凭证或任何本机配置。此次没有重新下载 Release 资产：结论是历史镜像与**该时点官方 API 所报告的资产摘要**一致，并非两份独立下载文件的逐字节比较，也不是独立签名验证。

GitHub API 所提供的摘要与发布记录不能替代 Apple `codesign`、`spctl` 或公证检查，也不能单独证明镜像内其他组件许可、所有对应源码或安装脚本已齐全。后续如拟分发其他组合包，须对其实际载荷重复核验；应在合适的 Mac 测试环境独立核对镜像与内部 App 的签名、公证及第三方组件，并分别记录结果。现用正式版和用户配对资料不属于本次核验对象。

本记录仅含已公开的上游链接、版本与散列，以及项目内部相对路径和测试产物标识；不含配对令牌、私钥、证书、用户目录、局域网地址、接收文件或个人使用数据。路径所指历史测试产物不会因本文而自动收录到源码或私有仓库。
