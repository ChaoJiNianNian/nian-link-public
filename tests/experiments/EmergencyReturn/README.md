# 仅键鼠应急归还的失败回归

此独立研究工程调用当前真实 `MultiPeerPreviewManager`，但键鼠、显示器与时钟均为内存合成对象。**没有实现新的应急协议或产品功能**，不连接设备、不更改现用程序或配对资料；下列反例测试设计为当前预期失败，不属于默认回归或通过数。

```powershell
dotnet test tests/experiments/EmergencyReturn/EmergencyReturn.Experimental.Tests.csproj -p:UseArtifactsOutput=true -p:ArtifactsPath="$(Join-Path (Get-Location) 'tests/.work/emergency-return-research')" --filter FullyQualifiedName~EmergencyReturnRedTests --verbosity minimal
```

当前四项失败分别表明：直接把普通切换当应急归还会调用显示器；单独进行模拟原生键鼠切换后，焦点事件仍可间接触发显示器；已有预览在模拟键鼠已回主控后，到期读取或 Dispose 仍可把键鼠移回旧观看端。后两项在虚拟键鼠上主动尝试了一次条件切回主控，再触发真实 manager 的预览到期或清理；并不是调用了尚不存在的产品应急接口。四条反例不涉及真实键鼠、系统队列或实际显示器，不能据此断言某次用户操作已发生。

旧 ID 到期时序另经真实 manager 和合成键鼠复现：第一合成预览结束、第二预览开始后，虚拟时钟令第二预览到期，目标端用第一预览旧 ID 请求 End。旧实现先读取并处理第二预览到期，合成焦点从目标移动到主控，旧 ID 请求返回 Viewing。当前候选在读取之前先对活动会话校验请求者和规范化 ID；旧 ID 与无关请求者在此场景被拒绝且不会由该 End 触发到期动作，合法 ID 的到期归还仍保留。该回归已移入正常 `DeskLink.Tests`；当时反例是先失败后修复。后续其他合法读取和焦点事件仍可处理租约到期，无法凭此宣称旧请求或系统队列被排空；测试不涉及真实网络、键鼠或设备。

未来候选须额外覆盖配对身份、请求方与预览参与方/当前焦点、不同设备同时使用、native 条件修订、超时后迟到、显示焦点事件抑制、旧服务器不支持时不回退普通 switch、应急提示，以及已有独立显示命令在途等路径。服务器当前 `switchGate` 只与其自身 `RevokeAsync` 排序，vault 另有可直接调用的撤销路径；认证与已接收操作的原子排序不能仅凭现有锁猜测。用户点击应急归还仅表示尝试，不能解除旧请求未确认状态或证明原生队列已排空。

## 失败关闭包装器研究

仅以独立测试包装器串接真实 manager 与合成键鼠、时钟、显示和授权表：

```powershell
dotnet test tests/experiments/EmergencyReturn/EmergencyReturn.Experimental.Tests.csproj -p:UseArtifactsOutput=true -p:ArtifactsPath="$(Join-Path (Get-Location) 'tests/.work/emergency-return-state')" --filter FullyQualifiedName~EmergencyStateResearchTests --verbosity minimal
```

该组 **11 项通过**并经独立复审：经唯一测试包装器访问时，锁存未确认后不再转发新 Begin、Renew、Read、普通切换和焦点联动；同一设备可以再次尝试，但已处于主控与真正尝试都保持未确认。研究额外拒绝了名单外设备、主控伪装、未连接设备，以及其他设备的预览。上述都是合成判断：名单由测试另行注入，可能与真实 manager 的成员集合不一致；研究包装器的普通 Begin、Renew、Read 和切换未进行认证，也没有真实 vault/网络授权，不能据此声称产品授权或所有入口已保护。

研究还明确重现了两个未解决问题：合成操作获得许可后撤销，已在途的键鼠操作仍可完成；先前已提交的合成条件切换在键鼠已处于主控、应急未改变修订时仍可迟到，随后把焦点移回被控。二者均不能证明真实内核事件，但表明包装器本身不是排空屏障。包装器的已在途显示命令未测；未确认时它不调用 manager 的 Dispose，不能称资源已安全清理；合成后端若不返回，Dispose 会一直等待。测试仅在断言后对合成 manager 做清理，不代表产品可安全销毁或重启。

上述 11 项是针对实验包装器的通过结果，与最初四项真实 manager RED 相互独立，四项仍按预期失败；不能据此认定产品应急归还或断线自动恢复已完成。

## 真实 manager 中的研究专用切片

`NianFenceResearchIsolatedTest=true` 时，真实 `MultiPeerPreviewManager` 编入实验性的仅键鼠尝试；默认构建不含该方法。测试不接服务器或界面，传入的请求者**假定已在外部认证**，manager 只核对自身批准的设备、当前连接、焦点和无副作用的预览快照；这不验证实际配对凭证或撤销后的授权。

