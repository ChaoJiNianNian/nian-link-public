# Windows 归还入口：隔离研究

## 普通归还回复晚于键鼠状态断开

在默认候选中，`red-late-switch-disconnect` 和 `red-late-switch-reconnect` 分别使用真实 WPF 主窗口归还按钮、真实 `WindowsPeerLink`、真实回环 TLS 控制客户端，以及独立合成身份、合成键鼠进程与合成 IPC。控制请求发出后，前者关闭键鼠状态 IPC，后者在同一 IPC 上依次发送断开和重新连接；确认状态变化后才让控制通道返回匹配的合成成功回复。修复前两项各返回 1，界面显示“键鼠已归还主控”；修复后均返回 0，真实 guard 锁存未确认且界面不显示归还成功。

修复使用同一个键鼠进程与生命周期引用、IPC 核心代次、连接状态变化代次及指纹证据，在请求前捕获、回复后复查；不相关 IPC 日志和重复 Connected 不更改连接代次。正常连接、上述不相关消息、服务器拒绝及进程状态读取异常四种对照通过；拒绝和异常仍锁存未确认。另在 `PeerControlOwner.Tests` 验证普通归还被取消后迟到结果不能清除未确认，原有应急尝试仍存在但不认定安全成功。定向窗口 6 项和 guard 全集 26 项、DeskLink 全集 489 项通过。

本验证不操纵物理键鼠或真实配对；TLS 证书、令牌与目录为合成数据，系统可能为合成证书产生用户密钥，未独立核验清理。版本证据只验证调用前后客户端已观测的状态，仍存在复查之后状态变化、远端动作或原生队列迟到、认证代次及窗口同步等边界；不能视为真实键鼠已经归还、跨进程屏障、断线恢复、或 `red`/`red-button` 浮窗竞态已解决。已有安装包在本修复之前生成，不包含此源码改动。

## 默认候选：关闭被取消时保留窗口所有权

默认 WPF 候选改为只在窗口真正触发 `Closed` 后释放 `targetReturn`。当关闭被取消时保留原窗口和原会话的 End 尝试，不再为新状态创建第二个归还窗口；隐藏后取消关闭则在后续观察到同一 ID 时尝试重新显示。关闭过程中阻止同 ID 或不同 ID 的同步重入；如果浮窗按钮在合成 End 成功后关闭被取消，按钮不再一直显示“正在归还”，而是提示归还未确认。状态刷新本身失败、关闭又被取消时，保留窗口并显示两个错误，避免二次关闭异常逃出异步计时器处理。

真实 WPF 窗口配合合成回调的取消、隐藏取消、两种重入、按钮关闭取消五项先失败后通过；状态刷新失败与关闭取消组合在添加错误处理前后分别失败、通过。正常关闭也通过。默认七项新场景及原七项默认回归通过；原 `red`、`red-button`、`red-stale`、`red-stop`、`red-no-id` 五项仍按各自的预期失败，不能把这些未解决的竞态或拟议安全目标当作已修复。首次测试构建因新增文件缺少 `System.IO` 未编译，补充导入后构建零警告、零错误。

`close-ownership-show-error` 通过反射将真实 WPF 浮窗设为主窗当前引用，在该浮窗的 `SourceInitialized` 人为注入异常，模拟主窗已有窗口引用时 Show 未完成；移除注入后，同 ID 更新能重新显示原窗，并可尝试原合成 End，窗口仍唯一。该单次结果不能推广所有系统显示错误或真实用户事故，也不验证 Mac 原生行为。

另人工注入 `Closing` 事件处理器抛错时，WPF 使窗口仍处于关闭状态，后续 Close/Show 也会报错，测试进程在退出时发生未处理异常（退出码 -532462766）；该观察保留在执行记录与隔离结果文件，不能宣称程序可从这种框架状态恢复。该破坏性夹具模式未保留在正常测试入口。测试只使用本机独立窗口、合成资源和合成 vault，没有真实连接或物理键鼠；取消/抛错由测试注入，不代表已观察到真实用户发生。隐藏入口依赖后续同 ID 状态观察才能尝试重显；旧回调和跨设备在途操作仍不能认为安全停止。

## Recovery 状态的现有归还入口

默认候选 `recovery-target` 使用真实 WPF `MainWindow.ApplyTargetReturn`、真实 `PreviewReturnWindow` 和合成 End 回调：Active 会话转换为相同目标和 ID 的 Recovery 时，原按钮保持可见；合成 End 仍返回 Recovery 时提示未确认、可再次尝试，合成 Viewing 响应后窗口关闭。另检查 Recovery 的新 ID、host/peer 目标、其他目标、空白 ID/本机名、Viewing 以及 Preparing/Returning。原代码先以 `Recovery state removed the target return window.` 返回 1，修复后返回 0。该夹具没有真实 guard、网络或物理键鼠；它只修正 Recovery 入口筛选，不能保证当前焦点或远端请求已经停止，已有并发关闭反例仍失败。

