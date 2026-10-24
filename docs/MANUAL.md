# VoxTrace 使用说明书

版本：0.1.2  
适用平台：Windows 10 / 11 x64

> 使用前请阅读 [DISCLAIMER.md](../DISCLAIMER.md)。本工具会录制他人语音、读取系统
> pagefile，并可能违反 VRChat 服务条款。

---

## 1. 这是什么

VoxTrace 从 VRChat 的 Photon 语音流量中提取每位玩家的独立音轨：

- 通过 Wintun 虚拟网卡中继并复制游戏 UDP 流量
- 从 pagefile 中恢复本次会话的 32 字节解密密钥
- 解密 Photon v5 数据报，解析 USpeak / Opus 语音包
- 按玩家身份写出独立 WAV，并实时显示在图形界面中

它不读取游戏进程内存、不注入、不修改游戏文件。密钥提取依赖 Windows 内存管理
行为（`EmptyWorkingSet` + 修改页列表刷新）。

---

## 2. 系统要求

| 项目 | 要求 |
|---|---|
| 系统 | Windows 10 / 11 x64 |
| 权限 | 管理员（Wintun 网卡 + 原始卷读取） |
| Python | 3.9 及以上，安装 `pycryptodome` |
| .NET | .NET Framework 4.x（系统自带，用于 GUI） |
| 磁盘 | 建议 SSD / NVMe（pagefile 扫描速度） |
| 内存压缩 | 必须关闭（见 4.3） |
| 中继 | `VrcWintunPoc.exe` + `wintun.dll`（见 4.2） |

---

## 3. 安装

### 3.1 使用发布包（推荐）

1. 从 GitHub Releases 下载 `VoxTrace-v0.1.0-win64.zip`
2. 解压到任意目录，例如 `D:\VoxTrace`
3. 安装 Python 依赖：

```powershell
python -m pip install -r requirements.txt
```

4. 复制配置模板：

```powershell
Copy-Item config.example.json config.json
```

5. 按第 4 节填写 `config.json`、准备中继
6. 右键 `bin\VoxTrace.exe` → **以管理员身份运行**

### 3.2 从源码运行

```powershell
git clone https://github.com/Leetfs/VoxTrace.git
cd VoxTrace
python -m pip install -r requirements.txt
scripts\build_gui.cmd
scripts\build_fastscan.cmd
```

然后按第 4 节配置，运行 `bin\VoxTrace.exe`。

---

## 4. 首次配置

### 4.1 config.json

```json
{
  "vrchat_dir": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRChat",
  "opus_dll": "",
  "relay_exe": "bin\\VrcWintunPoc.exe",
  "wintun_dll": "bin\\wintun.dll",
  "python": "",
  "captures_dir": "captures",
  "eventparse_exe": "bin\\eventparse\\eventparse.exe",
  "fastscan_exe": "bin\\FastScan.exe",
  "dotnet_root": "",
  "pagefile_drive": "C:",
  "relay_prefix": "82.117.224.0/24",
  "relay_port": 27002,
  "scan_workers": 4,
  "fastscan_threads": 4,
  "settle_seconds": 2,
  "drain_timeout": 10
}
```

| 键 | 说明 |
|---|---|
| `vrchat_dir` | VRChat 安装目录，用于定位 `opus.dll` |
| `opus_dll` | 也可直接指定 opus 库路径 |
| `relay_exe` | 中继程序路径（相对项目根目录） |
| `wintun_dll` | Wintun 驱动路径 |
| `python` | Python 解释器路径，留空自动查找 |
| `captures_dir` | 抓包和音轨输出目录 |
| `eventparse_exe` | 可选的名字解析辅助程序 |
| `fastscan_exe` | 高速扫描器路径 |
| `pagefile_drive` | pagefile 所在盘符 |
| `relay_prefix` / `relay_port` | 中继接管的路由前缀（可逗号分隔多个）与端口 |
| `scan_workers` | Python 兜底扫描并发数 |
| `fastscan_threads` | FastScan 直接读卷的线程数 |
| `settle_seconds` | 无法查询修改页列表时的固定等待 |
| `drain_timeout` | 修改页列表排空的最长等待秒数 |
| `gap_fill` | 缺口填充：`silence`（默认）或 `plc` |