```powershell
dotnet test tests/experiments/EmergencyReturn/EmergencyReturn.Experimental.Tests.csproj -p:NianFenceResearchIsolatedTest=true -p:UseArtifactsOutput=true -p:ArtifactsPath="$(Join-Path (Get-Location) 'tests/.work/emergency-manager-candidate')" --filter FullyQualifiedName~EmergencyManagerCandidateTests --verbosity minimal
```

当前 7 项定向测试使用真实 manager 和合成键鼠、显示器、时钟；验证请求者范围、锁存未知、旧预览读取/清理与显示焦点事件不产生新动作、有限等待和重复尝试。被控后端忽略取消时，请求可能在返回“尝试但未确认”之后执行。未知时研究 manager 不调用旧预览归还，处置明确抛出未确认，重复处置仍失败，不能假称停止成功；它尚未接入真实 runtime。若已有调用占住 manager 的门超过等待时限，尝试不会发送，处置也以超时失败，之后仍要等旧调用自行结束再处理。已有在途显示动作、直接持有 manager 外部资源的旁路、其他进程及底层原生命令并未受此测试控制；重新建立 manager 会丢失内存里的显示抑制，不能凭此次处置安全重启。默认构建反射测试确认没有此研究方法；原有四项旧 manager RED 和包装器测试仍分开保留。

## 真实 TLS、信任库与 manager 的回环研究

显式 `NianFenceResearchIsolatedTest=true` 时，真实控制服务、隔离真实 vault 与真实 manager 在回环 TLS 上接线；专用工厂继承原研究工厂的测试资料路径校验，只能在库内创建合成键鼠，不能传入 native 控制器或用户设备。服务端固定目标为主控，旧 v1 或普通 v2 switch 均不回退；成功通过认证及研究尝试也以 `ok=false`、`attempted-unconfirmed` 或 `already-host-unconfirmed` 表示，不能当作真实归还成功。研究服务不向客户端发送 token、指纹或单次内存租约。

```powershell
dotnet test tests/MultiPeerTrust.Tests/MultiPeerTrust.Tests.csproj -p:NianFenceResearchIsolatedTest=true -p:UseArtifactsOutput=true -p:ArtifactsPath="$(Join-Path (Get-Location) 'tests/.work/emergency-tls-vault')" --filter FullyQualifiedName~Emergency --verbosity minimal
```

当前 6 项定向测试覆盖真实 vault 的撤销先于登记、登记后撤销先于二次入闸、入闸后撤销、相同名字重新配对、目标与凭证控制，以及主控同名的大小写伪装；研究构建信任套件共 187 项通过，默认信任套件 165 项通过且反射确认默认构建没有研究工厂。应急 API 尚缺时测试首先编译失败，不是运行中的功能反例；大小写伪装额外先以运行断言复现、再修复。

两次验证与一次性租约登记各在 vault 锁内短同步完成，没有锁内 I/O；第二次验证若先于撤销，测试有意让**撤销后才发送合成动作**，结果仍是未确认，证明撤销不能消除在途操作。`vault.RevokeAsync` 的布尔成功只表示凭证记录已删除，不能表示旧动作排空。研究服务关闭监听不证明 manager 或底层键鼠已安全停止，也不证明在途显示动作不会完成；旧协议、其他进程、备份回滚与本地 native 认证未解决。Windows 测试的临时系统密钥清理和测试输出路径的通用重解析点防护仍未证实；网络反序列化得到的托管 token 字符串无法保证清零。没有接产品运行时、真实设备或自动恢复。

## 应急研究通道凭证编码一致性（2026-11-15）

使用私有源码提交 `96490aae49c3d941ecbe9f43d5fb9683479e8607` 的隔离归档和合成凭证，在真实回环 TLS、真实测试信任库与合成键鼠研究服务中复现：32 字节凭证的非规范 Base64 字符串可以与规范字符串解码为相同字节，研究应急入口原先会接受该等值编码并尝试一次合成键鼠切换。先前失败断言为期望尝试 0 次、实际 1 次；这不是无凭证伪造或授权绕过的证明。

仅 `NIAN_FENCE_RESEARCH` 编译的应急入口现核对解码长度为 32 且与规范 Base64 完全一致，再执行原有的信任和授权检查；已解码的字节数组在既有 finally 中清零。`Convert.ToBase64String` 会创建托管字符串，不能声称所有副本清零。相同回环测试修复后通过，研究全量 188 项、默认全量 165 项通过；独立复审确认默认编译未接入该研究入口。

此项只解决研究接口中的编码规范性，不阻止有效凭证的拥有者请求操作，不解决已入闸后撤销、网络超时后的迟到动作或原生键鼠队列排空；不代表可安全接入产品、真实设备或自动恢复。原始测试记录保留隔离目录，不上传合成凭据与本机路径。

