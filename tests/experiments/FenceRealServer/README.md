# 真实服务器的回环封存骨架

此工程切片只用于研究和自动测试：使用真实 `MultiPeerControlServer`、临时本机 TLS 与受保护的合成测试 vault。研究 factory 仅在 `NianFenceResearchIsolatedTest=true` 编译时存在，仅监听 `127.0.0.1`，没有键鼠回调、未接入 `MultiPeerRuntime`，Begin 固定返回不可用。正常编译无此入口，不改变现用正式程序；产品 v1、本地主控及其他客户端控制路径并未由它封闭。

运行测试（项目根目录）：

```powershell
dotnet test tests/MultiPeerTrust.Tests/MultiPeerTrust.Tests.csproj -p:NianFenceResearchIsolatedTest=true -p:UseArtifactsOutput=true -p:ArtifactsPath="$(Join-Path (Get-Location) 'tests/.work/fence-server-isolated')" --filter FullyQualifiedName~FenceServerSkeletonTests --verbosity minimal
```

真实服务器使用测试专用 vault 快照接口，在同一 vault 锁内验证唯一凭据、获取规范身份并登记内存墓碑；回调不重入 vault、不执行 I/O。该身份结合 token 的领域分离 HMAC 仅用于研究对象内的匹配，不发送到网络。覆盖拒绝 v1、旧版重复 token、错误 token 和目标、过期 incarnation、授权撤销/重新配对、同编号跨主体、容量、回环监听、隔离 vault 路径、错误证书和默认构建未启用研究协议；检查响应和诊断字符串不含合成凭证或派生标识，研究对象关闭后清理受控 marker 并拒绝重用。真实 TLS Seal 与撤销竞争经过受控 vault 锁测试，但尚不证明所有配对管理入口的跨进程事务。封存切片当时 **17/17**；此后研究服务器加入 6 项应急 TLS/vault/manager 测试，合计 **23/23**，其中动作使用合成键鼠和显示器，未接入产品，详见[应急归还研究](../EmergencyReturn/README.md)。授权标识独立研究 **10/10**，默认 MultiPeerTrust **165/165**。

真实 TLS 的一条测试先发送 Begin JSON，但扣住终止换行，Seal 在另一条连接完成，再补发换行，Begin 被拒；这覆盖尚未组成完整帧的重排，不是已完整帧在服务端分发前排队的验证。研究封存值只保存在本进程内存，不是持久化授权代次、底层键鼠归还或旧进程停止证明，也不代表产品端自动恢复或真实双机通过。

服务端不生成应用身份文件；Windows 的 Schannel 不支持 `EphemeralKeySet`，临时证书使用系统用户密钥，释放后系统是否清理及进程崩溃残留尚未核验。vault 的路径在工厂中以相对路径与链接检查限制，MSBuild 对编译输出路径仅做字符串检查，不保证挡住重解析点或竞态。本次测试前人工核验输出目录与上层为普通目录；不要将该检查当作完整安全隔离机制。

请求从 TLS JSON 反序列化时会生成托管字符串，包括合成 token；这些不可由代码可靠地原地清零。研究仅清理自身能够控制的凭证字节数组、派生数组及封存记录，不宣称运行时、网络库或所有字符串副本均已被清除；测试报告和响应不得打印这些值。
