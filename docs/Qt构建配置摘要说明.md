# Qt 构建特征摘要（隔离审查）

`scripts/qt_config_summary.py` 在四源码原生构建成功后，只读取本次唯一工作目录的 `qtbase-build/config.summary`、`qttools-build/config.summary` 和已完成构建记录，检查构建记录中的固定来源、补丁、工具和核心字段，再并列输出该记录的核心 SHA-256 与 Qt 配置摘要；摘要不会重新读取源码、补丁或核心，也不能独立证明配置与核心来自同一构建过程。

摘要包含两份原始 summary 的 SHA-256，以及 QtBase 26 项、QtTools 10 项固定名称的布尔值；未列出的选项、其他依赖与原始日志不输出。`null` 表示该可选项没有被识别，不能推断为开启或关闭；重复或非 yes/no 的已选项会失败。QtBase 的共享库、OpenSSL、OpenSSL 3、Network、Widgets 和 QtTools 的 Linguist 是核心构建要求，这六项必须识别为 yes 才能输出。摘要不输出上游 summary 的自由文本或路径；异常只报告预设阶段名。文件哈希只是原始文件指纹，不能据此恢复未保留的完整 summary。

```text
python -B -m unittest discover -s tests -p test_qt_config_summary.py
python -B scripts/qt_config_summary.py
```

手工配置工具链的本机记录可显式使用 `python -B scripts/qt_config_summary.py --local-manual`，结果使用独立 local schema；缺失版本不补造，且在 GitHub Actions 环境中拒绝该模式。具体边界与本机实测见[原生构建入口核验](Windows原生构建入口本机核验.md)。摘要仅检查记录字段和读取的配置摘要，不重新核算核心与补丁，也不独立确认原构建退出码。

首次命令不联网，第二条要求已有唯一、完整且已校验的独立构建记录。仅列出的功能选项不构成完整软件物料清单、对应源码或 Qt/第三方许可已完成的证明。实际安装包、动态加载插件、构建工具与操作系统依赖，仍需按最终载荷检查。

已在本机读取现有真实 QtBase 与 QtTools 配置摘要：白名单字段分别可识别 26/26 和 10/10；二者来自两次不同的隔离构建，不能合并成同一次 GitHub 新运行器成功证据。未来在云端只应按实际一次构建的记录报告。
