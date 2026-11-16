# Deskflow 源码材料：0.1.9 Windows 测试引擎

这份记录用于准备可审查的来源材料，**不是已完成许可证合规的声明，也不证明引擎已经从这些源码完整重建**。实际软件作者应按最终分发物、第三方许可证和法律要求逐项核对。

## 当前材料

0.1.9 Windows 测试载荷记录的修改版 `deskflow-core.exe` SHA-256 是 `6d06f16034edad54e6d51aad3df45e1e90e0c1d88b34489a69ff1cd5bd39b751`。目前可核对的源码材料包括：

- Deskflow 1.27.0，提交 `081f6478e654a112168879e67a41ce7d7e4ae3af`，上游源码 ZIP SHA-256 `8493dfa58dbeac161a8b25d5b3cb7cb8546b5f4d99a216fcd65296b220a864f0`；原文件的版权、许可证和 OpenSSL 例外保留在原始源码中。
- 修改补丁 `0001-current-user-native-control.patch`，SHA-256 `4553424fcf97b368c4356d647d5ed25b7e529daba1b00fe4cebd6a8f047e5324`。已对固定上游源码只读执行 `git apply --check`，通过；这不等于已经构建或验证二进制一致。
- QtBase 6.8.3 与 OpenSSL 3.5.9 的固定版本原始源码归档、哈希和原有许可文本；源码归档与本机记录一致，但 Qt 构建配置、重链接/替换条件、其他传递依赖及对应源码范围仍需审核。
- 历史构建脚本、工具链锁和 `0002-preview-input-isolation.patch` 作为来源记录保留。**0002 不是已确认属于 0.1.9 测试引擎的补丁**；历史脚本默认值和目录要求来自旧开发环境，不能直接视为可运行或复现的公开构建入口。

Mac 组合包里另含官方 Deskflow 安装镜像，其对应源码及分发要求需要单独核对；不能用 Windows 自编引擎的记录代替。

## 整理及验证

独立的 Windows 源码准备及手动构建草案见 [Deskflow手动构建.md](Deskflow手动构建.md)。该草案不运行历史脚本，也尚未经过干净环境完整实测。

`scripts/prepare_deskflow_source.py` 在指定的材料根目录中只读取固定白名单文件，边复制边校验 SHA-256，逐项复核生成的 ZIP，并拒绝覆盖已有文件和链接路径。默认只预览；加 `--apply` 才创建候选 ZIP。它不会下载、运行旧构建脚本或提取运行程序。

```text
python scripts/prepare_deskflow_source.py --source-root <recorded-materials-root> --output <new-candidate.zip>
python scripts/prepare_deskflow_source.py --source-root <recorded-materials-root> --output <new-candidate.zip> --apply
```

`<recorded-materials-root>` 必须有脚本固定白名单中所列的相对路径和精确文件；这一步本身依赖现有记录，不代表公开仓库已经能从零构建。不要运行 ZIP 内的历史脚本来替换现有应用。

当前已在隔离目录生成一个内部候选：`deskflow-sources-0.1.9-test.zip`，大小 103,570,733 字节，SHA-256 `51d56961a0979acd1a01b04a4bc4bf2becf101222a07f66565c527f60c45c1c`。它包含 11 项固定材料及清单、说明；已逐项读取校验。没有上传或对外发布。

公开分发前还需独立构建验证、审查准确对应源码与脚本、Qt/OpenSSL 和其他依赖许可、上游 Mac 镜像、完整源/二进制对应关系及安全隐私。不要把该 ZIP 与法律合规认证混同。
