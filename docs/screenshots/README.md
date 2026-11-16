# 界面图片来源与范围

本目录为年年互联多设备候选的界面展示资料。所有图片由提交 `5e94e129d735bcc1da3ea67ebd849f635fd0ebaf` 的独立源码副本生成；设备名称、状态与连接资料均为测试夹具提供的合成示例，不含真实配对、网络地址或个人桌面。

- Windows 图片由 `tests/MultiDevice.UiFixture` 使用 WPF 候选窗口及合成设备渲染，三次截图过程退出码均为 0；不是现用正式版或真实多设备连接的证明。
- Mac 图片由 `tests/Mac.MultiDeviceVisual` 使用真实候选窗口、FakeVault 和 Avalonia Headless 生成。它们不是 Mac 真机截图，也不能证明真实权限或设备行为。该次夹具先生成这五页图片，随后在“设置与权限”页的窗口宽度检查失败并以非零退出，不能称整套界面回归通过。
- 八张 PNG 均经目视检查、解码验证和元数据检查；未来替换时仍须逐图核验内容及元数据，并更新收集器里的固定哈希。图中的不可用按钮、示例名称或状态不代表实际已连接。

| 文件 | 展示页面 |
| --- | --- |
| `windows-devices.png` | Windows 主控设备 |
| `windows-screen.png` | Windows 主控画面权限 |
| `windows-controlled.png` | Windows 被控设备 |
| `mac-devices.png` | Mac 被控设备 |
| `mac-discovery.png` | Mac 发现与配对 |
| `mac-screen.png` | Mac 画面权限 |
| `mac-keyboard.png` | Mac 键鼠 |
| `mac-files.png` | Mac 文件 |
