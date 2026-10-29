# V0.1 完整验收

日期：2026-10-29（Asia/Shanghai）。

**STATUS = PARTIAL**：工程地基与 Mock 生命周期验收 PASS；真实 ZCode CLI / Provider 联网诊断未验证，入口强制关闭。不能把 Mock 结果视为实际排障完成。

## 环境与构建

- Windows x64，系统版本 10.0.17763；.NET SDK 10.0.401 / runtime 10.0.12；未申请或触发管理员提权。
- `scripts/build.ps1`：locked restore + Release build 成功，0 warning、0 error。
- `scripts/publish.ps1`：win-x64 self-contained 目录成功，`artifacts/win-x64/rdt.exe help` 正常启动。
- 验证了发布后再次执行 locked restore/build；RID 与开发锁文件分离，避免互相覆盖。
- 本机已有 Python/Node/Git 未成为 RDT 的运行依赖；未安装工具、未构建或执行上游代码。
- `dotnet list Rdt.slnx package --vulnerable --include-transitive`：当前 nuget.org 审计源未报告漏洞。这不是永久安全保证。

## 自动化结果

`scripts/test.ps1` / `dotnet test Rdt.slnx -c Release`：**43 executed / 43 passed / 0 failed / 0 skipped**。

机器可读证据：`TestResults/rdt-tests.trx`。源文件：`tests/Rdt.Tests/FoundationTests.cs`。

本机生成的 TRX、日志与报告不随公开源码发布；公开发布复验汇总见 [PUBLICATION_REVIEW.md](PUBLICATION_REVIEW.md)。

覆盖：

- 正确/错误 PIN，ciphertext/tag/nonce/salt 篡改，未知版本/算法/Provider/超额 KDF 参数/异常长度、超大凭据文件、随机 salt/nonce、不覆盖已有凭据。
- 私有 session 创建、HOME/USERPROFILE/DATA_BASE_DIR 等路径、父进程环境不变、Windows 文件与目录的受保护用户 DACL。
- 活动租约保护、正常/重复 cleanup、合法旧 session 识别、未知目录/错误标记拒绝、junction 越界拒绝且外部证据保留。
- G schemaVersion=1 与临时配置删除、HTTPS endpoint 校验、D 拒绝调用。
- 无 shell 固定 plan 参数、无 PIN/Key 参数、真实 runtime fail closed、真实 Mock 子进程启动、崩溃、启动失败、取消和终止。
- 报告不置于 session 内，退出后报告保留；日志无 Key/PIN；恶意异常消息不序列化；报告写入失败仍清理；清理失败如实报告且可重试。
- CLI 要求 PIN、成功解锁、只显示 Provider 缩写；重定向输入被拒绝；等待 PIN 时传递取消信号。

## 交互终端与发布目录实测

实际运行过开发产物及自包含目录的 `rdt.exe demo`。在终端输入临时测试 PIN，工具未回显 PIN，显示：

```text
Credential unlocked.
Provider: G
Mode: READ ONLY
Session: TEMP
Runtime: MOCK
Cleanup: complete
Result: None
```

完整成功示例（运行时本地保留，不随公开源码发布）：

- `<repo-root>/Reports/<timestamp>_<random>/summary.md`
- `<repo-root>/Reports/<timestamp>_<random>/session.log`
- session ID：每次运行生成的 GUID，具体本机 ID 不纳入公开文档。
- 子进程确实启动，退出码 0；相应 `%TEMP%/RDT-<ID>` 已不存在，Reports 文件仍存在。
- 对 Reports 扫描本次测试 Key 与 PIN：0 匹配；临时 demo 凭据目录：0 残留。
- 在 PIN 提示处实际发送 Ctrl+C，工具输出 `Session cancelled.` 并结束，demo 临时目录没有残留。子进程运行期间的取消由自动化测试验证。

自动化用例还检查了真实用户 HOME/USERPROFILE 不变、`.zcode` 根目录存在性/修改时间不变，并检查实际配置只出现在隔离目录。由于未运行真实 Agent，这不是对未来真实 CLI 全部落盘行为的证明。

## 尚未完成与风险

- 未取得并实测可信 Windows 独立 CLI；公开 Release 只有桌面资产。源码有 SEA 构建能力，不等于当前机器已有可运行发行包。
- 未验证实际 G 模型、网络、计费或工具能力；本阶段没有 API 请求、真实 Key 或主账号登录。
- plan 与 profile 重定向不是 OS 沙箱。真实 Adapter 不能启用；必须完成固定产物的工具拒绝与落盘边界实测。
- 强杀/断电、文件被占用、磁盘失败可能残留。清理不宣称物理擦除；长 PIN/口令有助于离线猜测防护。
- Windows 10/11/Server 的广泛兼容性、ARM64、签名、单文件分发、Job Object 与外部 evidence 导出尚未验收。

## 下一阶段

取得可信、固定 hash 的 CLI 产物与许可证材料；先用受控本地 Provider 验证参数、schema、Read 成功与写入/执行/扩展拒绝，确认 .env、builtin refresh、SEA 缓存路径；随后才启用真实诊断 Adapter。REPAIR、D、GUI 继续不进入此阶段。