> `config.json` 已被 `.gitignore` 排除，不会被提交。

### 4.2 准备中继

中继需要两个文件：

```
bin\VrcWintunPoc.exe
bin\VrcWintunPoc.dll
bin\VrcWintunPoc.runtimeconfig.json
bin\VrcWintunPoc.deps.json
bin\wintun.dll
```

`relay/` 目录包含中继源码；`wintun.dll` 需自行从 WireGuard/Wintun 项目获取并遵守
其 GPL-2.0 许可证。仓库不附带该二进制。

### 4.3 关闭内存压缩（重要）

Windows 默认会把换出的页压缩后留在内存，导致 pagefile 中没有密钥。

```powershell
Disable-MMAgent -MemoryCompression
```

重启后确认任务管理器中不再有 “Memory Compression” 进程。

### 4.4 可选：玩家名辅助程序

自动名字依赖 `gui/eventparse`，它需要 Photon .NET SDK 的 `PhotonClient.dll`：

```powershell
$env:PHOTON_CLIENT_DLL = "C:\path\to\PhotonClient.dll"
scripts\build_eventparse.cmd
```

没有该程序时，GUI 仍可手动双击绑定名字。

---

## 5. 图形界面

### 5.1 一键流程

1. 以管理员身份运行 `bin\VoxTrace.exe`
2. 点击 **一键开始**
3. 启动 VRChat 并进入世界
4. 日志出现 `Finished entering world` 后自动取密钥
5. 状态变为「密钥=… 分轨=运行中」后，玩家列表开始出现音轨

### 5.2 界面功能

| 功能 | 说明 |
|---|---|
| 玩家列表 | 显示玩家、时长、说话状态（说话行变绿） |
| 播放 / 暂停 | 播放选中玩家的 WAV |
| 双击行 | 手动绑定名字（优先于自动映射） |
| 加载会话文件夹 | 打开已有的 `live_*` 目录回放 |
| 导入日志名字 | 从 VRChat 输出日志导入候选名字 |
| 保存绑定 | 保存手动名字到 `names.ini` |
| 导出音轨 | 把当前会话目录打包为 zip |
| 停止监听 | 停止中继（VRChat 会断线） |

### 5.3 输出文件

```
captures\
  voice_HHMMSS.txt          抓包
  live_HHMMSS\
    player_usr_<uuid>_s1.wav  按身份命名的音轨
    player_actor<N>_s1.wav    未识别身份时按 actor 命名
    names.json                名字映射
    live_stats.json           实时统计
    session.json              会话清单（不含密钥）
```

---

## 6. 命令行

```powershell
python -m voxtrace live  --capture captures\voice.txt --key <hex> --out captures\live
python -m voxtrace names --capture captures\voice.txt --key <hex> --out captures\live\names.json
python -m voxtrace key   --capture captures\voice.txt
python -m voxtrace locate --file pagefile.sys --drive C:
python -m voxtrace bench --drive C: --mb 512
```

| 命令 | 作用 |
|---|---|
| `live` | 解码语音并写 WAV |
| `names` | 增量解析 Event 253 名字 |
| `key` | trim + 刷新修改页 + 扫描 pagefile 取密钥 |
| `locate` | 打印系统文件的 NTFS 区段 |
| `bench` | 测量原始卷读取速度（只读） |

---

## 7. 工作原理（简述）

1. 中继把发往 Photon 游戏服务器的 UDP 复制到抓包文件并原样转发
2. `EmptyWorkingSet` 把 VRChat 的脏页换出，`NtSetSystemInformation` 强制写回
   pagefile
3. 解析 NTFS MFT 定位 `pagefile.sys` 区段，扫描 32 字节密钥并用 AES-GCM 校验
4. 解密 Photon v5 数据报 → 重组分片 → Event 1 → USpeak → Opus
5. 按 `usr_` 身份写 WAV，世界切换时按 `_sN` 分段

细节见 [PROTOCOL.md](PROTOCOL.md) 与 [KEY_EXTRACTION.md](KEY_EXTRACTION.md)。

---

## 8. 性能调优

