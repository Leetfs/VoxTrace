# VoxTrace

[![CI](https://github.com/Leetfs/VoxTrace/actions/workflows/ci.yml/badge.svg)](https://github.com/Leetfs/VoxTrace/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Leetfs/VoxTrace)](https://github.com/Leetfs/VoxTrace/releases)
[![License](https://img.shields.io/github/license/Leetfs/VoxTrace)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)](docs/MANUAL.md)

> **把 VRChat 里每个玩家的语音，分别保存成一条独立音轨。**

VoxTrace 在协议层按发送者拆包：抓取并解密 VRChat 的 Photon 语音流量，实时为
**每一位玩家**写出一条独立 WAV。不是混音，不是 AI 分离，也不需要事后处理。

> 仅供研究与互操作性分析。使用前请阅读 [DISCLAIMER.md](DISCLAIMER.md)；完整安装与
> 使用说明见 [docs/MANUAL.md](docs/MANUAL.md)。

---

## 它和“录音 / 混音”有什么不同

| | 普通录音 / 混音 | VoxTrace |
|---|---|---|
| 输出 | 一条混合音轨 | **每位玩家一条独立音轨** |
| 分离方式 | 事后无法拆分 | 协议层按发送者直接拆包 |
| 依赖 | 声卡回放质量 | 原始 Opus 码流解码 |
| 实时性 | 录完才有 | 边说边写，GUI 实时显示谁在说话 |
| 额外处理 | 需要人工剪辑 | 文件已按 `usr_` 身份命名 |

VoxTrace 不做“音乐/人声分离”，也不使用任何 AI 模型；它只是把 VRChat 本来按玩家
分开传输的语音包，分别解码成文件。

## 核心能力

- **一人一轨**：`player_usr_<uuid>_s1.wav`，按玩家身份而不是座位号命名
- **实时写入**：语音到达即解码追加，说话中的玩家在列表里高亮
- **跨房间识别**：同一 `usr_` 在不同世界仍对应同一人，`_sN` 区分不同房间
- **本机麦克风单独成轨**：自己的语音不会和别人混在一起
- **自动名字**：从 Event 253 解析 `actor → usr_ → displayName`
- **一键流程**：启动监听 → 进世界 → 自动取密钥 → 自动分轨 → 自动名字
- **会话清单**：`session.json` / `live_stats.json` 记录房间、玩家、时长
- **导出**：一键把当前会话打包成 zip
- **命令行**：`live` / `names` / `key` 可脚本化

## 工作原理

```
VRChat ──UDP──▶ Wintun 中继 ──▶ 抓包（密文）
                                   │
                    pagefile 提取 32 字节会话密钥
                                   │
                    解密 Photon v5（AES-256-GCM）
                                   │
                    重组分片 → Event 1 → USpeak
                                   │
                    按发送者解码 Opus（48 kHz）
                                   │
              player_usr_<id>.wav   每人一条
```

1. Wintun 虚拟网卡复制发往 Photon 游戏服务器的 UDP，并原样转发
2. 通过 `EmptyWorkingSet` + 修改页列表刷新，从 pagefile 恢复会话密钥（不读取游戏内存）
3. 解密数据报，解析 Photon 命令帧与 USpeak 分包
4. 按发送者/身份增量写入 WAV；Event 253 提供玩家名映射

细节见 [docs/PROTOCOL.md](docs/PROTOCOL.md) 与
[docs/KEY_EXTRACTION.md](docs/KEY_EXTRACTION.md)。

## 快速开始

从 [Releases](https://github.com/Leetfs/VoxTrace/releases) 下载 Windows 包，或从源码运行：

```text
1. pip install -r requirements.txt
2. 复制 config.example.json -> config.json，填写 vrchat_dir
3. 把中继程序放到 bin\（见 docs/BUILD.md）
4. 右键 bin\VoxTrace.exe → 以管理员身份运行
5. 点「一键开始」，启动 VRChat 并进入世界
```

首次使用前必须关闭 Windows 内存压缩：

```powershell
Disable-MMAgent -MemoryCompression
# 重启后确认任务管理器里没有 “Memory Compression” 进程
```

## 输出结构

```
captures\
  voice_HHMMSS.txt                 原始抓包
  live_HHMMSS\
    player_usr_<uuid>_s1.wav       每位玩家一条独立音轨
    player_actor<N>_s1.wav         未识别身份时按 actor 命名
    player_self_s1.wav             本机麦克风（可选）
    names.json                     名字映射
    live_stats.json                实时统计
    session.json                   会话清单（不含密钥）
```

## 图形界面

- **一键开始**：启动监听 → 进入世界后自动取密钥 → 自动分轨 → 自动名字
- **加载会话文件夹**：回放/检查已有的 `player_*.wav`
- **导入日志名字**：从 VRChat `output_log` 导入候选名字
- **导出音轨**：把当前会话目录打包成 zip
- 双击玩家行可手动绑定名字（手动绑定优先于自动映射）

## 命令行

```bash
python -m voxtrace live   --capture captures/voice.txt --key <hex> --out captures/live
python -m voxtrace names  --capture captures/voice.txt --key <hex> --out captures/live/names.json
python -m voxtrace key    --capture captures/voice.txt          # 需要管理员
python -m voxtrace locate --file pagefile.sys --drive C:       # 需要管理员
```

## 配置（config.json）

| 键 | 默认 | 说明 |
|---|---|---|
| `vrchat_dir` | 空 | VRChat 安装目录（用于定位 `opus.dll`） |
| `opus_dll` | 空 | 直接指定 opus 库路径（优先于 `vrchat_dir`） |
| `relay_exe` | `bin/VrcWintunPoc.exe` | 中继程序 |
| `wintun_dll` | `bin/wintun.dll` | Wintun 驱动 |
| `python` | 空 | Python 解释器路径（空则自动查找） |
| `captures_dir` | `captures` | 抓包/音轨输出目录 |
| `eventparse_exe` | `bin/eventparse/eventparse.exe` | 名字解析辅助程序（可选） |
| `fastscan_exe` | `bin/FastScan.exe` | 高速 pagefile 扫描器 |
| `dotnet_root` | 空 | 便携版 .NET 根目录（可选） |
| `pagefile_drive` | `C:` | pagefile 所在卷 |
| `relay_prefix` | `82.117.224.0/24` | 中继路由前缀 |
| `relay_port` | `27002` | 抓取的 UDP 端口 |
| `scan_workers` | `4` | Python 兜底扫描的进程数（正常路径不走它） |
| `fastscan_threads` | `4` | FastScan 直接读卷时的线程数 |
| `settle_seconds` | `2` | 无法查询修改页列表时的固定等待 |
| `drain_timeout` | `10` | 修改页列表排空的最长等待（慢盘可调大） |

## 环境要求

- Windows 10/11 x64
- Python 3.9+（`pycryptodome`）
- .NET Framework 4.x（GUI 与中继；系统自带）
- 可选：.NET SDK 8 + Photon SDK（构建名字辅助程序）
- 管理员权限（Wintun 网卡 + 原始卷读取）
- 已关闭 Windows 内存压缩

## 适用场景

- 语音/网络协议调试，检查每位玩家的实际码流
- 为听障玩家生成按人分离的字幕或转写素材
- 内容创作：后期单独调整某位玩家的音量或静音
- Photon / USpeak / Opus 的互操作性研究

## 已知限制

- 仅 Windows；需要管理员权限
- 自动名字依赖 VRChat 的属性广播时序，个别玩家可能延迟或缺失
- 必须先关闭 Windows 内存压缩（见 KEY_EXTRACTION）
- 不支持 TLS 侧（WSS）流量解密；语音与房间数据走 UDP
- 切换世界时按 `_sN` 分段，不会自动合并为单文件
- 长时间挂机需要手动轮转抓包文件
- 与 UU 等加速器默认会争抢同一段路由，见 MANUAL 说明

## 许可证

源代码使用 MIT 许可证，见 [LICENSE](LICENSE)。第三方组件（Opus、Wintun、
Photon SDK、.NET）不随仓库分发，见 [THIRD_PARTY.md](THIRD_PARTY.md)。
