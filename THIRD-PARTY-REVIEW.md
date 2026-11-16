# 第三方材料核查摘要（公开源码审查）

本摘要供源码审阅者了解当前第三方材料及其局限；它不是完整组件清单、许可兼容性结论或二进制发行许可证明。具体许可、版权声明和例外以所分发文件为准；项目自有代码的根许可证不替代第三方条款。源码中逐文件及素材权属仍需持续核实。

| 材料 | 目前有据可核对的内容 | 尚待完成 |
| --- | --- | --- |
| Deskflow | 上游 1.27.0 固定提交 `081f6478e654a112168879e67a41ce7d7e4ae3af`；项目留存 0001—0004 补丁和来源记录；[上游许可文本](vendor/deskflow/metadata/licenses/Deskflow-LICENSE.txt)与[OpenSSL 例外原文](vendor/deskflow/metadata/licenses/LicenseRef-OpenSSL-Exception.txt)随源码审查材料保留。 | 源码快照不含完整上游源码或完整依赖；多设备测试核心与新构建产物哈希不同，未证明可复现或运行等价。按最终发行物核对实际源码、各文件许可、构建与安装脚本；哈希不同不能单独说明源码一定不对应。 |
| Qt / OpenSSL | 内部构建记录为 QtBase 6.8.3、OpenSSL 3.5.9；可参考 [Qt 6.8 官方许可说明](https://doc.qt.io/qt-6.8/licensing.html)和[OpenSSL 3.5.9 许可](https://github.com/openssl/openssl/blob/openssl-3.5.9/LICENSE.txt)。 | 当前源码候选不含二进制运行库及全部对应源码；对最终载荷逐组件确认许可证、第三方声明、构建选项及再分发条件。 |
| Avalonia、.NET、WebView2、Inno Setup、Lucide 等 | 工程内已有依赖声明和部分第三方通知；具体内容、版本以实际构建文件为准。 | 汇总锁定版本、传递依赖、源文件头和许可文本，检查最终交付物；不能把现有草案视为清单完备。 |
| Mac Deskflow 与素材 | Mac 测试版依赖已安装的 Deskflow；部分旧测试组合包另含官方 DMG。项目品牌素材来源已记录。 | 确认最终是否分发 DMG、来源与许可；确认素材使用权和商标近似情况。 |

不得将测试版或历史安装包的许可盘点当成对新版本的自动批准。二进制发行前仍须核验对应源码、内容权属、依赖许可、私人信息和干净构建；实际分发或共享的义务需按具体接收方、文件及条款判断。

本轮已保留并按原始归档校验 QtBase 和 OpenSSL 的 132 份许可及归因材料、QtTools 的 25 份材料，分别见 [QtBase](vendor/notices/qtbase-6.8.3/README.md)、[QtTools](vendor/notices/qttools-6.8.3/README.md) 和 [OpenSSL](vendor/notices/openssl-3.5.9/README.md)。历史及新运行时原始许可分别保留；具体问题见[二进制载荷依赖与许可审查](docs/二进制载荷依赖与许可审查.md)。材料齐备不等于发行合规已完成。

进一步的历史 Qt 来源和 PE 签名字段核对见[Qt 历史文件来源核验](docs/Qt历史文件来源核验.md)；两项托管依赖的许可证历史及来源核验见[托管依赖材料](vendor/notices/managed-runtime-publish-review.md)。最终发行载荷及许可条件仍须逐项确认。
