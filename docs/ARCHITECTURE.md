# 架构

```
VoxTrace/
├─ voxtrace/            Python 包
│  ├─ config.py         配置与路径解析
│  ├─ crypto.py         Photon v5 AES-GCM 解密
│  ├─ uspeak.py         Photon 消息 / 分片 / USpeak 解析
│  ├─ opuslib.py        libopus 绑定（解码 + PLC）
│  ├─ wavio.py          增量 WAV 写入
│  ├─ live.py           实时提取主循环
│  ├─ names.py          Event 253 增量名字解析
│  ├─ pagefile.py       NTFS 解析 + 原始卷扫描
│  ├─ keytool.py        清工作集 + 提取会话密钥
│  └─ __main__.py       命令行入口
├─ gui/                 WinForms 图形界面（C#）
│  ├─ VoxTrace.cs
│  └─ eventparse/       可选的 Protocol18 解析辅助程序
├─ relay/               Wintun 中继源码
├─ scripts/             构建与运行脚本
├─ docs/                协议、密钥、构建文档
└─ bin/                 构建产物（不入库）
```

## 数据流

```
VRChat ──UDP──▶ Wintun 中继 ──▶ captures/voice_*.txt
                                      │
                     crypto.decrypt_datagram
                                      │
                          uspeak.iter_messages（分片重组）
                                      │
                          Event 1 → uspeak_packets
                                      │
                        opuslib.Decoder（48k 立体声→单声道）
                                      │
                     live.Stream（按身份/房间分段，index 补静音）
                                      │
                          player_<usr|actor>[_sN].wav
```

## 密钥路径

```
keytool.extract()
  ├─ EmptyWorkingSet(VRChat)      # 仅 PROCESS_SET_QUOTA
  ├─ 等待脏页写回 pagefile
  ├─ pagefile.locate()            # NTFS MFT → 数据段
  └─ pagefile.scan()              # 特征扫描 + GCM 校验
```

## 身份与分段

- 语音事件只携带 actor number（房间内有效）
- Event 253 提供 `actor → usr_ → displayName`
- `live.py` 优先使用 `usr_` 命名文件；未知时退回 `actorN`
- 客户端 Join 请求中的 `wrld_...:instance` 变化时递增分段，文件名加 `_sN`

## GUI

- 通过 `python -m voxtrace ...` 调用各子命令
- 读取 `live_stats.json` 更新列表（不重复读 WAV）
- 读取 `names.json` 自动填名，手动绑定写入 `names.ini` 并优先
- 中继进程与 Python 子进程由 GUI 管理
