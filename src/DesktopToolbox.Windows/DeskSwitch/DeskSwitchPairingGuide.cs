using System.Text;

namespace DesktopToolbox.Windows.DeskSwitch;

public static class DeskSwitchPairingGuide
{
    public static async Task WriteAsync(string directory, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, string>
        {
            ["README-配对.txt"] = Instructions,
            ["打开Mac端.command"] = MacLauncher
        };
        foreach (var (name, text) in files)
        {
            var path = Path.Combine(directory, name);
            for (var cursor = path; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
                if ((Directory.Exists(cursor) || File.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("配对说明目录不能经过文件链接。");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, text.Replace("\r\n", "\n"), new UTF8Encoding(false), cancellationToken);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private const string Instructions = """
Win / Mac 键鼠共享与切屏
=====================

Windows：
1. 在工具箱保存两台电脑的英文名称，生成配对文件，再点“打开 Deskflow”。
2. Windows 使用独立便携配置 engine/settings/Deskflow.conf，导出的 win.ini 仅供查看。图形界面不支持 --settings 参数。
3. 点击原版 Deskflow 的开始按钮；允许所需的本地网络连接，两端核对证书指纹。工具箱不自动放行防火墙，也不把打开窗口当作已配对。

Mac：
1. 从官方安装 Deskflow 1.27.0，按 Apple 芯片/Intel 选择对应包。
2. 把 mac.ini、打开Mac端.command、本说明复制到 Mac 上同一个文件夹。
3. 正常退出 Mac 原有的全部 Deskflow。打开终端，在输入 bash 后加空格，将“打开Mac端.command”拖进终端，再按回车。
4. 脚本只在此文件夹创建 mac-profile/mac-state；通过原版支持的 XDG_CONFIG_HOME 和 XDG_STATE_HOME 使用私有设置，不改已安装的 Deskflow.app。
5. 原版窗口内确认本机名称与 mac.ini 相同，服务器填写 Windows 工作台显示的可互通局域网地址。脚本生成的地址可能来自 VPN/虚拟网卡，若不通请在原版窗口换成实际局域网地址。
6. 按原版提示授权辅助功能/输入监控/本地网络。两端核对指纹再允许连接；确认来自自己的电脑。
7. 每次在 Win 重新生成新配置后，重新复制 mac.ini 并运行脚本；原 Mac 配置会先留 .previous 备份，证书/信任子目录保留。
8. 系统阻止运行、权限失效、睡眠/网络变化需按原版说明处理，本工具不绕过系统权限。

使用：
Ctrl+Alt+F12：将键鼠控制权交给 Mac。
Ctrl+Alt+F11：回到 Win。回切热键由原版 Deskflow 处理，不依赖当前画面可见。

画面联动：
先接好两台电脑到显示器不同输入，打开显示器 DDC/CI（若支持）。
在工具箱检测显示器并显式设置 Win/Mac 输入；先测试且确认两方向可切，再启用联动、重新生成配置并重开 Deskflow。
测试切到 Mac 时可能黑屏，请提前知道如何用显示器实体按钮切回 Win。
检测成功不等于双向联动成功；未选中输入口是否接受控制、转接器和设备兼容性都要实测。当前内屏没有可控句柄时应显示不支持，不能猜成功。
原版键鼠共享与显示器的视频输入是两条独立通道。

停止：
键鼠接在 Win 上，Win 必须保持开机、不休眠。
关闭工具箱不会强杀独立 Deskflow；停止共享请在原版窗口停止并退出。
改连接设置前先正常退出两端 Deskflow，避免全局单实例打开了另一份旧配置。

来源：
Deskflow 1.27.0 / https://github.com/deskflow/deskflow
主要许可 GPL-2.0-only WITH LicenseRef-OpenSSL-Exception。
保留原版品牌与来源。完整源码归档与许可在工具箱 THIRD-PARTY-NOTICES/Deskflow 中。
""";

    private const string MacLauncher = """
#!/bin/bash
set -eu
task_dir="$(cd -- "$(dirname -- "$0")" && pwd -P)"
if pgrep -x deskflow >/dev/null 2>&1 || pgrep -x Deskflow >/dev/null 2>&1; then
  echo "请先在原版窗口正常退出所有 Deskflow，再运行本文件。"
  exit 1
fi
source_app="/Applications/Deskflow.app"
if [ ! -d "$source_app" ]; then source_app="$HOME/Applications/Deskflow.app"; fi
binary="$source_app/Contents/MacOS/deskflow"
if [ ! -f "$binary" ]; then
  echo "没有找到已安装的官方 Deskflow.app。请先安装 1.27.0，并放到 Applications。"
  exit 1
fi
if [ ! -f "$task_dir/mac.ini" ]; then echo "同一个目录缺少 mac.ini，请从工作台重新复制。"; exit 1; fi
profile="$task_dir/mac-profile"
state="$task_dir/mac-state"
if [ -L "$profile" ] || [ -L "$state" ]; then echo "配对资料不能使用链接目录。"; exit 1; fi
mkdir -p "$profile/Deskflow" "$state"
config="$profile/Deskflow/Deskflow.conf"
baseline="$profile/Deskflow/toolbox-generated.ini"
if [ -L "$profile/Deskflow" ] || [ -L "$config" ] || [ -L "$baseline" ]; then echo "配对资料不能使用文件链接。"; exit 1; fi
if [ ! -f "$baseline" ] || ! cmp -s "$task_dir/mac.ini" "$baseline"; then
  if [ -f "$config" ]; then cp -p "$config" "$config.previous"; fi
  cp "$task_dir/mac.ini" "$config"
  cp "$task_dir/mac.ini" "$baseline"
fi
# Opening settings must not silently resume sharing saved by the previous GUI exit.
awk '
  BEGIN { in_gui=0; written=0 }
  /^\[gui\][[:space:]]*$/ { in_gui=1; print; next }
  /^\[/ {
    if (in_gui && !written) { print "startCoreWithGui=false"; written=1 }
    in_gui=0
  }
  in_gui && /^startCoreWithGui[[:space:]]*=/ {
    if (!written) { print "startCoreWithGui=false"; written=1 }
    next
  }
  { print }
  END { if (in_gui && !written) print "startCoreWithGui=false" }
' "$config" > "$config.new"
mv "$config.new" "$config"
nohup env XDG_CONFIG_HOME="$profile" XDG_STATE_HOME="$state" "$binary" > "$state/launcher.log" 2>&1 &
echo "已请求打开原版 Deskflow。请在窗口中检查地址、权限和指纹，再手动开始共享。"
""";
}
