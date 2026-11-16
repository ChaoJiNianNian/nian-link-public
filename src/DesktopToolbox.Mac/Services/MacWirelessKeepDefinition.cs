namespace DesktopToolbox.Mac.Services;

/// <summary>The system job runs only Apple's fixed ifconfig binary; no user-owned code runs as root.</summary>
internal static class MacWirelessKeepDefinition
{
    internal const string Label = "com.nian.desktoptoolbox.wireless-smooth";
    internal const string PlistPath = "/Library/LaunchDaemons/" + Label + ".plist";
    internal const string ServiceTarget = "system/" + Label;
    internal const string Plist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
        <key>Label</key><string>com.nian.desktoptoolbox.wireless-smooth</string>
        <key>ProgramArguments</key><array><string>/sbin/ifconfig</string><string>awdl0</string><string>down</string></array>
        <key>RunAtLoad</key><true/>
        <key>StartInterval</key><integer>3</integer>
        <key>ThrottleInterval</key><integer>1</integer>
        </dict></plist>
        """;

    private static string Header => $$"""
        set -eu
        PATH=/usr/bin:/bin:/usr/sbin:/sbin
        export PATH
        umask 077
        plist='{{PlistPath}}'
        service='{{ServiceTarget}}'
        fail() { /usr/bin/printf '%s\n' "$1" >&2; exit 73; }
        safe_dir() {
            [ -d "$1" ] && [ ! -L "$1" ] || fail 'Unsafe system directory.'
            owner=$(/usr/bin/stat -f '%u' "$1")
            mode=$(/usr/bin/stat -f '%Lp' "$1")
            [ "$owner" = 0 ] && [ "$((0$mode & 022))" -eq 0 ] || fail 'Unsafe system directory permissions.'
        }
        safe_dir /Library
        safe_dir /Library/LaunchDaemons

