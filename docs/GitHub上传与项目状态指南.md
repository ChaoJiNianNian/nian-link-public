# 年年互联：GitHub 上传与项目状态指南

这份说明用于将来整理完成后上传源码。**现在还没有经过完整许可与隐私审核、可以直接公开上传的完整源码包。** 本文不表示已经创建仓库、提交或上传任何文件。

## 现在是什么状态

- 目前使用的桌面工具箱正式版与正在研发的年年互联多设备候选是不同版本。继续使用现有正式版即可，不需要为了上传而替换它。
- 多设备候选曾在 Windows 双机和 Windows 与 Mac 上分别进行过连接与键鼠切换测试，但最新修改、异常断线、应急归还和自动恢复尚未完整验收。不能把它描述为已正式发布的稳定版。
- 现有[多设备版使用说明（内部测试）](多设备版使用说明-内部测试.md)、[安装与首次连接](USER-GUIDE.md)和[免文件配对内测说明](免文件配对内测使用说明.md)分别说明不同测试流程。公开前需要核对版本、安装方法、下载地址、系统权限和未完成事项，不能把它们混成一套正式版说明。
- 旧的源码审查 ZIP 是内部历史快照，不能直接上传；对应版本的源码、第三方依赖许可、隐私以及干净环境构建仍须审核。源码不完整或许可不清楚时，即使先建私有仓库，也不要把未经核对的资料传上去。

## 先准备一个独立的私有仓库

以下是由您自行操作的步骤，本指南不会替您执行提交或上传。按钮名称可能随 GitHub Desktop 更新而略有变化。

**如果已经在 GitHub 网页上创建了私有仓库，跳过本节中新建及 Publish repository 的步骤。** 登录 GitHub Desktop 后选择 **File → Clone repository…**，在 GitHub.com 选项卡选择已有的私有仓库，确认本地路径为 `D:\Projects` 下新建的独立文件夹，然后点击 **Clone**。若仓库不在列表中，先确认登录的是创建该仓库的账号。克隆只建立本地副本，不会自动上传年年互联工程；不要选择现有的 `[本机路径已省略]` 工作目录。具体操作见 [GitHub Desktop 克隆仓库指南](https://docs.github.com/en/desktop/adding-and-cloning-repositories/cloning-and-forking-repositories-from-github-desktop)。

1. 如果还没有账号，到 [GitHub](https://github.com/) 注册或登录；到 [GitHub Desktop 官网](https://desktop.github.com/) 下载并安装 Windows 版。登录只在 GitHub 的页面或程序中进行，不要把密码、验证码或令牌发给他人。
2. 打开 GitHub Desktop，按提示登录 GitHub。点击 **File → New repository…**（新建仓库）。
3. **Name** 可填写 `nian-link-open-source`；**Description** 可留空。**Local path** 选择 `D:\Projects`，Desktop 会在其中新建名为 `nian-link-open-source` 的独立文件夹。若同名文件夹已存在，另选一个新的名称，不要覆盖。
4. 勾选 **Initialize this repository with a README**，先创建一个仅含初始说明的本地仓库；点击 **Create repository**。这一操作会生成本地的初始提交，不会自动上传项目源码。
5. 点击 **Publish repository**，确认勾选 **Keep this code private**（保持私有），确认发布到自己的账号，再点 **Publish Repository**。此时上传的是新仓库中已有的内容；请先确认只有初始 README 等您认识的文件。
6. 点击 **Repository → View on GitHub**，在网页确认仓库标记为 **Private**。私有仓库不是零风险的保险箱，不应放入不必要的敏感资料。

**不要将 `[本机路径已省略]` 整个工作目录拖入 GitHub Desktop。** 该目录当前不是 Git 仓库，并混有内部测试、构建产物和仅供本人使用的资料；也不要直接上传 `.artifacts`、`tests/.work`、配对 JSON、证书、私钥、令牌、用户资料或内部日志。不要上传旧源码审查 ZIP 代替审核。

## 审核完成后上传源码

以下步骤要等我们准备并明确标识“可以上传”的独立源码目录后再执行；目前尚无此目录，不能凭文件夹名称自行猜测。公开前需要核实源码和对应第三方组件及许可、检查个人信息与凭证、核对教程，并在干净环境验证构建。源码仓库和安装包是两种不同交付物，不能仅凭仓库存在就说用户可直接安装。

1. 在文件资源管理器打开**已经审核的独立源码目录**，将其中审核通过的内容复制到刚创建的仓库实际文件夹；如果您在上一步使用了其他仓库名称，就以那个文件夹为准。先确认 GitHub Desktop 中显示的仓库名称与本地文件夹一致。不要复制其他目录，保留仓库自己的隐藏 `.git` 文件夹，也不要覆盖原开发工程。
2. 回到 GitHub Desktop 的 **Changes**，逐项查看准备提交的文件及变更；有不认识的文件或敏感资料时先停下处理，不要勾选上传。即使文件后来删除，之前提交过的内容也可能仍留在 Git 历史中。
3. 在 **Summary** 写简短说明，例如 `Add reviewed source`，点击 **Commit to main**（分支名称可能不同）。这是保存到本机仓库，尚未上传。
4. 点击顶部 **Push origin**，把刚才的提交传到 GitHub。点击 **View on GitHub** 检查文件、README 和 **Private** 标记。日后修改也是先检查 Changes、Commit，再 Push。

GitHub Desktop 的官方图文说明：[创建并发布第一个仓库](https://docs.github.com/en/desktop/overview/creating-your-first-repository-using-github-desktop)。

## 将来决定公开时

先确认每个历史提交都适合公开、相关许可和对应源码已核实、使用教程与真实发布版本一致；一旦公开，他人可能保存或复制内容，之后改回私有不一定能收回。确认后，在 GitHub 仓库页面点击 **Settings → General → Danger Zone → Change repository visibility**，选择 **Public**，按提示确认。设置入口和权限以实际页面为准。详见 [GitHub 官方仓库可见性说明](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/managing-repository-settings/setting-repository-visibility)。

在审核与正式验收前，README 应写明“开发中／内测”，不要宣称自动恢复、正式安装包、签名或全部设备功能已经完成。
