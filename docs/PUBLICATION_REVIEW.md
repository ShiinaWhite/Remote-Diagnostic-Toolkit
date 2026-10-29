# V0.1 foundation 公开发布冻结记录

日期：2026-10-29。

```text
STATUS = PARTIAL
Mock foundation PASS
Real ZCode diagnostic runtime NOT ENABLED
```

本次仅冻结及发布已验收的 V0.1 foundation，不开发 V0.2，不改变架构或运行能力。源码、测试、构建脚本、配置与依赖锁文件保持冻结前内容；发布修改仅涉及 README、忽略规则和文档脱敏。

## 重新验收

- Locked restore：PASS。
- Release build：PASS，0 compiler warnings，0 errors。
- Automated tests：43/43 passed，0 failed，0 skipped；数量不变。
- win-x64 self-contained publish：PASS；二进制与发布目录仅本地保留，不上传。

## 公开内容边界

- 源码、测试、项目文件、无秘密示例配置、依赖锁文件及技术文档。
- Reports 只保留 `.gitkeep`；真实运行报告、session ID、日志及 TRX 不上传。
- 忽略 bin、obj、artifacts、TestResults、凭据文件、根 credentials、临时 session、上游 profile、环境文件、IDE cache 和系统临时文件。
- 文档中的本地运行实例改成 `<repo-root>`、`<timestamp>`、`<random>`、`%TEMP%` 等占位形式；保留全部技术结论。
- 测试中的固定 Key、PIN、密码、示例 endpoint 均是明确的虚构 fixture，不能用于任何真实服务。它们不是操作者的登录信息。

## 扫描范围与方法

发布前对完整工作树检查字符串形式的凭据、服务 token、私钥、JWT、Bearer、凭据 URL、凭据赋值；包含被忽略的日志、TRX、配置、生成文件及二进制的 ASCII/UTF-16 内容。对公开候选文件另查本机路径、用户名、机器名、个人邮箱和内部 IP。语义复核区分了测试 fixture、依赖内容哈希、源代码字段名与真实秘密；不将命中内容打印到公开文档。

凭据 URL 检测命中的 `.invalid` 地址属于 endpoint 拒绝测试的固定虚构数据，其副本也出现在本机 TRX 和测试二进制中；按该精确 fixture 复核豁免，未扩大为对整个 tests 目录的豁免。

原目录不存在 Git 历史。采用单个 main 基线提交，并在推送前扫描所有本地 Git blob、commit、tag（含不可达对象），检查提交身份只使用公开 GitHub 用户名和 noreply 邮箱。没有发现需要阻止发布的真实秘密。

推送后必须通过远端元数据、HEAD、独立获取的文件树和 blob 内容再次核验。最终远端 SHA 和扫描结果以发布汇报为准；此文档不以本地状态替代远端验证。

这些检查不构成对任意未知格式秘密的数学保证。当前真实 runtime 继续 fail closed；D、REPAIR、YOLO、提权、自动安装及真实 API 调用均未启用。
