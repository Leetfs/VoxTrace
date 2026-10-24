# Changelog

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
