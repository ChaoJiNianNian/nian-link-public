# 预览所有者闸门实验

该工程是内存中的隔离模型，**未接入 Windows、Mac 或真实预览会话**，也不读取用户资料。它探索一个共享所有者如何在发起动作之前原子占位，在 Begin 或归还结果不确定时永久阻止新的操作。应急归还仍可调用，但它本身会使状态保持未确认；包括并发中的旧响应返回也不能清除该状态。

实验后端通过内部接口注入，只由测试内假实现产生“无交接”或“归还已确认”的证据。受信后端是本实验的**前提假设**：不能用普通状态查询、超时、快捷键或现有 v1 接口返回值冒充这种证据。该工程不提供供一般调用方手动设置已归还的入口。真实可信后端、证据来源和请求绑定尚未实现；程序集内部代码若能构造假后端也可以伪造实验结论，因此本模型不是安全边界。

状态只存在于单个对象内；关闭进程、重新创建对象或启动另一个配置不会保留状态，也不能约束其他设备。没有文件写前记录、跨进程锁、服务器封存、旧版本旁路防护或 Deskflow 原生动作排空证明。已有产品预览归还误判未因此修复，断线自动恢复也未实现。

在工程根目录运行：

```powershell
dotnet test tests/experiments/PreviewOwnerGate/PreviewOwnerGate.Tests.csproj -p:UseArtifactsOutput=true -p:ArtifactsPath="$(Join-Path (Get-Location) 'tests/.work/preview-owner-gate')" --verbosity minimal
```