本夹具不是安装包，也不是正式功能。`red` 使用真实 WPF 主窗口的预览归还处理、真实 `PreviewReturnWindow` 和真实 `GuardedPeerControl`，在浮窗 `Closing` 时从后台发起合成 Begin；当前默认候选会丢失原浮窗，测试预期失败。旧版 RED 原始结果亦保存在 `tests/.work/wpf-ui-race-retained/result.txt`。夹具将已有处理片段提取为 `ApplyTargetReturn` 以便受控调用，并不模拟真实两机、键鼠或原生事件队列。

显式设置 `NianPeerEmergencyResearch=true` 时，独立研究实现尝试使用常驻、折叠式 WPF 入口。测试覆盖：上述窗口关闭期间合成 Begin；应急点击经真实内存 guard 尝试并保持未确认；折叠、展开的工作区边界、正常关闭被拦截、显式释放；成功后仍可用及通过可变合成回调模拟绑定变化；以及一个合成信号量阻塞并超时后提示未确认、允许再次操作。研究结果不证明真实连接重绑、窗口创建失败、启动/停止重入、真实屏幕遮挡体验或物理归还成功。

**已知重大限制：** 被控端尚无未知会话 ID 时可用的键鼠-only 应急协议。研究按钮调用现有普通 `switch`，它可能同时切换显示器。客户端超时或取消不撤销服务端已接收请求，旧请求可能晚到；不能解除未知状态。Start/Stop 并发仍存在生命周期竞争，快捷键归还后的保守锁定尚未解决。关闭主窗口后无法保证仍有应急入口。本研究不是安全栅栏，不可据此开启自动恢复或交付用户测试。

默认编译不包含研究窗口及其接线；研究项目的构建目标要求 `UseArtifactsOutput=true` 且 `ArtifactsPath` 字符串位于 `tests/.work/` 下。该检查仅阻止不符合条件的项目编译：NuGet 还原可能在检查前向指定目录写入中间文件；字符串检查也不防止符号链接或其他重解析点。调用方须先自行核查输出与还原路径，不要使用真实用户资料或正式版路径，不生成安装包。

结果路径必须是本工程 `tests/.work` 下的全新文件，父目录须已存在；程序在显示窗口前拒绝已有文件和包含链接的路径。重复运行请使用新的结果文件名或新的独立目录，无法覆盖旧结果。该路径检查不防止恶意并发替换，不能当作完整安全沙箱。

使用 .NET 10 SDK，在项目根目录运行以下命令（输出目录须保持独立）。显式设置 DisplayBridge 来源为同次 artifacts 输出，避免复制默认位置中的旧文件：

```powershell
dotnet build tests/PeerControlOwner.UiRaceFixture/PeerControlOwner.UiRaceFixture.csproj -p:UseArtifactsOutput=true -p:ArtifactsPath="$(Join-Path (Get-Location) 'tests/.work/wpf-emergency-default-final')" -p:NianDisplayBridgeOutputDirectory="$(Join-Path (Get-Location) 'tests/.work/wpf-emergency-default-final/bin/DesktopToolbox.DisplayBridge/debug')"
dotnet tests/.work/wpf-emergency-default-final/bin/PeerControlOwner.UiRaceFixture/debug/PeerControlOwner.UiRaceFixture.dll "$(Join-Path (Get-Location) 'tests/.work/wpf-emergency-default-final/red.txt')" red
dotnet tests/.work/wpf-emergency-default-final/bin/PeerControlOwner.UiRaceFixture/debug/PeerControlOwner.UiRaceFixture.dll "$(Join-Path (Get-Location) 'tests/.work/wpf-emergency-default-final/default.txt')" default
dotnet build tests/PeerControlOwner.UiRaceFixture/PeerControlOwner.UiRaceFixture.csproj -p:NianPeerEmergencyResearch=true -p:UseArtifactsOutput=true -p:ArtifactsPath="$(Join-Path (Get-Location) 'tests/.work/wpf-emergency-research-final')" -p:NianDisplayBridgeOutputDirectory="$(Join-Path (Get-Location) 'tests/.work/wpf-emergency-research-final/bin/DesktopToolbox.DisplayBridge/debug')"
dotnet tests/.work/wpf-emergency-research-final/bin/PeerControlOwner.UiRaceFixture/debug/PeerControlOwner.UiRaceFixture.dll "$(Join-Path (Get-Location) 'tests/.work/wpf-emergency-research-final/race.txt')"
dotnet tests/.work/wpf-emergency-research-final/bin/PeerControlOwner.UiRaceFixture/debug/PeerControlOwner.UiRaceFixture.dll "$(Join-Path (Get-Location) 'tests/.work/wpf-emergency-research-final/persistent.txt')" persistent
dotnet tests/.work/wpf-emergency-research-final/bin/PeerControlOwner.UiRaceFixture/debug/PeerControlOwner.UiRaceFixture.dll "$(Join-Path (Get-Location) 'tests/.work/wpf-emergency-research-final/timeout.txt')" timeout
```