        read_job() {
            job_status=0
            /bin/launchctl print "$service" >/dev/null 2>&1 || job_status=$?
            case "$job_status" in
                0) loaded=1 ;;
                113) loaded=0 ;;
                *) fail 'Cannot verify wireless system job.' ;;
            esac
        }
        read_policy() {
            installed=0
            if [ -e "$plist" ] || [ -L "$plist" ]; then
                [ ! -L "$plist" ] && [ -f "$plist" ] || fail 'Conflicting wireless policy file.'
                [ "$(/usr/bin/stat -f '%u:%g:%Lp' "$plist")" = '0:0:644' ] || fail 'Unsafe wireless policy permissions.'
                /usr/bin/cmp -s "$plist" - <<'NIAN_WIRELESS_PLIST' || fail 'Conflicting wireless policy contents.'
        {{Plist}}
        NIAN_WIRELESS_PLIST
                installed=1
            fi
            read_job
            disabled_output=$(/bin/launchctl print-disabled system) || fail 'Cannot verify wireless disabled setting.'
            disabled=$(/usr/bin/printf '%s\n' "$disabled_output" | /usr/bin/awk '
                BEGIN { key="\"{{Label}}\""; value=0 }
                /^[[:space:]]*disabled services = \{/ { header++ }
                /^[[:space:]]*\}[[:space:]]*$/ { footer++ }
                $1 == key {
                    matches++
                    if (NF != 3 || $2 != "=>") invalid=1
                    else if ($3 == "disabled" || $3 == "true") value=1
                    else if ($3 != "enabled" && $3 != "false") invalid=1
                }
                END { if (header != 1 || footer != 1 || matches > 1 || invalid) exit 1; print value }
            ') || fail 'Cannot recognize wireless disabled setting.'
        }
        """;

    private const string AcquireLock = """

        lock='/Library/LaunchDaemons/.com.nian.desktoptoolbox.wireless-smooth.lock'
        if [ ! -e "$lock" ] && [ ! -L "$lock" ]; then
            (set -C; : >"$lock") 2>/dev/null || true
        fi
        [ ! -L "$lock" ] && [ -f "$lock" ] || fail 'Unsafe wireless transaction lock.'
        [ "$(/usr/bin/stat -f '%u:%g:%Lp' "$lock")" = '0:0:600' ] || fail 'Unsafe wireless transaction lock permissions.'
        exec 9<>"$lock"
        /usr/bin/lockf -s -t 5 9 || fail 'Another wireless transaction is running.'
        read_policy
        """;

    internal static string ReadScript => Header + """

        read_policy
        if [ "$disabled" -eq 1 ] && { [ "$installed" -eq 1 ] || [ "$loaded" -eq 1 ]; }; then
            /usr/bin/printf 'NIAN_WIRELESS_KEEP_V1:disabled\n'
        else
            /usr/bin/printf 'NIAN_WIRELESS_KEEP_V1:%s:%s\n' "$installed" "$loaded"
        fi
        """ + "\n";

    internal static string EnableScript => Header + AcquireLock + $$"""

        [ "$loaded" -eq 0 ] || [ "$installed" -eq 1 ] || fail 'Orphan wireless job; switch off before enabling.'
        created=0
        complete=0
        temporary=''
        rollback() {
            outcome=$?
            trap - EXIT
            [ -z "$temporary" ] || /bin/rm -f "$temporary"
            if [ "$complete" -eq 0 ] && [ "$created" -eq 1 ]; then
                /bin/launchctl disable "$service" >/dev/null 2>&1 || true
                status=0
                /bin/launchctl print "$service" >/dev/null 2>&1 || status=$?
                if [ "$status" -eq 0 ]; then
                    /bin/launchctl bootout "$service" >/dev/null 2>&1 || exit "$outcome"
                    status=0
                    /bin/launchctl print "$service" >/dev/null 2>&1 || status=$?
                fi
                if [ "$status" -eq 113 ]; then /bin/rm -f "$plist"; fi
            fi
            exit "$outcome"
        }
        trap rollback EXIT
        if [ "$installed" -eq 0 ]; then
            temporary=$(/usr/bin/mktemp '/Library/LaunchDaemons/.com.nian.desktoptoolbox.wireless-smooth.XXXXXX')
            /bin/cat >"$temporary" <<'NIAN_WIRELESS_PLIST'
        {{Plist}}
        NIAN_WIRELESS_PLIST
            /usr/sbin/chown 0:0 "$temporary"
            /bin/chmod 644 "$temporary"
            /usr/bin/plutil -lint "$temporary" >/dev/null
            /bin/mv -f "$temporary" "$plist"
            temporary=''
            created=1
        fi
        /bin/launchctl enable "$service"
        if [ "$loaded" -eq 0 ]; then /bin/launchctl bootstrap system "$plist"; fi
        read_job
        [ "$loaded" -eq 1 ] || fail 'Wireless system job did not load.'
        /sbin/ifconfig awdl0 down
        complete=1
        """ + "\n";

    internal static string DisableScript => Header + AcquireLock + """

        /bin/launchctl disable "$service"
        if [ "$loaded" -eq 1 ]; then /bin/launchctl bootout "$service"; fi
        read_job
        [ "$loaded" -eq 0 ] || fail 'Wireless system job is still loaded.'
        if [ "$installed" -eq 1 ]; then /bin/rm -f "$plist"; fi
        /sbin/ifconfig awdl0 up
        """ + "\n";

    internal static MacWirelessCommandResult ParseReadResult(MacWirelessCommandResult result)
    {
        if (result.Unsupported || result.Cancelled || result.TimedOut || result.AuthorizationPending) return result;
        if (result.ExitCode != 0)
            return result with { KeepEnabled = null, Error = result.ExitCode == 73
                ? "自动保持配置异常，请检查系统任务。" : "自动保持状态未确认，请刷新重试。" };
        return result.Output.Trim() switch
        {
            "NIAN_WIRELESS_KEEP_V1:disabled" => result with { KeepEnabled = true, Error = "自动保持已被系统停用，请关闭后重新开启。" },
            "NIAN_WIRELESS_KEEP_V1:1:1" => result with { KeepEnabled = true, Error = "" },
            "NIAN_WIRELESS_KEEP_V1:0:0" => result with { KeepEnabled = false, Error = "" },
            "NIAN_WIRELESS_KEEP_V1:1:0" => result with { KeepEnabled = true, Error = "自动保持未运行，请关闭后重新开启。" },
            "NIAN_WIRELESS_KEEP_V1:0:1" => result with { KeepEnabled = true, Error = "自动保持记录不一致，请关闭后重新开启。" },
            _ => result with { KeepEnabled = null, Error = "无法识别自动保持状态，请刷新重试。" }
        };
    }

    internal static string AppleScriptLiteral(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}