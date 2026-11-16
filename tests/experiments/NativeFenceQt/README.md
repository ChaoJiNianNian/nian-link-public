# 原生 Qt 控制与事件队列隔离研究

这是一组测试夹具和源码补丁，研究完整 Begin 已经进入 Qt 控制解析后、进入真实 Deskflow `EventQueue` 前被延迟，Seal 能否先封存并拒绝迟到的 Begin。它没有接入年年互联产品，不启动 Deskflow 核心，也不安装键鼠钩子或操作真实设备。协议 v2 在这里指**本地 Qt 管道测试命令**，不是产品网络协议。

## 运行与来源

- 需要 MSVC、Windows SDK、Qt、CMake/Ninja 以及两份隔离 Deskflow 候选（`Candidate` 的 `source` 含 0005、0006 与 0007，`CoreCandidate` 的 `build-02` 含预编译静态库）。这些依赖和来源需要另行审核，不自动下载。
- 在仓库根目录的 PowerShell 中设置 `tests/.work` 下的两个候选目录与工具链缓存路径，并显式传参：

```powershell
$work = Join-Path (Get-Location) 'tests/.work'
$candidate = Join-Path $work '<qt-source-candidate>'
$coreCandidate = Join-Path $work '<deskflow-built-candidate>'
$cacheRoot = '<native-toolchain-cache>'
& 'tests/experiments/NativeFenceQt/run-tests.ps1' -Candidate $candidate -CoreCandidate $coreCandidate -CacheRoot $cacheRoot
```

- 使用固定上游基线和 0001–0004 补丁，在隔离目录先应用 `0005-preview-fence-state-experimental.patch`，再按顺序使用 `git apply --check`、`git apply` 应用本目录 0006 和 0007 补丁；历史研究曾在独立候选中验证两种 fixture。0007 仅在测试宏下输出半帧观察信号，不解析或打印帧内容。Git 换行转换可能改变文件字节哈希。
- 两个 fixture 只创建自己的 QCoreApplication 和以各自 PID 结尾的本地端点，测试后退出或由脚本结束自己启动的 helper；PID 端点可预测，同一用户可连入、伪造请求或关闭研究 fixture。运行前检查研究目录及其祖先，并递归拒绝源码和静态库树里的重解析点；检查与使用之间仍可能发生并发替换，不构成对恶意环境的完整隔离。

## 证据

先在未打补丁的隔离基线运行，v1 切换拒绝断言失败。应用 0006 后，Qt 解析器接收完整 Begin 帧，测试夹具将其暂存；另一个连接发出 Seal，真实事件队列执行封存，然后夹具把 Begin 放回队列，Begin 被拒绝、合成开始次数为零。将状态机中的封存判断移除后，上述断言失败。完整帧暂存是受控测试安排，不是网络 TLS 拆帧或实际网络延迟的证明。

新增的半帧测试用另一条连接：先发送完整 JSON 但不发送帧结束换行，等 0007 的信号确认真实 Qt `readClient` 已缓存该数据；再在第二条连接完成合成 Seal，随后补发换行。迟到的 Begin 返回拒绝，合成开始计数保持零；随后对该已封存操作发出的合成身份不一致或代次不一致的 Seal 返回拒绝（没有测试这两种情况的 Begin）。Seal、迟到的 Begin 及后续 Seal 的响应还核对版本、请求编号与成功标志。隔离负控故意去掉首次 Seal 的墓碑记录，同一测试观察到 Begin 返回 active、合成开始计数变为 1；另外分别篡改迟到 Begin 的响应版本、请求编号及成功标志，三种负控均被响应检查拒绝。无 0007 观察信号的基线会超时，这只表明测试缺少可确定同步点，不是生产故障。此测试不覆盖任意分片、所有半帧积压或资源耗尽方式。

另测合成 Begin 成功后 Seal、重复 Seal 不重复执行合成归还、旧 Begin 重放拒绝；非法字段、合成身份/目标/代次不匹配、断连回调和墓碑容量失败关闭。用故意不调用完成回调的合成请求占住真实 Qt 单连接的 8 个 pending，验证重复 requestId 及第 9 个请求被拒绝。

在测试宏开启时仅检查研究控制入口的 v1 `switch`、`switch-if-current` 被拒绝，`state` 保留；宏关闭时定向验证 v1 条件切换及 v2 拒绝，不能外推其他生产入口或旧进程。两套 fixture 通过时脚本各输出一条 PASS。独立复审发现随机 ID 与故意挂起前缀重合会导致测试超时；修成确定 ID 后，独立构建与全脚本重新运行通过。pending 测试的挂起由夹具人为制造，不证明真实后端挂起或恢复行为。

## 安全边界

身份由 requestId 前缀合成，绝非认证；本地管道的同用户限制不证明请求来自可信客户端。同一用户的其他进程可以连接预测得到的夹具端点，并触发 0007 的观察信号或发送伪造请求；响应字段关联检查仅能捕获字段不符的响应，不能认证信号或抵御了解测试数据的同用户进程。没有绑定真实配对授权、主控目标或撤销，`fixture-exit` 也仅为测试使用。`PreviewFenceState` 仅内存存活，进程重启不保留封存。动作只是计数器，不能证明键鼠或显示系统已停止，不能证明先前已发到真实核心或操作系统的命令已排空。多进程、旧协议旁路、系统输入、完整产品接入、真实断线恢复均未完成。不要把本补丁或测试宏用于正式软件。
