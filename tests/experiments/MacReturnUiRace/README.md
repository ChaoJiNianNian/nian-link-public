# Mac 归还浮窗并发复现（预期失败）

默认候选新增 `recovery-target` 场景：以 `PollAsync` 使用的状态选择方法和真实窗口更新构造 Active→Recovery，并用真实 `GuardedPeerControl`、内存合成后端验证既有 End 可尝试、归还未确认时后续 Read 被阻止且重复 End 仍可尝试；同时检查新 ID、另一目标、空白 ID/本机名、Viewing 及 Preparing/Returning。原选择规则使窗口消失并返回 1，修复后返回 0。此场景通过反射分别调用选择方法和窗口更新，未运行完整 `PollAsync`；测试主窗的 client 为 null，不能证明真实 client 状态提示或真实 Mac 键鼠。原有跨设备并发反例仍未解决。

本轮相关研究回归第一次在 `research-close-allow` 的合成关闭断言失败，断言同时检查主窗可见、发现控制器释放和进程停止次数，原日志不能区分哪一项；完整失败结果、退出码及源码/程序集摘要保存在隔离输出。相同输入立即重试通过，临时增加三项诊断后连续 8 次通过，恢复原夹具后 17 项研究回归也通过。根因尚未确认，不能把首次失败当作已经修复或仅凭复跑将其抹去；该结果与真实 Mac 设备无关。

后续在隔离源码对 `Opened` 的合成 vault 加受控门，确认真实窗口初始化仍处于 busy 后尝试关闭：旧版固定等待 500 毫秒会忽略关闭，复现与历史失败相同的大类结果；该实验无法证明历史那次失败的实际原因。新 `research-close-ready` 先确认初始化进入受控门，再在限定时间后放行，旧夹具返回 1。夹具现等待真实 `Opened` 事件及 busy 结束、窗口 client 被初始化实例替换，再接入测试 owner；等待有界，超时明确失败。修后研究构建的原 43 场景和新模式共 44 项通过；默认构建 13 项通过、3 项既有竞态反例按预期失败，构建均零警告零错误。上述修复测试前提，不代表产品启动或退出问题已经修复，也不验证真实 Mac 或键鼠。

这是与默认回归分开的研究夹具，直接构造真实 `MacMultiDeviceWindow`、`MacRemotePreviewReturnWindow` 和多设备候选的 `GuardedPeerControl`。只使用合成后端、内存 owner 和不会读取或保存身份的假 vault；生命周期测试仅使用本机回环 IPC，不连接其他设备或真实键鼠。Avalonia Headless 在 Windows 运行，不能代替 macOS 原生窗口或双机测试。

测试提供两种受控时序：`poll` 模拟 `PollAsync` 已完成状态检查后调用 `UpdateRemoteReturn`；`button` 模拟浮窗归还按钮收到合成 End 成功响应后自行关闭。两者均在旧浮窗 Closing 事件中从后台通过真实 owner guard 发出合成 Begin，然后检查原浮窗是否仍在。当前两种模式各返回退出码 1，结果文件记录 `In-flight keyboard request lost the Mac return window during Closing.`；这是刻意保留的失败证据，不计入通过数。`button` 模式的成功 End 来自合成后端，不能当作真实服务成功归还或 guard 接线的证明。这些路径证明检查/成功回调与关闭不是原子操作，不能证明所有实际请求都会发生该竞争。

在工程根目录运行，输出与结果必须位于隔离目录：

