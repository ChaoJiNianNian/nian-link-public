# 原生预览封存状态实验

独立 C++ 研究工程，**未接入产品、真实 TLS、原生控制接口或 Deskflow 核心**。实验只在内存管理单线程所有者、操作编号、设备及授权代次；封存先于开始时登记墓碑并拒绝迟到开始，动作失败或结果不确定时保持未确认，旧操作不能归还新所有者。已使用相邻隔离候选编译出的真实 Deskflow `EventQueue` 和 `SimpleEventQueueBuffer` 做一个预入队回归；执行动作只是内存计数，不安装键鼠钩子，也不启动或连接任何 Deskflow 核心。该夹具不覆盖跨线程提交时序或系统输入队列。

`async_queue_harness.cpp` 进一步研究**人为违反实验回调契约**的情况：在真实 `EventQueue` 预入队合成 Begin、Seal；Begin 的回调把合成动作追加到同一队列后立即返回 `true`，而没有等动作执行。事件轨迹为 Begin、Seal、迟到动作，状态机依次报告 Active、Sealed，但最后合成焦点从 Host 变为 Peer。该故意设置的反例返回 23；脚本严格要求这一返回码和 RED 标记，只有反例及其他对照均按预期运行才报告脚本成功。它展示了把“已排队”误认为“已完成”时的契约风险，**不证明真实产品存在这一误用**。

两项对照使用相同合成队列：如果排队后回调返回 `false`，实验报告 Unknown、Unknown，迟到动作仍执行，因此 Unknown 只是避免虚报，并不能取消或排空动作；如果回调同步完成合成动作后返回 `true`，则报告 Active、Sealed，合成焦点最后为 Host。测试核对事件轨迹与动作次数，不依靠延时。没有使用真实键鼠、认证或操作系统输入。

`PreviewFenceState.h/.cpp` 为实验状态机。`0005-preview-fence-state-experimental.patch` 仅新增这两个类文件，不修改 CMake、协议、产品服务或调用入口；它不是可发布的内核补丁。补丁已在独立源码副本中 `git apply --check` 和 `git apply` 成功，新增文件与实验源码 SHA-256 一致；历史验证使用独立源码候选及准备记录。源基线是从固定上游归档依序应用 0001–0004 的隔离候选，18 个归档内符号链接以同一归档内受限资源的普通副本物化，详情见基线 `source-preparation.json`。

在 PowerShell 中从项目根目录运行：

```powershell
$work = Join-Path (Get-Location) 'tests/.work'
$candidate = Join-Path $work '<isolated-candidate>'
$cacheRoot = '<native-toolchain-cache>'
& 'tests/experiments/NativePreviewFence/run-tests.ps1' -Candidate $candidate -CacheRoot $cacheRoot
```

需显式提供 `tests/.work` 下的隔离候选（含固定版本 Deskflow 源码及已构建静态库）和包含兼容 MSVC、Windows SDK、Qt 的工具链缓存；还需 CMake 与 Ninja。脚本不自动下载这些依赖，也不验证缓存包来源或许可；示例占位符必须替换，不能直接运行。运行前检查研究目录及其祖先，并递归拒绝源码和静态库树里的重解析点；检查与使用之间仍可能发生并发替换。脚本每次生成新的 `tests/.work/native-preview-fence-test-*` 构建输出，仅运行实验自身的三个可执行程序，异步夹具分别运行三种模式，每次有 15 秒超时；可通过参数更改候选和缓存位置。当前 Windows 环境状态机 **11/11 通过**，原真实队列的合成夹具通过，异步反例和两项对照结果符合预期。起始占位实现先有 8 项测试失败、1 项通过，随后实现并补充其余测试。最初使用 g++ 时因工具链安装路径包含非 ASCII 字符而链接失败，改用 D 盘 MSVC；最初队列夹具用 `waitForReady()` 出现等待通知的时序卡住，只终止该夹具进程并改为预入队，不作为最终通过数。

## 必须保留的边界

- 线程归属在构造时固定，跨线程直接访问拒绝；调用者必须在实际队列线程构造和调用，实验没有提供消息调度器。
- 主体、授权代次、目标和 incarnation 均由测试直接提供，**不是认证证明**；真实配对库、可信授权映射、目标规范化和重配撤销尚未接入。
- 回调 `true` 被实验假定为动作已经完全、安全地结束；实际 Deskflow IPC 回复、仅凭 `Viewing` 状态或等待固定时间都不能自动冒充此证明。未知操作故意不能靠重试或应急动作解锁。
- 封存和操作结果只在当前对象内存中，容量耗尽后失败关闭，不会自动清理墓碑；对象/进程重建不能证明旧进程与系统事件已经静止，也没有持久化恢复。
- 真实服务端 TLS 还要先封存请求，防止较早的 Begin 仍滞留在网络层；旧协议/普通切换旁路、native dispatcher、实际焦点及 OS 事件、跨设备运行和真实自动恢复均未实现或验证。
