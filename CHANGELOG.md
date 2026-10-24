# Changelog

## 0.1.3

- 实测 jp / use / usw / eu 四个区域的 Photon 网段，写入默认配置
- 新增区域采集脚本：`find_region_ips.ps1`、`collect_region_maps.ps1`
- 新增 pcapng 验证器：按 UDP 27002 + Photon 头部 + GCM 过滤，排除 CDN/API
- 中继增加 `dst seen` 日志，便于确认实际服务器 IP

## 0.1.2

- 密钥校验改为只认抓包末尾的最新 8 个数据报，避免上一局残留密钥被误选
- VRChat 进程变化时重建抓包文件，避免旧会话数据混入新会话
- 修复上一局密钥命中后立刻失效、触发第二次 trim 的问题

## 0.1.1

- 修复 VRChat DTX 静音期被当作丢包、导致近一半音轨是 PLC 合成音的问题：
  缺口默认写静音（`gap_fill: silence`），仅短缺口可选 PLC
- 中继支持逗号分隔的多网段（`--prefix a/24,b/24`），路由只增删 VRCProxy 上的条目
- 补充 UU 路由模式串接说明与多区域网段采集方法

## 0.1.0

首个公开版本。

发布流程：

- GitHub Actions：`ci.yml` 负责编译检查，`release.yml` 在推送 `v*` 标签时自动发布
- 新增完整使用说明书 `docs/MANUAL.md`

发布前 review：

- 配置中的 `relay_prefix` / `relay_port` 现在真正生效（此前 GUI 写死）
- GUI 配置解析支持数字值（此前只读字符串）
- 导出目录改用配置的 `captures_dir`
- `session.json` 不再写入会话密钥
- 名字辅助程序调用增加 60 秒超时
- trim 失败时输出可读提示，不再抛异常堆栈
- 移除未使用的流式扫描与 `wait_drain` 死代码

- Photon v5 AES-256-GCM 数据报解密
- Photon 命令分片重组
- VRChat Event 1 / USpeak / Opus 48 kHz 立体声解码
- 按 `usr_` 身份归档、房间分段（`_sN`）
- USpeak index 静音补齐与 Opus PLC
- 重复包抑制
- 本机麦克风独立音轨
- 增量 WAV 写入
- pagefile 会话密钥提取（`EmptyWorkingSet` + NTFS 原始卷扫描）
- Event 253 玩家名映射（可选辅助程序）
- WinForms 图形界面：实时列表、波形、播放、名字绑定、导出 zip
- `session.json` / `live_stats.json` 会话清单