| 现象 | 调整 |
|---|---|
| 取密钥慢 | 确认 `fastscan_exe` 存在；`fastscan_threads` 可加到 8 |
| 第一次未命中 | 保持默认双 trim；确认内存压缩已关闭 |
| 慢盘 / 机械盘 | 调大 `drain_timeout`（如 30–60） |
| 不想频繁 trim | 无法关闭，轮次逻辑保证最多 3 轮 |

正常 NVMe 上，进世界后约 **3–6 秒**完成取密钥与首条音轨。

---

## 8.1 多区域服务器

默认只接管日本区：

```json
"relay_prefix": "82.117.224.0/24"
```

如果 VRChat 选择美区/欧区，需要把对应 Photon 网段一起写入（逗号分隔）：

```json
"relay_prefix": "82.117.224.0/24,203.0.113.0/24,198.51.100.0/24"
```

获取某个区域的网段：

1. 在游戏内切到目标区域并进入世界
2. 管理员 PowerShell 抓 10 秒包：

```powershell
pktmon start --capture --pkt-size 128
Start-Sleep -Seconds 10
pktmon stop
pktmon format PktMon.etl -o pm.txt
Select-String pm.txt -Pattern "27002" | Select-Object -First 20
```

3. 记录远端 IP，取它所在的 /24
4. 写入 `config.json` 的 `relay_prefix`，重启中继

每个网段都会被指向 VRCProxy（metric 1）；如果 UU 也路由同一段，我们的路由优先。
停止中继时只会删除 VRCProxy 上的路由，不会动 UU 的。

## 8.2 与 UU 加速器共存

UU 的「路由模式」和 VoxTrace 中继都用 Wintun + 路由，可以串接：

```
VRChat → VRCProxy（我们）→ UU 隧道 → UU 服务器
```

前提是**先开 UU、再点一键开始**。中继启动时会用 `GetBestInterface` 选择出口，
UU 已在运行时出口就是 UU 网卡；如果顺序反过来，中继会锁定物理网卡，语音不再
经过 UU 加速（抓包仍然正常）。

验证方法：中继日志出现 `physical ifIndex=<UU 网卡的 ifIndex>` 即表示走 UU。

## 9. 常见问题

**Q：一直显示「等待 VRChat 进入世界（监听日志）」**  
确认 VRChat 日志目录存在：
`%USERPROFILE%\AppData\LocalLow\VRChat\VRChat\output_log_*.txt`。

**Q：提示“游戏仍在加载”或长时间没有密钥**  
确认以管理员运行、内存压缩已关闭；查看 `bin\_pipeline.log` 的 `drain:` 行。

**Q：播放时提示 MCI 错误 304**  
该玩家可能还没有音频，或文件正在写入。程序会自动复制临时文件重试；仍失败请
稍后再点。

**Q：没有玩家名字**  
自动名字依赖 `eventparse` 辅助程序；没有时可双击手动绑定。

**Q：可以和 UU 加速器一起用吗**  
默认会抢同一段路由，可能抓不到流量。见 README 的“与加速器共存”说明。

**Q：会被 EAC 封号吗**  
不能保证。工具不读进程内存、不注入，但流量拦截与录制可能违反 ToS，建议使用
小号并自行评估风险。

**Q：停止监听后 VRChat 断线**  
中继是 VRChat 网络路径的一部分，停止中继会断开连接，属预期行为。

---

## 10. 隐私与合规

- 录制他人语音可能受当地法律约束，请先获得同意
- 抓包与音轨包含玩家名、`usr_` 标识，属于个人信息
- 不要公开传播他人音轨
- 使用即表示接受 [DISCLAIMER.md](../DISCLAIMER.md)

---

## 11. 卸载

1. 停止监听并退出程序
2. 删除程序目录（含 `captures\`）
3. 如需恢复内存压缩：

```powershell
Enable-MMAgent -MemoryCompression
```

4. 删除中继虚拟网卡（设备管理器 → 网络适配器 → Wintun）

---

## 12. 构建与发版

见 [BUILD.md](BUILD.md)。推送 `v*` 标签会触发 GitHub Actions 自动构建并发布
源码包与 Windows 包。
