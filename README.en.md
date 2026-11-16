# Nian Link

**Work across nearby computers on a trusted local network.**

Nian Link is a multi-device collaboration project. A Windows controller can connect to another Windows computer or an Apple silicon Mac to share keyboard and mouse input, preview a screen with permission, and transfer text and files.

[中文介绍与界面预览](README.md) · [User guide (Chinese)](docs/USER-GUIDE.md) · [Build and test status (Chinese)](docs/构建与测试.md) · [License and release status (Chinese)](docs/许可与发布状态.md)

> **Status: development and source review.** The repository contains source, documentation and interface examples, not fully validated general-purpose installers. Earlier Windows-to-Windows and Windows-to-Mac candidates were each tested for file-free pairing and keyboard/mouse handoff; those separate tests do not establish acceptance of the current source. Return-window race cases remain, and real disconnect recovery, clean-device installation, and complete third-party distribution review are not yet verified.

[Test guide and known issues (Chinese)](docs/多设备测试版使用与已知问题-发布准备.md) · [Development map (open locally after downloading)](docs/进度技能树/index.html)

## Capabilities and limits

- **Discovery and pairing:** the controlled device opens a 30-second discovery window. Compare the complete device keyboard/mouse and controller control-channel fingerprints on both devices before confirming; names and discovery results alone do not authenticate a device.
- **Keyboard and mouse:** select a connected device on the Windows controller and return control using the interface or its indicated shortcut. Sharing uses [Deskflow](https://github.com/deskflow/deskflow).
- **Screen preview:** viewing the other screen and allowing your own screen to be viewed are separate permissions. An earlier Mac candidate was tested for preview operation and return.
- **Text and files:** text and link clipboard sharing and separate file transfer are available. Image or screenshot clipboard sharing is not supported; sending images as ordinary files still needs validation on the relevant candidate.
- **Device management:** up to four controlled devices can be configured; four concurrent devices have not been accepted in testing.

Automatic reconnection, keyboard-only emergency return, floating-window concurrency, shared-monitor input switching, current-source acceptance and long-term reliability remain unfinished or unvalidated. Loss of Internet access differs from loss of connectivity between devices; automatic recovery cannot currently be promised.

## Getting started

Use an existing isolated test build and separate test data on a trusted LAN, preserving existing installations and pairing data. Select Windows as the controller and Windows or Mac as the controlled device. On the controller, select the active network and prepare the controller; on the controlled device, choose the active network and enable discovery. Scan from the controller, compare both complete fingerprints and confirm on both devices. If controller sharing has not started, start it as prompted. Start the connection on the controlled device, check any additional keyboard/mouse fingerprint prompt, then select the device on the controller. Use the interface or its displayed shortcut to return control.

The Mac test candidate targets Apple silicon and macOS 14 or later and requires Python 3 and Deskflow. Grant local-network, accessibility or screen-recording permission as prompted. Screen sharing must be allowed again after restarting the Mac multi-device connection. The Mac test package is not notarized. See the [user guide (Chinese)](docs/USER-GUIDE.md) and [multi-device usage guide (Chinese)](docs/使用说明.md) for more detail.

## Screenshots and documentation

The [Chinese overview](README.md) includes eight synthetic interface renders. Windows images come from a WPF test fixture; Mac images were rendered using Avalonia Headless on Windows and are not Mac screenshots. A later Mac fixture width check failed; the images do not prove a complete UI test passed. See [image provenance](docs/screenshots/README.md).

- [Feature details and limitations](docs/功能详解.md) · [Project progress](docs/项目进度.md)
- [Build and test status](docs/构建与测试.md) · [Windows native build](docs/Windows原生源码干净构建.md)
- [License and release status](docs/许可与发布状态.md) · [Third-party review](THIRD-PARTY-REVIEW.md)

Original project code its contributors have the right to license is offered under [GPL-2.0-only](LICENSE). Third-party components retain their own copyrights, licenses and applicable exceptions; final distribution and corresponding-source obligations still require review. See [license scope](LICENSE-NOTICE.md) and [third-party notices](THIRD-PARTY-NOTICES.md). Do not commit pairing credentials, private keys, tokens or personal device data.
