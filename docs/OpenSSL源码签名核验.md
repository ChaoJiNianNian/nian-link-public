# OpenSSL 3.5.9 源码签名核验

本记录核验项目留存的一份 OpenSSL 3.5.9 **源码归档**，不代表历史 Windows 测试包中的 OpenSSL 二进制与该源码对应，也不代表本项目的完整发行载荷、许可义务或全部第三方来源已经核验。

## 核验对象

- 归档：项目内 `.artifacts/deskflow-multidevice-source-review/openssl-3.5.9.tar.gz`，大小 53,279,637 字节，SHA-256 为 `603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a`，与项目固定源码清单一致。
- 官方发布签名：[openssl-3.5.9.tar.gz.asc](https://github.com/openssl/openssl/releases/download/openssl-3.5.9/openssl-3.5.9.tar.gz.asc)，931 字节，SHA-256 为 `bdccf2057a7c4ae377b40e832cb8c632ae8b532d4fb64cc7b843066eee748f89`。
- 官方发布公钥集：[pubkeys.asc](https://www.openssl-library.org/source/pubkeys.asc)，45,049 字节，SHA-256 为 `56e106cd1c44bdb117aec24795f6dfbecaa9bfa0a2901b85331f0059aad16d53`。该公钥集可能随上游维护而变化，以上摘要记录本次获取的字节，并非要求未来版本保持相同。
- [OpenSSL 官方下载页](https://www.openssl-library.org/source/)明确列出该版本的签名链接，并将 `B146647E45A7B33947AB226B2A2C87D161692D40` 列为当前发布签名证书的主密钥指纹。

## 方法与结果

2026-11-15（UTC），在项目的独立临时工作目录通过 HTTPS 下载上述公开签名与公钥集；不下载或改写上述已有归档。先对归档流式计算长度和 SHA-256，再用 Git 随附的 GnuPG 2.4.9 将官方公钥集解码为独立二进制公钥环，以 `gpgv --status-fd 1` 验证签名和原归档。验证退出码为 0，唯一 `VALIDSIG` 状态记录：

- 签名子密钥指纹：`C46ED3F2CBEFDA1FDAADA44264ED7B1DCCE71CB2`
- 状态记录的签名日期：`2026-09-29`
- 所属主密钥指纹：`B146647E45A7B33947AB226B2A2C87D161692D40`，与官方发布页公布的完整主指纹一致。

另外分别使用只含另一把历史公钥的公钥环和无关的合成内容验证，均返回非零退出码；前者报告 `NO_PUBKEY`，后者报告 `BADSIG`。这些负例说明本次检查不会接受所试的错误公钥或错误内容，不代表穷尽所有攻击场景。

本轮还对 Windows 原生入口在新隔离目录实际下载的 OpenSSL 3.5.9 归档使用同一公钥环及签名独立运行 `gpgv`，结果同样为有效签名；入口自身仍只自动核对固定大小和 SHA-256，并不自动执行 PGP 验签。

所用 GnuPG 的工作目录和公钥环均为此次独立工作目录；未导入用户密钥库，也未安装、启动或修改现用程序。第一次尝试导入完整公钥集时，Git 随附 GPG 的 agent 启动失败并返回非零；最终采用独立的 `gpg --dearmor` 与 `gpgv` 验证，不依赖该次未完成的导入。工作目录内的公钥集、签名和非敏感验证材料不自动进入仓库。

该结论依赖本次通过 HTTPS 访问的 OpenSSL 官方网页、官方公钥与签名及其所列指纹；并非独立于官方发布渠道的身份认证。公钥指纹匹配及签名成功支持该**特定源码归档**与 OpenSSL 发布签名一致；仍须分别核对实际发行二进制、构建配置、运行依赖及适用许可。