## 认证观察与绑定修订的应急尝试（仅研究编译）

`emergency-probe` 与 `emergency-bound` 只在 `NIAN_FENCE_RESEARCH` 下的本机回环研究服务可用，必须通过隔离真实 vault 的唯一凭证校验；没有接入默认客户端、真实局域网、真实设备或产品界面。探测在真实 manager 串行门内读取合成键鼠修订，返回该修订和研究服务对象随机代次；绑定尝试再次授权，在同一 manager 门内重新读取并比较修订，匹配时才按既有研究逻辑尝试固定切回主控，响应仍标记未确认。探测成功不证明系统键鼠已经安全归还。

通过真实回环 TLS 与合成键鼠验证：探测后合成焦点从目标切出再切回、以及二次授权后入 manager 之前发生变化，旧修订均不执行切换；正确修订尝试一次但仍未确认。另检查缺失/错误/不同服务对象代次、无效凭证、授权登记后先撤销，以及入闸后撤销仍可能发送动作；所有响应检查版本和请求编号关联。独立审查发现最初探测未核对活动预览参与者或未确认尝试的归属，两个合成配对 peer 的回环测试先失败、研究实现补同门无副作用快照与归属检查后通过；成功与拒绝响应均检查不回显凭证。原研究 `emergency` 入口仍然存在且不检查修订，测试明确展示它可绕过这个限制，所以这个切片不是完整协议，也不能作为默认产品的安全屏障。

修订和代次只是进程内可重复使用的观测，不是单次票据或身份凭据，亦不绑定持久配对代次或未变更修订的已发动作；RequestId 只关联响应，不做去重。合成测试明确演示：同名设备撤销重新配对后，旧凭证被拒，但新凭证仍能使用旧观测发起尝试，因此不能把探测结果说成已经绑定配对代次。旧 Begin 若已在原生队列里、其他进程/旧协议绕过、服务或系统重启、备份回滚和显示器既有在途动作，均未被这个研究证明安全；请求超时或取消也不等于动作被撤回。研究端点不对真实设备开放，不能用于安装包。新增功能的第一条测试最初因服务没有该探测接口而运行失败，不能把它称作旧修订被接受的运行反例；旧研究入口可绕过的受控测试单独保留。

## 无关设备读取与租约到期（拟议安全目标，预期失败）

`ReadParticipationTests` 使用当前未修改的真实 manager、虚拟时钟和内存键鼠复现：host→one 的预览已到期，非参与方 two 调用 `ReadForDeviceAsync`，响应为空但焦点已从 one 切回 host。以“非参与方读取不得触发本次键鼠动作”为新目标的测试按预期失败；它只描述该合成时序，不证明真实用户发生了故障，也不说明租约到期本身不应归还。参与方 host/one 各自读取已到期会按原策略归还；未过期的无关读为空且不切换；在 Begin 占用 manager 门时取消无关读不会发送新动作。这些正控共 4 项通过。

另一正控构造真实 `MultiPeerRuntime`、隔离信任库、仅回环监听和合成键鼠，不调用任何 peer read 或手动 End，等待真实八秒租约后观察 runtime 定时器归还；单次测试通过。每秒 watcher 与读取共用 manager 门，门挂起、watcher 异常退出、进程停止或独立使用 manager 时并不能保证定时归还；因此不能仅凭该正控断言提前拒绝非参与方读取不会改变所有失败场景的安全性。

调用方必须为此实验显式设置 `UseArtifactsOutput=true`、将 `ArtifactsPath` 指向本工程 `tests/.work` 下的独立目录，且设置 `DOTNET_GENERATE_ASPNET_CERTIFICATE=false`、`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false` 与独立 `DOTNET_CLI_HOME`；不得指向正式安装或真实用户资料。实验项目本身仍没有对普通编译与 restore 路径强制隔离检查。runtime 正控现依据编译时工作目录元数据，在创建 vault 或测试密钥前检查 `AppContext.BaseDirectory` 必须是该 `tests/.work` 的严格子目录，并拒绝路径上的现存重解析点。受控外置输出先复现旧版仍会创建合成数据，再用新 guard 在独立外置路径及 junction 路径各验证提前拒绝，确认未创建 watcher 数据目录；合法独立目录的五项正控仍通过。最初用于复现的合成输出已移动到 `.work` 内保存，junction 已移除。此检查不能约束测试执行前的 restore/build 写入，也不防恶意并发更换路径或伪造编译元数据，不是完整沙箱。

`SyntheticProtector` 将**合成**配对 token 原样保存在测试 vault 文件，runtime 也会在该目录生成合成 TLS PEM 私钥；Windows 加载 TLS 还使用系统管理的临时用户密钥，未独立核验其清理。测试不使用正式配对资料或实体键鼠，`.work` 是本机测试数据，**不得上传或纳入发布包**。此处仅保留现象和拟议目标，产品行为尚未修改。
