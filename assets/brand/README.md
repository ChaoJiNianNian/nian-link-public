# 年年互联图标

## 设计与来源

用户确认的设计沿用本项目旧版 Mac「年年电脑切换」图标的视觉概念：蓝色圆角底、两台显示器与双向箭头，表示设备互联。参考素材为同一项目的旧版图标；确认稿 `nian-link-logo-approved.png` 由内置 `image_gen` 工具基于该参考生成，并非直接拷贝旧图。

生成提示词：

```text
Redesign this existing desktop connectivity app icon as a polished evolution of the reference, retaining its clean, friendly macOS look and simple blue-to-indigo rounded square. Keep the concept of two different displays and two curved directional arrows around them, with a clearly readable silhouette at very small sizes. Use flat precise vector-like white shapes and a restrained soft blue gradient; refined, subtle depth, generous spacing, and balanced composition. Single centered standalone app icon with a clean smooth silhouette and fully transparent outside, no stray fragments, no words, no letters, no logos, no mockup. Avoid glossy glass tubes and clutter. It must look clearly related to the reference while being a fresh design.
```

确认稿 SHA-256：`1e834aefe5c44cae64f1e24557ea33fa962ef14a3afeaa60159b994ed866bfc5`。该设计尚未做商标或近似标识可用性检索，不能据此声称名称或图案已获得商标权。先前否决的方案不作为当前生成母版，也不包含在候选源码中。

## 生成平台图标

从项目根目录执行：

```powershell
python -B scripts/build-brand-icons.py
```

需要 Pillow 12.2.0。脚本先验证确认稿的哈希和 RGBA 尺寸，再生成：

- `assets/brand/NianLink.ico`：Windows 多尺寸图标，包含 16、20、24、32、48、64、128、256 像素帧。16、20、24 像素使用脚本中独立绘制的双显示器和箭头简化图；32 像素及以上由确认稿缩放。
- `src/DesktopToolbox.Mac/Assets/NianDeskLink.icns`：以 1024 像素图生成的多尺寸 Mac 图标。
- `assets/brand/NianLinkBrand.png` 和 `src/DesktopToolbox.Mac/Assets/NianLinkBrand.png`：由确认稿缩放生成的两端设置页品牌图标，内容一致。
- `tests/.work/brand-tiny-comparison.png`：16 像素母版缩图与简化图在浅色、深色背景下的对照。

生成器验证 ICO 尺寸和简化帧的像素一致性、ICNS 头及预期尺寸块，并输出每个产物的 SHA-256。在固定 Python、Pillow 和压缩库版本的构建环境下，可比较这些哈希；跨环境压缩实现差异不应被误称为图形内容变化。

Windows `NianLink.exe` 使用 `ApplicationIcon`；设置页使用嵌入的品牌 PNG，托盘提取应用图标，安装器与卸载入口引用同款图标。Mac 的 `Info.plist` 指向 ICNS，`package_app.py` 校验并打入 App。更换 Mac 应用图标会改变 App 内容，重新打包签名后需要实际检查系统授权状态。

## 验证状态

ICO 多尺寸与 ICNS 数据块由生成器验证；托盘和 Mac Finder/Dock 的最终视觉效果应在实际打包后分别验证。
