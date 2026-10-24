# VoxTrace

[![CI](https://github.com/Leetfs/VoxTrace/actions/workflows/ci.yml/badge.svg)](https://github.com/Leetfs/VoxTrace/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Leetfs/VoxTrace)](https://github.com/Leetfs/VoxTrace/releases)

**VRChat 语音抓取与分离工具（研究 / 互操作性用途）**

> 完整安装与使用说明见 [docs/MANUAL.md](docs/MANUAL.md)。

VoxTrace captures VRChat's Photon voice traffic, recovers the per-session datagram
key, decodes USpeak/Opus audio and writes one WAV file per speaker.  It ships a
small WinForms GUI and a Python package.

> 仅供研究与互操作性分析。使用前请阅读 [DISCLAIMER.md](DISCLAIMER.md)。

---

## 功能

| 功能 | 说明 |
|---|---|
| 实时抓包 | 通过 Wintun 虚拟网卡中继 Photon 游戏服务器流量 |
| 会话密钥提取 | 利用 `EmptyWorkingSet` + pagefile 原始卷扫描，不读取进程内存 |
| 语音解码 | Photon Event 1 / USpeak / Opus 48 kHz 立体声 → 单声道 WAV |
| 按玩家分离 | 按 `usr_` 身份归档，同一玩家跨房间可对应；`_sN` 区分房间 |
| 时间轴补齐 | 按 USpeak index 插入静音 / Opus PLC，录音更接近真实时间 |
| 玩家名 | 从 Event 253 解析 `actor → usr_ → displayName`，自动填充 |
| 图形界面 | 音轨列表、波形、播放、名字绑定、实时高亮、导出 zip |
| 会话清单 | `session.json`、`live_stats.json` 记录房间、玩家、时长 |

## 环境要求

- Windows 10/11 x64
- Python 3.9+（`pycryptodome`）
- .NET Framework 4.x（GUI 与中继；系统自带）
- 可选：.NET SDK 8（构建名字辅助程序）
- 管理员权限（Wintun 网卡 + 原始卷读取）

## 快速开始

从 [Releases](https://github.com/Leetfs/VoxTrace/releases) 下载 Windows 包，或：

```text
1. pip install -r requirements.txt
2. 复制 config.example.json -> config.json，填写 vrchat_dir
3. 把中继程序放到 bin\（见 docs/BUILD.md）
4. 双击 bin\VoxTrace.exe（右键以管理员身份运行）
5. 点「一键开始」，启动 VRChat 并进入世界
```

### 图形界面

- **一键开始**：启动监听 → 进入世界后自动取密钥 → 自动分轨 → 自动名字
- **加载会话文件夹**：回放/检查已有的 `player_*.wav`
- **导入日志名字**：从 VRChat `output_log` 导入候选名字
- **导出音轨**：把当前会话目录打包成 zip
- 双击玩家行可手动绑定名字（手动绑定优先于自动映射）

### 命令行

```bash
python -m voxtrace live  --capture captures/voice.txt --key <hex> --out captures/live
python -m voxtrace names --capture captures/voice.txt --key <hex> --out captures/live/names.json
python -m voxtrace key   --capture captures/voice.txt          # 需要管理员
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
| `pagefile_drive` | `C:` | pagefile 所在卷 |
| `relay_prefix` | `82.117.224.0/24` | 中继路由前缀 |
| `relay_port` | `27002` | 抓取的 UDP 端口 |

## 工作原理（简述）

1. Wintun 中继把发往 Photon 游戏服务器的 UDP 复制成抓包文件；
2. 用 pagefile 提取的 32 字节密钥解密 Photon v5 AES-256-GCM 数据报；
3. 解析 Photon 命令帧 → Event 1 → USpeak 分包 → Opus 解码；
4. 按发送者/身份写入增量 WAV；
5. Event 253 提供 `actor → usr_ → 显示名` 映射。

细节见 [docs/PROTOCOL.md](docs/PROTOCOL.md) 与
[docs/KEY_EXTRACTION.md](docs/KEY_EXTRACTION.md)。

## 已知限制

- 仅 Windows；需要管理员权限
- 自动名字依赖 VRChat 的属性广播时序，个别玩家可能延迟或缺失
- 必须先关闭 Windows 内存压缩（见 KEY_EXTRACTION）
- 不支持 TLS 侧（WSS）流量解密；语音与房间数据走 UDP
- 切换世界时按 `_sN` 分段，不会自动合并为单文件
- 长时间挂机需要手动轮转抓包文件

## 许可证

本项目源代码使用 MIT 许可证，见 [LICENSE](LICENSE)。
第三方组件（Opus、Wintun、Photon SDK、.NET）不随仓库分发，见
[THIRD_PARTY.md](THIRD_PARTY.md)。
| `scan_workers` | `4` | Python 兜底扫描的进程数（正常路径不走它） |
| `fastscan_threads` | `4` | FastScan 直接读卷时的线程数 |
| `settle_seconds` | `2` | 仅在无法查询修改页列表时的固定等待 |
| `drain_timeout` | `10` | 修改页列表排空的最长等待（慢盘可调大） |
| `fastscan_exe` | `bin/FastScan.exe` | 高速扫描器路径 |
