# ZCode network behavior — fixed candidate qualification

STATUS = PARTIAL。候选 SHA-256：`f40f4b6c8709ba2cc89ea000c3a6d97e8680ee4233720f737b6b7a506417ddf8`；源码 commit：`3c5b227649371507ddc4eb46ac5133afe6db1cab`。

## 方法与实际观察

开发 fixture 只监听 loopback，捕获真实模型请求的 API 路径、工具声明和工具结果，不向上游模型转发。唯一模型认证值是固定无效 fixture token，未使用真实 G/D Key，也未登录 ZCode。

Windows GetExtendedTcpTable 以 50 ms 间隔采样候选与可发现的子进程 IPv4 TCP；Toolhelp32 同步观察进程。无需管理员、未启用 Sysmon/EDR/ETW 或更改系统审计/防火墙。UDP、IPv6、短连接、DNS 查询和 TLS SNI 不在该方法覆盖内，不能据未采样到连接声称完全离线。

| 目标 / 行为 | 实测 / 源码证据 | 结论 |
| --- | --- | --- |
| `127.0.0.1:<fixture>/v1/chat/completions` | 本地 HTTP server 收到真实候选请求、工具参数和第二次请求中的工具结果 | 配置的模型目标；没有真实模型调用 |
| `https://zcode.z.ai/api/v1/client/configs` | 固定源码的默认控制 origin 与 builtin downloader 明确调用；普通 headless 采样到额外非 loopback TCP 443（例如 `124.165.216.129:443`） | 存在模型之外的启动联网；该 IP 与具体 hostname 的关系没有通过 DNS/SNI 捕获证明 |
| builtin control endpoint 的 loopback 变体 | 父 `.env` 实验使 fixture 实际收到 `/api/v1/client/configs?app_version=0.0.0-dev&platform=windows-x86_64` 请求 | 证明这条控制面链路实际运行，且向上 `.env` 会改变目标；未只凭源码推断 |
| builtin 配置 CDN | downloader 读取上一步返回的 `builtin_provider_config_json` HTTPS URL，再下载 release JSON | 源码确认潜在第二个 host；本次未获得可证明完整的 CDN host/成功下载链，不能宣称没有 CDN 联网 |
| telemetry / OTLP | prepareModelTelemetryEnv 在没有 OTLP trace endpoint 时直接返回；本次环境不含这些变量 | 当前受控配置未启用 exporter；不是对所有上游遥测实现的完整排除证明 |
| 登录 / account / key 查询 | 新私有 profile、个人 fixture Provider、没有执行 login | 未观察到成功登录链；其他账号入口和错误恢复路径未穷尽 |
| 插件 / Skill 同步 | 全局禁用且有合成 canary；实际工具不注册，canary 不执行；随包资产仍会在 Session 物化 | 本次没有成功扩展执行。不能从缓存落盘推断发生了网络同步，也不能把未采样到连接当作完整网络证明 |
| 更新检查 | 没有调用 CLI 更新/安装命令；源码入口与实测没有提供足以证明全部更新链均不存在的证据 | NOT PROVEN，未声称全部禁用 |
| Browser / Computer Use | 全局插件关闭、无 js 工具、未启动浏览器或 GUI | 未覆盖启用后的下载/同步/网络行为；生产也未启用 |

builtin downloader 对每次请求使用新选项，`credentials=omit`、`redirect=error`，不继承控制面鉴权，20 秒总预算；这些是固定源码的约束，并非本次抓包验证过全部请求头。fixture 模型 header 中只有无效 fixture 值。

单次 prompt 完成会关闭 provider registry runtime，某些后台刷新可能在完成前被取消。因此短任务的成功不能证明网络条件健康或所有后台联网都已发生。RDT 每次创建新隔离缓存，也不能据同一进程的 TTL 行为推断跨 Session 不刷新。

Node/pnpm/GitHub/npm 下载属于**开发构建**网络；没有把它们放进客户端。运行期与构建期网络不能混作同一列表。

## 尚需关闭的观察缺口

需要在受控、可归因的 Windows 测试环境补充 DNS、IPv6/UDP、TLS 目标、短命子进程和完整生命周期观察，区分 builtin 控制面、CDN、遥测、登录、更新及扩展来源。当前活跃开发机的普通权限采样无法完成该证明；本阶段没有为了取得数据修改系统安全配置。

因此 `network_behavior = PARTIAL`，不建议生产放行。该缺口需要更完整的测试环境与观察方法，**不是要求真实模型 Key**。

## 固定源码依据

- [builtin downloader](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/packages/provider-node/src/zcode-builtin-download.ts)
- [endpoint defaults](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/packages/shared/src/zcodeEndpoint.ts)
- [CLI provider preparation](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/packages/cli/src/provider-runtime-env.ts)
- [telemetry bootstrap](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/packages/telemetry/src/bootstrap.ts)
- [dotenv discovery](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/apps/zcode-cli/packages/cli/src/env.ts)
- [upstream disclosure / NOTICE](https://github.com/zai-org/ZCode/blob/3c5b227649371507ddc4eb46ac5133afe6db1cab/NOTICE.md)

脱敏逐项观察见 [evidence-summary.json](runtime-qualification/evidence-summary.json)；原始本机路径、PID、完整模型请求与日志只在忽略的本地 artifacts 中保留。