```powershell
dotnet build tests/experiments/MacReturnUiRace/MacReturnUiRace.csproj -p:UseArtifactsOutput=true -p:ArtifactsPath="$(Join-Path (Get-Location) 'tests/.work/mac-return-ui-race')"
dotnet tests/.work/mac-return-ui-race/bin/MacReturnUiRace/debug/MacReturnUiRace.dll "$(Join-Path (Get-Location) 'tests/.work/mac-return-ui-race/poll-result.txt')" poll
dotnet tests/.work/mac-return-ui-race/bin/MacReturnUiRace/debug/MacReturnUiRace.dll "$(Join-Path (Get-Location) 'tests/.work/mac-return-ui-race/button-result.txt')" button
dotnet tests/.work/mac-return-ui-race/bin/MacReturnUiRace/debug/MacReturnUiRace.dll "$(Join-Path (Get-Location) 'tests/.work/mac-return-ui-race/unknown.txt')" unknown
dotnet tests/.work/mac-return-ui-race/bin/MacReturnUiRace/debug/MacReturnUiRace.dll "$(Join-Path (Get-Location) 'tests/.work/mac-return-ui-race/ordinary.txt')" ordinary
dotnet tests/.work/mac-return-ui-race/bin/MacReturnUiRace/debug/MacReturnUiRace.dll "$(Join-Path (Get-Location) 'tests/.work/mac-return-ui-race/state-failure.txt')" statefailure
dotnet tests/.work/mac-return-ui-race/bin/MacReturnUiRace/debug/MacReturnUiRace.dll "$(Join-Path (Get-Location) 'tests/.work/mac-return-ui-race/legacy.txt')" legacy
dotnet tests/.work/mac-return-ui-race/bin/MacReturnUiRace/debug/MacReturnUiRace.dll "$(Join-Path (Get-Location) 'tests/.work/mac-return-ui-race/dispose-failure.txt')" disposefailure
dotnet tests/.work/mac-return-ui-race/bin/MacReturnUiRace/debug/MacReturnUiRace.dll "$(Join-Path (Get-Location) 'tests/.work/mac-return-ui-race/dispose-pending.txt')" disposepending
dotnet tests/.work/mac-return-ui-race/bin/MacReturnUiRace/debug/MacReturnUiRace.dll "$(Join-Path (Get-Location) 'tests/.work/mac-return-ui-race/dispose-success.txt')" disposesuccess
```

四项 `unknown`、`ordinary`、`statefailure`、`legacy` 是已通过的浮窗提示定向检查：分别模拟多设备所有者未确认、普通失败、所有者状态不可用以及旧单机窗口。`unknown` 先复现旧提示错误后修复；四项不改变按钮执行的旧 End，不代表网络归还成功。相同四类行为也已加入正常 `tests/Mac.MultiDeviceVisual` Headless 回归，包括再次点击、旧单机和文字布局检查。

`disposefailure`、`disposepending` 和 `disposesuccess` 使用真实多设备窗口的 `StopAsync`、真实 `MacMultiPeerClient`、`DeskflowSession` 与 `MacCollaborationService`；进程、回环 IPC、媒体服务及身份资料为合成。它们验证首次及重复媒体清理失败均不触发合成键鼠进程停止、清理阻塞时不停止而完成后停止、正常清理后停止。三项在 Dispose 修复后通过；此集成夹具建立时修复已落地，**不能称其本身已在旧源码失败**。真实 Mac、系统权限、长时间不返回及进程退出时的行为仍需另外验证。

`startfailure` 和 `startconcurrent` 检查启动阶段协作资源清理失败时保留真实对象，重复启动、停止以及排队的并发停止不得跳过失败；`startsuccess` 与 `startcleanfailure` 检查正常启动停止和启动失败但清理成功；`startstopfailure` 检查协作清理成功而合成键鼠停止失败时明确提示、阻止重启并允许显式停止重试。`closefailure` 与 `closesuccess` 直接关闭真实 Headless 窗口，验证协作清理失败时窗口恢复且发现控制器未永久释放，以及正常关闭的键鼠停止和发现释放。`startfailure`、`startstopfailure`、`closefailure` 分别在修复前得到失败结果，修复后这些模式通过。退出期间发现控制器自身 Dispose 失败、原生 macOS 平台行为、旧请求是否仍在处理，均不由这些测试证明。

运行追加场景（每次单独指定绝对结果路径）：

```powershell
foreach ($mode in @('startfailure','startconcurrent','startsuccess','startcleanfailure','startstopfailure','closefailure','closesuccess')) {
  dotnet tests/.work/mac-return-ui-race/bin/MacReturnUiRace/debug/MacReturnUiRace.dll "$(Join-Path (Get-Location) "tests/.work/mac-return-ui-race/$mode.txt")" $mode
}
```
重建旧 `sessionId` 浮窗不能保证它能归还后发请求：新 Begin 在途时其 ID 可能尚未产生，旧连接退役或重新配对后也不得将旧 ID 和回调盲目绑定到新连接。当前普通切换可能联动显示器，不能冒充仅键鼠的应急操作。最终修复需要明确的无 ID 应急协议、连接代次和 UI 生命周期，并验证关闭、停止、重连、实际点击及失败提示；不能把此复现当作浮窗问题已经修复。

