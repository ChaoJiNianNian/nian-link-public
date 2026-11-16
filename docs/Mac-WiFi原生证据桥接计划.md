# Mac Wi‑Fi 原生证据桥接计划

## 目标与边界

为后续独立 Mac 多设备候选提供可调用的只读系统证据桥；本增量不连接产品、发现 socket、授权或钥匙串，不读取或输出网络名、接入点标识、硬件地址，不执行网络扫描。CoreWLAN 与 SystemConfiguration 各自报告的 Wi‑Fi 接口 BSD 名须非空、合法、唯一、数量有界且集合完全一致；该结果只是系统分类的瞬时观察，不是物理设备证明或发现授权。

采用独立 Objective‑C/C 动态库和固定 C ABI，而非依赖 Swift 6.1 的实验性 `@_cdecl`。Swift 6.3 的正式 `@c` 当前在实测 Mac 编译器上不可用；既有 Swift 命令行探针只是系统调用的前期取证，不能直接作原生桥。文件先保存在本工程，正常 Windows/Mac 产品及正式版均不引用。

## 数据契约与实现

公开函数 `int32_t nian_wifi_evidence_v1(uint8_t *out, int32_t capacity, int32_t *written)` 使用调用方提供的固定缓冲；桥同步完成调用，不返回跨 ABI 对象、不在返回后保留指针或向调用方转移分配。成功返回 0、写入版本 1 且只包含排序后接口名的 UTF‑8 JSON，`written` 是字节数，不包含 NUL；失败返回非零、`written` 为 0，并将有效范围内输出清零。参数非法或缓冲长度超上限时不能解引用无效输出；调用方须提供有效且不重叠的缓冲区和长度指针。

采集由 CoreWLAN 的共享 `CWWiFiClient` 获取 `interfaces` 和每个 `interfaceName`，再通过 `SCNetworkInterfaceCopyAll` / `SCNetworkInterfaceGetBSDName` / `kSCNetworkInterfaceTypeIEEE80211` 获取另一份名单；有界复制，并在同一次同步调用中以纯 C 规则函数核对、排序和编码。`Copy` 所有权和 Objective‑C 临时对象按 ARC/自动释放作用域处理；异常或任一来源失败按未知返回，不输出部分结果。输出窗口不包含地址或网卡其他属性。

桥只提供证据，不构建实时批准句柄。调用者未来须在非 UI 线程维持完整缓冲与调用生命周期，不能把超时当作原生调用已停止；两来源顺序采样不是原子快照，返回后的网络状态也可能变化。桥本身无法证明设备物理性、驱动真实性、最终 App 签名/沙盒、本地网络权限或跨设备收发。

## 验收

先写可在普通 C 编译器执行的合成行为测试，覆盖双来源一致、排序、空/缺失/重复/不一致、非法名称和长度、个数上限、缓冲不足、输出清零、指针与容量边界；测试不会调用真实系统接口。另用项目静态检查确认桥未被正式产品引用、不包含不允许的系统接口、导出仅一项，源码候选纳入文件。先确认失败再实现，Windows 可用编译器先做独立语法或行为测试；Mac 原生 `clang -dynamiclib -fobjc-arc` 编译、C 调用/符号清单和用户设备实测由主任务在经核验的独立目录进行。只有退出码和完整 JSON 均正确才可报告一次只读观察；签名和候选包接入另行规划。

### 隔离目录中的 Mac 编译和验证

把 `src/NianLink.MacDiscoveryNative` 内四个文件以及 `tests/MacWifiBridge` 内三个测试源复制到单独的临时测试目录；在该目录执行，绝不向既有正式版目录写入。以下命令仅使用系统编译器和系统框架：

```sh
xcrun clang -std=c11 -Wall -Wextra -Werror -fvisibility=hidden -c WifiEvidenceCore.c -o WifiEvidenceCore.o
xcrun clang -std=c11 -Wall -Wextra -Werror WifiEvidenceCoreTests.c WifiEvidenceCore.o -o WifiEvidenceCoreTests
./WifiEvidenceCoreTests
xcrun clang -std=c11 -Wall -Wextra -Werror -fobjc-arc -fvisibility=hidden -c WifiEvidenceBridgeNameTests.m -o WifiEvidenceBridgeNameTests.o
xcrun clang WifiEvidenceBridgeNameTests.o WifiEvidenceCore.o -framework Foundation -framework CoreWLAN -framework SystemConfiguration -o WifiEvidenceBridgeNameTests
./WifiEvidenceBridgeNameTests
xcrun clang -std=c11 -Wall -Wextra -Werror -fobjc-arc -fvisibility=hidden -c WifiEvidenceBridge.m -o WifiEvidenceBridge.o
xcrun clang -dynamiclib WifiEvidenceBridge.o WifiEvidenceCore.o -framework Foundation -framework CoreWLAN -framework SystemConfiguration -Wl,-exported_symbol,_nian_wifi_evidence_v1 -install_name @rpath/libNianWifiEvidence.dylib -o libNianWifiEvidence.dylib
nm -gU libNianWifiEvidence.dylib
xcrun clang -std=c11 -Wall -Wextra -Werror WifiEvidenceBridgeSmoke.c -L. -lNianWifiEvidence -Wl,-rpath,@loader_path -o WifiEvidenceBridgeSmoke
./WifiEvidenceBridgeSmoke --run
```

符号清单须只有 `_nian_wifi_evidence_v1`；两个合成测试通过后，显式 `--run` 才会进行一次只读系统观察。实际观察失败应保留错误及退出码，不将空值或不一致当作成功。尚未执行这些 Mac 编译步骤时，不声称已完成原生验证。

Windows 工具链位于含非 ASCII 字符的用户目录，当前 GCC 链接器不能找到其运行时文件，故本地仅执行 C 语法检查。为定位问题曾在 `tests/.work/macwifi-gcc-toolchain` 建立指向已有工具链的目录联接；清理该联接的操作遭自动审批拒绝，因此保留原状并不再重试，源码候选收集器跳过整个 `.work`。

独立审查发现有效输出在空长度指针时未清零，以及字符串含内嵌 NUL 和 Foundation 缓冲指针寿命风险；已补测试并改为受限字符逐个验证、复制至固定栈缓冲。Mac 隔离目录中对清零缺陷的回归测试在修复前编译通过、执行退出码 1，失败位置为该清零断言；修复后的完整编译及运行结果另由主任务记录。

修复后，主任务在新建的独立 Mac 目录验证：7 个源文件的 SHA 与 D 盘源文件一致；使用 `xcrun clang -std=c11 -Wall -Wextra -Werror` 编译 C 核心、C 规则测试、Objective-C 字符串测试及桥接实现全部通过，C 规则和字符串测试均实际运行通过。动态库的 `nm -gU` 仅列出 `_nian_wifi_evidence_v1`；C smoke 不带参数退出码为 2，显式指定 `--run` 后退出码为 0，返回完整 JSON：`{"version":1,"wifi_candidates":["en1"]}`。该结果仅证明当前 Mac 命令行环境中的一次只读观察，不证明最终 App 的本地网络权限、签名或接口物理性，也不代表其他机型已验证；本桥仍未接入产品。