## 2026-11-15：关闭与退役的补充反例

在默认编译下，`red-button` 通过真实 `PreviewReturnWindow.ReturnAsync` 模拟旧 End 返回成功，利用 WPF `Closing` 事件从后台经真实 owner guard 触发合成 Begin；与原 `red` 一样以“在途请求丢失归还窗口”退出 1。按钮成功由合成回调提供，不代表真实归还。

`red-stale`、`red-stop` 和 `red-no-id` 是**待讨论的新安全目标的预期失败**：在合成 Begin 尚未完成（`red-stop` 额外调用 BeginStopping）时，分别尝试旧 ID 的 End、停止中的旧 ID End、无 ID 时普通 switch。三个场景均捕获未确认异常，但下游合成后端各被调用一次，按“不得发送旧 ID 或普通 switch”断言返回 1。当前旧协议刻意允许未确认时尝试归还；这三项展示实现与拟议更严格目标的差异，不证明真实设备曾发生，也不能在缺少可用救援协议时简单阻断所有归还。已存在的远端发起会话和停止时有效 ID 的行为必须一起设计、验证。

在显式研究构建中，`research-retired` 手动将合成 backend 绑定到真实窗口 owner，创建真实研究入口后让 Begin 在途、调用 BeginStopping 和 Retire，再调用真实 `PresentPeerEmergencyStopped`；入口仍可见并显示停止且未确认。点击连接已停止的真实 `ReturnPeerEmergencyAsync` 时因窗口的 `peer` 为 null 显示失败提示，没有调用合成 End 或 switch；该测试没有建立真实 `WindowsPeerLink`，不能等同于执行真实 Stop/连接失败或重连。`research-clean-stop` 检查合成 owner 无未决操作退役后研究入口释放。两项和既有 `race`、`persistent`、`timeout` 各返回 0，仍是研究宏，不影响默认产品；现有研究入口连接时仍使用可能联动显示器的普通 switch，不能称作键鼠专用应急功能。

此次隔离构建的 SDK 首次启动曾输出已安装 ASP.NET 开发证书的通用提示；事后只读检查当前用户证书存储中的两张同名证书有效期均早于本次，但此前没有证书存储快照，不能断言没有发生其他存储变化。没有删除或信任证书；后续每个 dotnet 进程明确设置 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`、`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false` 与 `DOTNET_CLI_TELEMETRY_OPTOUT=1`。

## 停止时等待已在途的预览归还

`peer-stop-pending-success` 和 `peer-stop-pending-failure` 使用真实 WPF 主窗口、真实 `DeskflowPreviewSession` 和受控内存后端：在第一项 End 已发送但未返回、`CanStop` 已为 false 时调用 `StopPeerPreviewAsync`。旧实现因跳过等待而关闭归还浮窗，两项均先返回 1；修复后等待同一清理任务，End 成功或失败返回后才关闭，两项均返回 0；失败后的会话状态仍为未确认，但该浮窗此后仍会关闭。`peer-stop-pending-timeout` 不完成合成 End，验证现有 3 秒等待上限后恢复调用、状态仍未确认，而合成后台任务本身并未被取消。

`peer-chain-pending-success`、`peer-chain-pending-failure` 从真实 `StopPeerInsideAsync` 调用同一路径，验证等待和状态，但夹具中 `peer` 为 null，**没有**验证真实连接停机、Retire 或真实键鼠。以上只消除已有清理进行中被 `CanStop` 跳过的情况；其他回调、开始操作和窗口关闭仍可能阻塞，不能保证整体停止时间有上限。失败后旧流程仍可继续关闭浮窗或断连；主控远端新 Begin、两条浮窗关闭竞态以及键鼠专用应急均未解决。旧 `red`、`red-button`、`red-stale`、`red-stop` 和 `red-no-id` 仍按前文预期失败。

在该文档上述隔离编译完成后，分别运行 `peer-stop-pending-success`、`peer-stop-pending-failure`、`peer-stop-pending-timeout`、`peer-chain-pending-success`、`peer-chain-pending-failure` 作为最后一个参数，并为每次运行指定新的绝对结果文件路径。每项成功返回 0。测试不启动真实核心或操作实际设备。
