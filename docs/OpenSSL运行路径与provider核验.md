# OpenSSL 3.5.9 运行路径与 provider 核验

状态：隔离环境中的 OpenSSL 命令行观察；未完成产品运行、迁移或发行验证。此记录不包含个人路径和配对资料。编译时固定前缀属于临时构建目录，不能据此认定产物可迁移或可发行。

## 官方行为与依据

- 构建配置 `--prefix` 和 `--openssldir` 影响安装位置及配置目录，具体选项和默认值见 [3.5.9 构建说明](https://github.com/openssl/openssl/blob/openssl-3.5.9/INSTALL.md)。[3.5.9 源码](https://github.com/openssl/openssl/blob/openssl-3.5.9/crypto/defaults.c) 显示 Windows 编译配置可以影响路径解析；不得据此推断其他历史二进制使用相同设置。
- [环境变量说明](https://docs.openssl.org/3.5/man7/openssl-env/)列出 `OPENSSL_CONF`、`OPENSSL_CONF_INCLUDE`、`OPENSSL_MODULES` 和 `OPENSSL_ENGINES`。它们可影响配置或模块位置；未来若需要配置，须先评估进程隔离与可信目录，不应随意修改系统全局环境变量。
- [默认 provider 文档](https://docs.openssl.org/3.5/man7/OSSL_PROVIDER-default/)说明：未曾尝试加载其他 provider 时，默认 provider 可自动加载；一旦尝试过显式加载（即使失败），就不再自动回退。需要同时使用默认及其他 provider 时，须按应用的真实需要明确配置。
- [provider API](https://docs.openssl.org/3.5/man3/OSSL_PROVIDER/)支持内置及动态模块和库上下文下的模块搜索路径；是否适用于应用，取决于初始化时机和实际使用的库上下文，不应在未确认调用链前直接更改。
- [`openssl info`](https://docs.openssl.org/3.5/man1/openssl-info/)可检查 CLI 报告的默认目录和 Windows context，结果不等于产品进程实际使用的目录。Windows DLL 搜索亦需考虑[搜索顺序和劫持风险](https://learn.microsoft.com/en-us/windows/win32/dlls/dynamic-link-library-security)。

## 本机隔离观察

以独立编译前缀中的 OpenSSL 3.5.9 命令行执行；四个 OpenSSL 路径覆盖变量仅从子进程环境移除，系统环境未修改。[脱敏机读证据](openssl-runtime-evidence.json)包括命令类别、退出码、provider 名称、路径归属布尔值以及被测程序 SHA-256。

| 检查 | 结果 | 能说明什么 |
| --- | --- | --- |
| `info -configdir`、`-enginesdir`、`-modulesdir` | 均退出 0，报告路径位于隔离前缀 | 本次 CLI 的默认路径依赖隔离前缀 |
| `info -windowscontext` | 退出 0，`Undefined` | 仅说明本次 CLI 的报告值 |
| 默认 `list -providers` | 退出 0，显示 default | 这次 CLI 可载入默认 provider |
| 显式 legacy | 退出 0，显示 legacy | 本次前缀中的 legacy 可被 CLI 载入 |
| 子进程指定不存在的模块目录后显式 legacy | 非零，未显示 legacy | 模块目录错误会导致该次 CLI 加载失败 |
| 显式 default 和 legacy | 退出 0，显示两者 | 该命令可同时列出两者 |

隔离安装前缀可见 legacy 模块和若干 engine DLL；文件存在并不代表软件需要、加载或将它们包含在发行包中。未移动该前缀，也未测试其他电脑。以上结果不证明 Deskflow、Qt 的 TLS 通路、证书验证或发行可迁移性。

## 发布前验收清单

1. 固定实际发行包以及各组件版本和 SHA-256，检查程序的配置加载、使用的 OpenSSL 库上下文、provider 和 engine 真实需求，并记录每个进程的依赖与默认目录。
2. 在全新隔离 Windows 环境中，将测试副本分别放在两个不同目录，清除相关 OpenSSL 覆盖变量作为对照；让原构建路径不可用，但只操作隔离副本或虚拟机，不触碰真实构建和用户数据。覆盖缺失模块、错误配置、非可信搜索路径及显式 default/legacy 组合，记录退出码与实际加载路径。
3. 使用独立测试版验证应用的正反向 TLS 连接、指纹和合成证书验证，并检查不同权限、含空格和非 ASCII 路径。验证实际进程的 DLL 和 provider 来源，检查目录权限与 DLL 搜索路径；不要依赖全局环境或宽泛的系统 PATH。
4. 核验 Qt 实际启用的 TLS 后端与加载的 OpenSSL DLL 是否为预期版本，排查 DLL 抢先加载；同时核实 Deskflow、Qt、OpenSSL 以及 Windows 运行库的发行依赖、对应源码、许可证和通知。
5. 若将来需要 FIPS，应另行核对经过验证的模块与安装完整性，不得把本次测试解释为 FIPS 认证或符合性证明。

这些项目尚未验收；当前只提供调查与可复核的 CLI 证据。
