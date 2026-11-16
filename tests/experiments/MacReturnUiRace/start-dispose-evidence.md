# Mac 协作 Start 与 Dispose 竞态（未解决）

使用真实 `MacCollaborationService`、独立合成资料和仅测试编译钩子：保存 `ShareScreenEnabled=true`，暂停读取配置后的 `StartAsync`，调用 `DisposeAsync`。现有实现的 Dispose 在 Start 仍暂停时完成；测试原本要求 Dispose 等待 Start 并在停止后拒绝提交，因此失败。失败输出在 `tests/.work/start-stop-red` 的隔离测试运行中观察到；编译输出不应作为可执行正式软件。

曾尝试用 startGate 排空：测试通过但独立复审发现 Start 在持 gate 时可能调用外部 `Changed`、取消回调或 `getPairing`；回调如果同步等待 Dispose 会与排空产生循环等待，故撤回该候选和临时测试钩子。已有 UI 订阅采用 Dispatcher.Post，但这不能证明公开服务不发生重入。要解决此问题需要重新设计启动提交、资源所有权和通知边界，另测停止前已登记的工作和回调重入；不能称当前 Dispose 可安全排空所有启动或新共享。
