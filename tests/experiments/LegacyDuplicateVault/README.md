# 旧版重复设备凭证：隔离反例

这是待解决的安全研究，不是产品修复。使用当前真实 `MultiDeviceCredentialVault` 和 Windows 用户保护器，测试只在显式指定的独立临时目录创建全新合成 v1 设备资料，不访问现用配对。两台合成设备名称、指纹及槽位各不相同，但凭证解码后相同，且其中一份故意采用不同的非规范 Base64 字符串。不会在测试输出中打印凭证。

五个预期失败分别覆盖：当前身份认证分别将同一凭证接受给两个不同名称（两项）；按目标设备读取的端点凭证仍可取得；真实 runtime 的两个设备设置索引相同（仅通过反射取索引比较，不打印派生值、不启动 runtime）；移除其中一个设备后重开，持有被移除设备相同凭证的一方仍可通过另一名称认证。单设备非规范编码作为兼容对照应通过，并验证打开/关闭不改写合成文件。这只证明预先存在异常重复凭证时的行为，不证明随机生成的凭证已发生碰撞或可以凭空伪造；索引相同不等同于实际配置串用已经发生。

本机隔离运行和再次运行均为 4 项预期失败、1 项兼容对照通过；第二次运行前创建的 9 个合成 vault 均按摘要核对未变，另生成 5 个全新随机资料目录。两份运行日志未发现符合 44 字符 Base64 凭证格式的内容；该有限扫描不是任意输出或托管字符串清理的证明。合成信任文件使用 CreateNew，不覆盖已有文件。首次编译缺少 WPF 框架引用、随后缺少 `System.IO`，已补齐，失败日志保留；未设置隔离构建选项时门禁按预期拒绝。新增设置索引反例后本机与独立复验各为 5 项预期失败、1 项兼容对照通过；两份日志的同类有限扫描未发现 44 字符 Base64 凭证形态。以上不代表该问题已修复。

```powershell
dotnet test tests/experiments/LegacyDuplicateVault/LegacyDuplicateVault.Tests.csproj -p:UseArtifactsOutput=true -p:ArtifactsPath="$(Join-Path (Get-Location) 'tests/.work/legacy-duplicate-vault-build')" --verbosity minimal
```

运行前须为进程显式设置 `TEMP`、`TMP`、`DOTNET_CLI_HOME` 为独立测试目录，并设置 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`、`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false` 和 `DOTNET_CLI_TELEMETRY_OPTOUT=1`。合成库只创建于工程 `tests/.work/legacy-duplicate-vault` 的随机子目录，检查各祖先是否为重解析点；该检查不能抵御恶意并发替换。编译要求 artifacts 字符串位于 `tests/.work/`，不能代替完整沙箱，还原可能先写入目标路径。测试失败是要保留的反例，不计入正常通过数。合成资料受当前用户保护器加密，测试不会删除任何已有资料；独立测试目录不可用于正式配对或上传。此实验不直接运行 TLS 服务、真实键鼠或真实设备，也不证明撤销在途请求的排空。