## 跨所有者陈旧读取反例

`cross-owner-red` 在默认构建运行：独立模拟主控先通过真实 `MultiPeerPreviewManager` 开始第一段合成预览，目标窗口显示实际产生的会话 ID；主控随后结束该会话。真实被控 `GuardedPeerControl` 通过合成后端委托 manager 的 `ReadForDeviceAsync`，得到 Viewing；模拟主控再直接调用 `BeginAsync("host", "peer")` 开始新的预览，合成键鼠在内存中切到 peer，被控 owner 仍没有未决操作。最后模拟将先前 Viewing 的 UI 更新交给真实 `UpdateRemoteReturn`，旧浮窗被关闭；测试按预期退出 1，错误为 `Cross-owner stale Viewing update lost the return window.`。测试不点击目标端归还按钮。

`research-cross-owner` 使用相同 manager 与顺序，在 `NianMacReturnResearch=true` 构建下先建立研究状态入口；陈旧更新到来后，该入口仍为唯一可见同标题窗口，退出 0。本场景没有点击或发送目标端归还。断言检查合成开始成功、被控 owner 未收到主控 Begin；窗口没有发出目标端 End 或普通 switch，主控的第一段会话 End 是测试中有意安排的动作。测试完成后 manager 的清理可能在内存合成键鼠上执行归还。

