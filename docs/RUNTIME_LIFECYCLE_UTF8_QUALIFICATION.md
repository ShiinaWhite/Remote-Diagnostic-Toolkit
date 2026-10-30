# Windows Job lifetime 与 UTF-8 qualification

STATUS = PASS（本阶段限定的两个问题）。生产 `ZcodeRuntime` 仍为 `RUNTIME_NOT_VALIDATED`，没有启用真实模型，也不 merge main。

基线 `76254e3b972c26dd2ec9bc8ba8b23f9548419bdd`。沿用同一官方源码 SEA：upstream `3c5b227649371507ddc4eb46ac5133afe6db1cab`，CLI 0.17.0，runtime SHA-256 `f40f4b6c8709ba2cc89ea000c3a6d97e8680ee4233720f737b6b7a506417ddf8`。builtin 来源与 hash 继续复用 Phase 2 identity。默认方向仍为 `agent-server --stdio + mode=yolo`，不增加只读采集器、命令权限层或新产品功能。

## 实现范围

新增 `tools/Rdt.LifecycleQualification` 开发 bridge、固定 synthetic fixture、证据判定测试和脱敏汇总。开发 bridge 不属于 Rdt.slnx 或客户 publish graph，生产源码未改。共享既有 protocol client、失败后 PID recovery 与**同一份 SessionPaths 源文件**；强杀 wrapper 后的测试清理仍由现有 SessionCleaner 完成，不另造宽松路径判断。

