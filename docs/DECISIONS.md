# V0.1 决策

- 2026-10-29：选 C# / .NET 10（本机已有 SDK），Core 与 CLI 分离，Windows 优先；开发期 SDK 依赖，发布支持 self-contained win-x64 目录。最低 Windows/Server 版本须后续逐项实测，不能把当前主机通过外推到全部 Windows 10/11/Server。
- scrypt 使用 BouncyCastle.Cryptography 2.7.0（NuGet 官方 feed 查询确认），AES-256-GCM 使用 .NET；不自研密码算法。参数固定版本化，拒绝文件控制的任意 KDF 成本。
- xUnit 与 Microsoft.NET.Test.Sdk 自动化；不需要真实 API、不依赖上游构建。依赖精确版本和 lock 文件纳入工程。
- 开发构建与 RID 发布分别使用 `packages.lock.json` / `packages.win-x64.lock.json`，避免发布后覆盖开发锁文件导致后续 locked restore 失败。
- 默认 READ ONLY；REPAIR 与 D 只保留接口，拒绝调用。真实 runtime fail closed；Mock 是明确标识的工程验收，不是诊断成功。
- 报告仅导出受控元数据，原始 prompt/stdout/stderr/内部日志不落入审计日志，避免泄露业务数据。后续 evidence 导出另行设计。
- session 使用 GUID、私有目录权限、标记文件和排他 lease；cleanup 不接受任意路径，拒绝 reparse point，不扫描删除没有合法标记的目录。
- 启动只提醒可识别旧 session；`cleanup-stale` 只处理超过 24 小时、无活动租约的合法 session。不碰普通用户文件。
- Reports 与 session 树分离；保留报告和显式创建的加密凭据。演示凭据在临时目录中生成并删除，测试使用假 Key。
- 不自动安装、不提权、不修改安全策略、不默认打开 TUI；不提供任意命令执行入口。
