# 协议笔记

以下内容来自对实际流量的观察与公开资料交叉验证，仅覆盖本项目用到的部分。

## 1. Photon v5 UDP 数据报

```
[peerID:2][0x01][challenge:4][nonce:12][ciphertext][tag:16]
```

- 加密：AES-256-GCM
- 密钥：32 字节，客户端每场会话随机生成；同一会话的多个连接共用
- nonce：密文前 12 字节（即 `nonce` 字段）
- AAD：前 7 字节头 + nonce
- 开销：明文长度 + 28

## 2. Photon 消息

明文结构：

```
[command_count:1][timestamp:4][command...]
command: [type:1][channel:1][flags:1][reserved:1][length:4 BE][seq:4 BE][payload]
```

- `type = 8`：分片（payload 头部 5 个 uint32 BE：startSeq、fragCount、fragIndex、totalLength、fragOffset）
- 其余为可靠/不可靠命令，payload 内可能包含 `F3 <msgType>` 消息

消息头 `F3 <type>`：

- `type = 2`：Operation Request
- `type = 3`：Operation Response
- `type = 4`：Event

Event 参数表：`count:1`，随后 `[paramCode:1][type:1][value]`。

常用类型：

| 编码 | 含义 |
|---|---|
| `0x43` | byte[]（长度 LEB128） |
| `0x0D` | int16 LE |
| `0x0B` | byte |
| `0x08` | null |
| `0xFE` (254) | SenderKey（值为 actor number） |

## 3. 语音：Event 1（USpeak）

```
param 245 (byte[]):
  [4 bytes header]
  [uint16 index][uint16 size][opus payload] ...
```

- 每个 USpeak 包 20 ms
- Opus：48 kHz、立体声
- `index` 用于排序/去重；跳号时可用静音或 Opus PLC 补齐
- 客户端发出的包（C 方向）是本机麦克风，服务端下发（S 方向）是其他玩家

## 4. 玩家名：Event 253

```
param 251 = {
   <actor>: {
      "user": { "id": "usr_...", "displayName": "...", ... },
      ...
   },
   ...
}
```

- 这是 Protocol18 的嵌套字典，需要按类型递归解析
- 本项目用可选的 C# 辅助程序（Photon SDK）解析；缺失时退化为手动绑定
- 属性是**陆续广播**的，进入房间后需要等待

## 5. 房间标识

客户端 Operation Request（`F3 02`，opcode 226 Join）中包含：

```
wrld_<uuid>:<instanceId>~...
```

用于切换世界时分段（`_s2`、`_s3`…）。

## 参考

- [VRC-PhotonDocs/Voice](https://github.com/OptoCloud/VRC-PhotonDocs/blob/master/Voice/README.md)
- Photon Protocol18 / USpeak 公开实现