此测试仅在一个进程通过直接方法调用模拟两侧拥有者，不走真实网络、认证、Deskflow 或操作系统输入；`UpdateRemoteReturn` 通过反射调用，不是整个定时器或 macOS 窗口捕获的端到端测试。它证明已读取的状态在被 UI 使用时可能陈旧，以及研究入口在此合成顺序下保持可见；不证明默认产品已修复、远端动作已停止或按键一定能归还。两种构建使用各自 `tests/.work/` 目录和 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`、`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false`。使用仓库根目录下的独立 SDK 时，可按上面的构建示例分别增加 `cross-owner-red`、`research-cross-owner` 模式运行；研究编译必须加 `-p:NianMacReturnResearch=true`，结果必须与默认构建分目录保存。

## 后续接线验收

- 多设备连接必须先创建可见且可折叠的唯一归还入口，成功后才公开控制连接；窗口创建失败不得继续开放控制。一次连接内的常驻入口不受普通轮询关闭影响，按钮成功或超时也不得消失。
- 点击时按当前连接代次解析未来的键鼠专用无 ID 协议。旧连接的回调、旧预览 ID、停止或重配之前的迟到结果都不能作用于新连接；旧端不支持时明确提示，不能回退普通 `switch`。已经发送或尝试均显示未确认，不能清除旧操作的未知状态。
- 停止前先阻止新控制，安全收尾后才释放干净状态的入口。若停止未确认，保留检查本机与连接不可用提示；应用真正退出或进程崩溃后，窗口不能跨进程保留，不能据此声称保护持久化。
- Mac 实时捕获通过窗口标题 `NianPreviewReturnOverlay` 与应用 PID 只选择唯一匹配窗口（`src/DesktopToolbox.Mac/Native/RealtimeCapture.swift`）。新旧两个同标题窗口并存会使匹配失败，因此必须统一生命周期并测试只有一个匹配窗口及捕获过滤；不要为绕开该检查扩大整个应用窗口捕获范围。
- 补受控 WPF 与 Avalonia 界面用例：在关闭事件、请求发出前和返回后分别插入操作，覆盖停止/重连、旧回调、提示超时、重入和正常闲置无闪烁；模拟成功不等于键鼠安全归还。然后分别验证 Mac 原生捕获和双机操作。


## 2026-11-15：仅研究编译的常驻状态入口

最初基于私有源码提交 `96490aae49c3d941ecbe9f43d5fb9683479e8607` 的独立副本，新增 `NianMacReturnResearch=true` 才编译的研究入口。初版窗口在开始连接前准备，关闭、轮询和旧结果不销毁它；展开时显示“连接及归还未确认 / 此处不发送归还”，折叠后仍显示“未确认”。初版只提示检查键鼠，不发送旧 session ID 或普通 switch，也不执行真正的应急归还。入口预检失败不得继续启动，停止或退出的未确认情况保留提示；创建期间阻止重入创建和释放；关闭被取消时保留窗口引用。后续绑定变化见下节，不能把初版行为当成当前研究版行为。

Windows 的 Avalonia Headless 研究构建中，19 个场景通过：其中 13 个研究场景和 6 个既有生命周期场景；包括真实候选窗口与 `MacMultiPeerClient`、合成进程/回环 IPC 的启动预检、清理或键鼠停止失败、主窗口关闭失败、在途停止和迟到回复、创建重入、唯一窗口、折叠与提示。`research-poll` 通过反射调用真实 `UpdateRemoteReturn`，并非定时器 `PollAsync` 的端到端测试；在途状态由合成占位产生，不是真实网络命令；所谓代次替换是窗口/旧回调模拟，不证明真实连接重配。部分红绿及突变分别验证预检时序、过早关闭、旧 ID 发送、丢失窗口引用、创建中释放和提示内容。独立复审未发现本研究范围阻塞。后又基于私有提交 `0ef65a5aaa61e254703ff6a852e80df5f2ce8be1` 的新隔离归档叠加七项研究文件复验：研究 19 项通过，默认 5 项通过、2 项预期失败；覆盖文件测试前后与维护源码一致。首次复验因夹具参数顺序错误中止，已保留失败并在全新目录纠正；该复验仍是 Windows Headless 和合成进程，不是 Mac 原生或真实连接。

默认宏关闭的独立构建和反射检查确认研究入口未编入；四项原有提示回归和一项宏关闭反射检查通过，`poll` 和 `button` 两条原有真实窗口竞态仍按预期失败。研究构建要求独立 artifacts 字符串路径位于 `tests/.work/`，该判断不排除目录链接或并发替换，NuGet restore 也可能先于编译门禁写入输出；不应将其视为完整沙箱。

以上初版只是状态入口的隔离研究：不保证真实键鼠归还、服务端或原生队列排空，不验证 Mac 原生界面、屏幕捕获、真实设备或真实连接更代。研究按钮不作为产品功能启用；连接期的可用性、实际应急协议、退出及故障清理仍须继续设计和验收。

## 仅研究编译：动态已知会话尝试

`NIAN_MAC_RETURN_RESEARCH` 下，连接启动前保留唯一常驻窗，并按连接生命周期的内存代次保存最近观察到的非空会话 ID 和当时的控制对象。旧代次观察不更新绑定；空的 Viewing 不清除上次 ID，因为它可能已在交付前变陈旧。停止协作清理期间保留既有尝试，客户端停止完成或失败时失效；协作清理本身失败时保留绑定。窗口失效、隐藏、不是当前窗口或无 ID 时不发送。当前可见窗口点击时调用保存的 `EndPreviewAsync`，不调用普通 switch；结果、异常及超时都不称归还已确认。等待已取得的任务最多三秒，不能保证同步阻塞的后端调用、其他回调或原生队列有界，也不取消后台实际动作。

**风险：** 该 ID 仅是最近一次观察，并不能证明是当前会话。原服务端 `MultiPeerPreviewManager.EndAsync` 在校验 ID 之前读取当前会话，合成反例证明读取可能触发新会话租约到期归还。当前候选已调整为在该 `EndAsync` 路径先核对无到期副作用的会话快照、参与方及 ID，再执行原读取；此范围不涵盖其他 Read、旧版本服务端或已在途请求，也不证明系统输入队列已排空。旧版路径本已支持未确认时尝试，这里只探索保留该能力；不构成键鼠专用无 ID 应急协议或安全屏障，不应默认启用或发布为已修复功能。旧窗口或旧请求发出之前的身份检查也不能撤回已经在途的操作。

`research-dynamic` 在真实 Headless 窗口使用真实所有者 guard 和内存后端，覆盖无 ID 不发、最近已知 ID 尝试、旧代次的陈旧观察、迟到 End、旧窗释放后不得使用新窗绑定、唯一标题、文案及定位。代次由测试直接调用私有方法模拟，不是真实重配或网络。移除窗口身份检查的受控突变复现旧窗口对新绑定发送 End，恢复后通过；第一次突变因测试超时未命中断言，已保留结果并修正后再次复现。`research-stop-bound-pending` 与 `research-start-bound-failure` 使用真实候选窗口、客户端、协作服务及合成进程和媒体，分别模拟停止清理进行中及启动清理失败时保留一个通过测试注入的合成 End 尝试；不证明真实网络或进程退役后仍可归还。`research-start-repeat` 先复现重复开始会清掉仍在运行的连接绑定，再改为直接拒绝并保留绑定，合成尝试可发送；同一真实客户端和窗口在两个合成进程及回环 IPC 下顺序停止、重启并再次停止，验证新代次下无 ID 与旧窗口不发送，新窗口可尝试新 ID。这不是实际网络重配。研究窗口在 Windows Avalonia Headless 测试，尚未验证 Mac 原生窗口、捕获或真实键鼠。默认编译不含这项研究入口。

最终研究编译及既有研究、生命周期、提示和本轮新场景共 32 个模式通过；默认宏关闭构建零警告零错误，五个提示与宏缺席检查通过，`poll`、`button` 和 `cross-owner-red` 三条默认竞态仍按预期失败。每个模式的独立结果文件保存在 `tests/.work/mac-dynamic-research` 与 `tests/.work/mac-dynamic-default`；这些均为本机隔离证据，不属于发布包。

## 默认候选：归还窗口关闭所有权

真实 Avalonia Headless 窗口配合合成 End 测试默认候选：当窗口关闭被取消时，旧代码先丢弃引用并创建同标题新窗；先复现失败，再改为仅由 `Closed` 回调释放旧引用，关闭取消时保留旧窗并阻止新窗创建。窗口被隐藏后取消关闭也保留引用；如果之后观察到同一会话 ID，才尝试重新显示旧窗，不能保证立即恢复可见。关闭期间拒绝重入更新，避免再次关闭或在关闭栈内显示；已保留的旧窗口仍能发起原有会话 ID 的 End 尝试。停止路径遇到关闭被取消会报错、不会继续清理，提示检查键鼠。

七个默认模式分别覆盖取消、隐藏后取消、关闭事件抛错、隐藏后抛错、不同 ID 重入、隐藏后同 ID 重入和正常关闭，以及解除取消后重试；其中取消、隐藏后取消和两类重入先得到失败后通过。Headless 中事件抛错时窗口可能已经关闭，测试按实际结果接受“已关闭无残留”或“未关闭仍被持有”，不称这两项修复前均失败。旧回调使用合成后端，未证明真实键鼠已归还；如果一直无法再次观察到同一 ID，隐藏窗口也不保证重现。

默认提示/宏缺席检查五项通过；Mac 候选四种托管构建均零警告零错误，免文件候选 Avalonia Headless 视觉回归通过。首次误以非免文件选项运行视觉夹具，因缺少免文件专用对话框方法而失败，之后以正确选项重建通过。上述均在 Windows 隔离目录进行，并非 Mac 原生捕获或真实双机验证。已有跨设备 `poll`、`button`、`cross-owner-red` 竞态仍按预期失败；此切片不解决这些竞态、未知 ID 应急或系统输入队列排空。

## 研究入口停止后迟到 Begin（历史反例）

`research-stop-late-begin` 在研究编译中使用真实 `MacMultiDeviceWindow`、真实 `MacMultiPeerClient` 和真实 `MultiPeerPreviewManager`，但不使用真实设备：Mac 核心进程、回环 IPC、媒体和主控键鼠状态均为合成。测试先检查研究浮窗唯一可见，然后由模拟主控调用真实 manager 的 Begin，并在合成键鼠条件切换中暂停；该 Begin 不经过目标的所有者 guard。合成被控 client 停止成功且本地 owner 无未决，观察到研究窗消失且无同标题可见窗口；随后放行先前 Begin，合成主控仍报告目标在线，Begin 成功并将合成焦点切到目标。以“停止后在途操作仍需有提示入口”为目标的断言按预期返回 1，信息为 `RED: stop released the only return entry before the independently admitted synthetic Begin completed.`

测试只证明该受控模型下本机无未决不代表独立主控操作已停止；主控始终认为目标在线是有意构造的假设，不能证明真实 Deskflow 在被控进程停止后实际会完成切换，亦不是跨进程、网络、Mac 原生或实际键鼠的验收。合成测试无需打开真实键鼠服务、保存真实配对、覆盖安装。退出码和结果位于独立 `.work` 目录，运行时关闭 .NET 自动开发证书及全局工具路径变更。此历史反例在下述研究改动后成为提示保留测试，不是默认产品已解决。

## 研究编译：停止后保留提示与用户自主关闭

独立研究编译中，先复现上面的迟到 Begin 场景；研究入口在 Stop 完成或键鼠核心停止失败后仍保持唯一可见提示，显示连接停止或不可用、键鼠状态未确认，不能借该入口发送归还。此前协作清理仍在进行或清理失败时保留当前绑定及已有 End 尝试；只在开始客户端停止前失效绑定。若 Stop 期间已有发送在途，该改动不能撤回或排空它。

停止操作完成后用户可点“关闭提示”，该操作不代表已归还；关闭被取消或窗口被隐藏时保留所有权并在关闭回调返回后尝试重新显示。Show 失败时主窗提示检查键鼠并可确认退出。重连前须用户自行关闭停止提示：这是为了避免自动关闭旧提示后创建新窗失败而丢失提示，同时防止排队的旧窗点击作用于新连接。重新连接时创建新的窗口代次并在开放连接前显示；这仍不保证窗口替换或程序退出时对端没有迟到动作。

用户关闭整个程序时会看到知情退出确认，明确告知退出会让提示消失；取消保留程序，确认后仍须按原顺序成功停止资源才能退出。协作或核心清理失败、窗口持续拒绝关闭等情况下不能把失败称作安全关闭，也不保证强制退出。已知会话的 End、状态按钮、主窗退出和关闭提示均是研究范围；未实现真正无 ID 应急归还。进程崩溃、系统关闭、真实 Mac 捕获或双机键鼠均未验，默认产品不包含此研究入口。

测试使用真实 Avalonia Headless 窗口、真实候选客户端及管理器、合成进程/回环 IPC/内存键鼠。原迟到 Begin 反例 RED 后在同一模型中保留唯一提示；额外覆盖停止失败、协作失败保留已知 End、停止中拒绝关闭、用户确认/取消退出、旧窗关闭回调、隐藏取消/异常、重复关闭和重连需显式关闭。单独源码副本移除关闭动作来源身份检查后，旧窗能够关闭新窗且该测试预期失败；恢复检查后通过。首次使用 Windows tar 提取中文路径失败，已保留并在另一隔离目录用 Python 正确提取。测试仅保证所测模型，不代表真实操作系统输入排空。

## 关闭取消后的按钮提示（2026-11-16）

使用真实 Avalonia Headless 浮窗、合成 End 和多设备所有者：取消关闭、隐藏后取消、旧单机构造三项先失败后通过；默认相关 18 项通过，免文件候选该切片 5 项、研究编译相关 13 项通过。旧单机仅构造浮窗，并未覆盖旧主窗所有权；关闭异常时窗口可能已销毁、反馈不可见，隐藏窗口仅在再次观察到同 ID 后尝试显示。三项已有跨设备竞态仍失败；以上不证明真实 Mac 或物理键鼠归还。

## 旧单机主窗所有权研究（2026-11-16）

未显示或启动的真实 MainWindow 配合合成快照复现取消关闭丢失旧窗、重入重复窗口、真实关闭后引用残留及同 ID 隐藏恢复问题；profile 路径断言未创建，不接网络或键鼠。独立源码候选含 25 项通过（含五项先失败后通过），未合入；独立复审发现隐藏旧窗与会话 ID 变化可能导致无可见归还入口，成功归还后旧快照可能使浮窗重现，主窗退出及异常流程也待完成。仅属 Headless 合成验证，不代表真实 Mac 或安全归还已完成。

## 旧 Mac 服务恢复及异常提示（2026-11-16）

在项目根目录运行构建与测试，使用固定隔离 artifacts。`legacy-real-recovery` 经真实 MacLinkService 与 ViewModel、合成本机回环 TLS 后端和 Headless 浮窗，验证 Recovery 返回时显示未确认；`exception-sentinels` 验证五种合成异常内容不进入按钮和提示。合成资料只在项目 tests/.work/mac-return-recovery-fixture 下创建；已有目录、工作目录不符或重解析点会拒绝，finally 仅删除本测试三个已知合成文件和空目录，不递归；不能保证同用户并发替换或系统临时密钥容器。维护源码 12 个相关模式通过，首次离线还原缺 Avalonia.Headless，使用本机已有包缓存后非增量构建零警告零错误。异常仍允许用户重试，无法证明旧请求已停止、键鼠已归还或真实 Mac 行为；不得上传任何合成密钥/配对文件。