Job 使用未命名、不可继承的 SafeHandle，唯一生命周期 limit 是 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (0x2000)`。创建后用 GetHandleInformation / QueryInformationJobObject 验证实际句柄属性及 flags；不设置 BREAKAWAY_OK、SILENT_BREAKAWAY_OK、UI/security、CPU、内存、进程数量或其他资源限制。嵌套测试外围 Job 的 flags=0 只用于构造“wrapper 已在另一个 Job”的环境；runtime 自身 Job 始终是 0x2000。

流程：CreateJobObject → SetInformationJobObject → Process.Start → AssignProcessToJobObject → IsProcessInJob → 才发送 session/create / session/send。任一步失败都停止候选，不能进入 YOLO Session。Create/config 在启动前完成；Assign 失败立即停止未准入的 runtime。故障注入验证三个失败点均为 0 protocol requests、0 Provider requests，并完成 Session 清理。

使用当前 caller token，不调整权限、不自动提权、不启用 breakaway 绕过宿主 Job。真实 API 失败保留 Win32 error code，作为兼容性失败报告，不降级为无 Job 运行。

Job 只表示正常后代进程的生命周期归属，不是 sandbox、命令 permission system 或修改回滚。Session 活着时正常 Bash、PowerShell、Write、Edit、Start-Process 能力保留；实际 Provider 仍注册完整 YOLO 工具面，Write/Edit 回归实际成功。Job 不回滚文件/注册表/服务变更，也不承诺约束由外部 broker/服务另外创建的非后代进程。

## 启动窗口与嵌套 Job

实测记录 Process.Start 到 Assign 的时间、调用前后 Job 状态，并在任何 Session frame 前对进程快照交叉检查。额外 `startup-race` 将未绑定窗口扩大到 2 秒，不发送 Session 请求。

观察到 Windows 系统目录的 **conhost.exe**：它可在 Assign 前出现，实际 IsProcessInJob 为 false。未把它隐藏成 RDT Job 成员。检查其真实模块路径与系统目录一致，并保留进程句柄验证其随 root 退出而结束；全部测试均无 startup recovery。未知、未入 Job 的启动子进程会阻止协议准入。这个 Windows console helper 没有形成长生命周期逃逸；后续实际 Agent PowerShell 与 detached PowerShell 均由 Job API 证实属于 RDT Job。

因此固定 SEA 当前未观察到需要 CREATE_SUSPENDED 的不可接受 race，本版保留 Process.Start → Assign。该结论只覆盖固定 hash，不是对任意/未来 runtime 的无竞态证明；换版本须重跑。

实际测试宿主 wrapper 已处于 Job；runtime 在新绑定前也继承了已有 Job。普通场景 Assign 成功。另一个 controlled nested-job 用例把 wrapper 再放进无资源/UI限制的外围 Job，runtime 仍成功进入专用生命周期 Job。不是对所有远程运维软件、Terminal、企业限制策略的兼容承诺；不兼容时 fail closed。

## Membership 与退出证据

不从父子 PID 关系推断 Job membership。使用：

- IsProcessInJob(runtime handle, RDT Job handle)。
- PowerShell marker 的 Session ID 与精确 StartTime ticks 匹配后，再 IsProcessInJob。
- 同样的方法验证 Start-Process detached child。
- QueryInformationJobObject(JobObjectBasicProcessIdList) 枚举成员并保留进程句柄；Job 关闭后等待这些句柄退出，避免 PID reuse 造成误判。

PID/创建时间/完整协议、命令、环境和工具输出只在忽略的本地 artifacts 中。公开 [脱敏证据](runtime-qualification/lifecycle-utf8-evidence-summary.json) 只包含成员布尔值、时间/计数、退出码、检查结果和合成 Unicode 数据。

| 路径 | 结果 |
| --- | --- |
| 正常工具完成 → ZCode EOF 退出 → 关闭 Job | PASS |
| detached-child + runtime 正常退出 | PASS，parent tool 完成、detached child 在 Job 关闭前仍存活，关闭后退出，无 PID recovery |
| detached-child + session/stop | PASS，相同 membership 与前后存活证据 |
| detached-child + wrapper CancellationToken | PASS，先完成创建 detached child 的工具，再启动另一个 hold 工具；真实 wrapper token 取消传播到 await，stop/EOF 后 orphan 仍由 Job 收回 |
| 普通 session/stop | PASS |
| 模型在途取消 | PASS，fixture 实际观察连接中止 |
| 前台工具取消 / wrapper token 取消 | PASS，实际成员查询、stop ACK、最终句柄退出 |
| 工具失败 | PASS，真实 Exit code 7，ZCode 正常退出，Job 清理完整 |
| runtime 错误退出 | PASS，membership 确认后对本次 root 注入单进程终止，真实非 0；剩余后代由 Job 清理。不是伪造上游 crash，也不是用 kill-tree 作 cleanup |
| wrapper cleanup | PASS，工具仍运行时显式关闭 Job，root 与成员终止 |
| wrapper 被强杀 | PASS（进程生命周期），只终止 wrapper；OS 关闭其最后的非继承 Job handle，已保留句柄的 root/后代及 console helper 全部退出 |

强杀 wrapper 时 Session 目录会留下，这是没有执行 finally 的实际结果。测试父进程用已保存的精确 marker GUID/创建时间及现有 Cleaner 验证后清理；没有宣称 Job 自动删除目录，也没有降低 marker、lease、temp 根或 reparse point 条件。父进程没有取得/复制 Job handle，以免改变 last-handle-close 语义；只保留进程句柄。

PID recovery 仅作为失败后兜底。任何 recovery_required=true 都判 FAIL，不能因最终文件夹消失而改成 PASS。本阶段成功用例均 recovery_required=false。

## UTF-8 契约

所有本阶段 PowerShell synthetic commands 使用同一 preamble：

```powershell
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
```

bridge 的 stdin 为 UTF-8 无 BOM，stdout/stderr 为严格 UTF-8 decoder；任务只进 stdio frame。固定脚本通过 EncodedCommand 的 UTF-16LE Base64 传入，文件显式以 UTF-8 无 BOM 写入、严格 UTF-8 读取。不修改 ExecutionPolicy，不做 OCR、乱码替换或事后转码修复。

真实链路 `PowerShell → Bash Tool → ZCode → loopback Provider` 已逐字符验证：

```text
山西晋铁源-中文诊断测试
中文路径测试
服务状态：正常
```

分别核验 Console/PowerShell 直接字符串、ConvertTo-Json 原始输出及其解析值、UTF-8 文件完整内容与 SHA-256、实际中文目录/文件名 `中文路径测试/中文诊断.txt`，再让 builtin Read 读取该文件。检查完整工具结果没有 U+FFFD，不能只检查退出码或字符串子串。

| Shell | 实际版本 | Direct / JSON / file / path / builtin Read |
| --- | --- | --- |
| Windows 自带 Windows PowerShell | 5.1.17763.1007 | 全部 PASS |
| 本机 PowerShell 7 | 7.6.6 | 全部 PASS |

保留的首次失败：5.1 的 Get-Content -Raw 结果带 PowerShell 扩展属性，直接深度 JSON 序列化产生约 41 MB，导致上游 persisted-output 截断；7 的同样组合未出现该放大。改用显式 UTF-8 ReadAllText 返回普通字符串并只序列化有界字段后，两者均通过。原始失败保留在本地，不把截断改写为当时已通过。5.1 的 CLIXML/progress 包装与 7 也不同，均不需要修复字符。

运行错误注入初版的长 Base64 命令还触及 Windows shell 命令长度，PowerShell 报 EncodedCommand 参数错误，未构成 runtime 错误退出证明。现改为 wrapper 在 membership 证实后对拥有的 root 注入终止，真实退出码记录在证据；固定 synthetic 命令另设长度预检。这只约束测试载荷，不限制 Agent 的命令权限。

结论是**显式 preamble + 显式 UTF-8 文件/协议**的契约通过；不声称任意 legacy native 程序、缺省编码或任意 Agent 生成的脚本都自动变成 UTF-8。生产接入前仍须 Review 如何要求/构造这条约定。

## 重放与回归

```powershell
dotnet restore tools/Rdt.LifecycleQualification/Rdt.LifecycleQualification.csproj --locked-mode
dotnet build tools/Rdt.LifecycleQualification/Rdt.LifecycleQualification.csproj -c Release --no-restore
dotnet tools/Rdt.LifecycleQualification/bin/Release/net10.0/Rdt.LifecycleQualification.dll self-test
python -m unittest discover -s scripts/runtime-qualification -p test_lifecycle.py -v
python scripts/runtime-qualification/qualify_lifecycle.py --candidate <fixed-SEA> --builtin <fixed-builtin-json>
python scripts/runtime-qualification/summarize_lifecycle.py
```

CI 只执行 foundation、无 candidate 的 native Job 自测和 Python harness 测试；不下载候选，不用真实 Key，不上传 Reports/artifacts。实际 SEA qualification 只在开发测试环境显式运行。

本次实际 SEA 用例 21/21 PASS；Native Job API 自测 12 项检查通过；Foundation 69/69、全部 Python harness 41/41 PASS。Release build 0 warnings / 0 errors，win-x64 self-contained publish 及 help smoke check 通过。公开文件、全部 Git 对象（含 UTF-16）与 decoded JSON 私有 profile 路径检查、Gitleaks 扫描均无发现。生产源码、原 tests 与 Phase 1/2 脱敏证据 JSON 对基线保持不变。

NotSigned、0.0.0-dev、普通用户能力和完整整机 attribution 继续保留，但不是本阶段 blocker。权限设计为 RDT inherits caller token；需要管理员运维时由操作者显式以管理员身份启动，不存在自动提权。Phase 1/2 的失败和只读历史证据保留。LICENSE = UNDECIDED。

下一步只提交本分支供外部 Review。生产 Adapter 仍关闭；本阶段 PASS 不自动解锁。

官方依据：[Job Objects / last-handle-close](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)、[AssignProcessToJobObject / nested Jobs](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject)、[IsProcessInJob](https://learn.microsoft.com/en-us/windows/win32/api/jobapi/nf-jobapi-isprocessinjob)、[QueryInformationJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-queryinformationjobobject)、[PowerShell character encoding](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_character_encoding)。上述 Windows API 语义以固定 SEA 实测补充，不据文档推断实测结果。
