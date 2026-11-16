# 预览封存模型实验

这是独立的内存合成模型及测试，不是产品实现，不连接真实设备，不包含持久化或断线自动重连。历史隔离测试在 Windows 使用本机 .NET SDK 与 NuGet 缓存运行，30 项通过；不能据此推断真实会话、键鼠或安全归还已经验收。

在独立源码副本、已安装 .NET 10 SDK 的环境中，从项目根目录执行：

```powershell
dotnet test tests/experiments/PreviewFenceModel/PreviewFenceModel.Tests.csproj -c Release
```

测试和构建只使用合成资料；不要指向真实配对目录。其他研究项目、跨进程授权及原生事件队列需分别检查。
