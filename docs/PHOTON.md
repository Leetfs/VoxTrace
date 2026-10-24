# Photon 层解析

本文档记录 VoxTrace 对 VRChat Photon v5 流量的解析结果：帧格式、操作码、事件码、
已确认字段，以及仍未解析的部分。

> **警告**
> 解密后的抓包包含认证 token、`usr_` 用户 ID、显示名、avatar 链接、社交资料和
> 会话密钥。`JoinRoom` 的参数里甚至能看到 JWE 形式的认证串。不要把原始
> `voice_*.txt`、`photon.jsonl` 或密钥发给别人。

---

## 1. 分层

```
UDP 27002
  └─ Photon v5 数据报：peer:2 + 0x01 + challenge:4 + nonce:12 + ciphertext + tag:16
       └─ AES-256-GCM 解密
            └─ Photon 命令流（count + 12 字节命令头 + payload）
                 └─ 分片重组（命令类型 8）
                      └─ 消息：F3 <type> ...
                           ├─ type 2：OperationRequest
                           ├─ type 3：OperationResponse
                           └─ type 4：Event
```

- 密钥每次游戏启动生成，跨世界不变。
- 命令头里包含类型、通道、长度；长度是 4 字节大端。
- 命令类型 8 是分片，按 `start/frag_count/frag_num/total/offset` 重组。

## 2. 消息格式

```
F3 <mtype> ...
```

| mtype | 含义 | 后续字段 |
|---|---|---|
| `2` | OperationRequest | opcode:1 + 参数表 |
| `3` | OperationResponse | opcode:1 + returnCode:2 + debug:string + 参数表 |
| `4` | Event | eventCode:1 + paramCount:1 + 参数表 |

参数表每项：

```
paramCode:1  type:1  value
```

已确认的类型标记：

| type | 含义 | 编码 |
|---|---|---|
| `0x08` | null | 无 |
| `0x0B` | byte | 1 字节 |
| `0x0D` | short | 2 字节小端 |
| `0x43` | byte[] | varint 长度 + 数据 |

其余类型由 Photon `Protocol18` 处理（字典、数组、float、string 等）。

## 3. OpCode

来自 VRChat 社区文档与实测：

| Code | 名称 | 说明 |
|---|---|---|
| 217 | GetRoomList | |
| 219 | WebRPC | |
| 220 | GetRegions | |
| 221 | GetLobbyStats | |
| 222 | FindFriends | |
| 225 | JoinRandomRoom | |
| 226 | JoinRoom | 携带世界/实例、用户设置、认证串 |
| 227 | CreateRoom | |
| 228 | LeaveLobby | |
| 229 | JoinLobby | |
| 230 | Authenticate | |
| 231 | AuthenticateOnce | 实测返回 `ret`/`debug` |
| 251 | GetProperties | |
| 252 | SetProperties | |
| 253 | RaiseEvent | 客户端发事件 |
| 254 | Leave | |
| 255 | Join | |

### JoinRoom（226）实测参数

| 参数 | 内容 |
|---|---|
| `255` | 世界/实例字符串，例如 `wrld_...:26691~private(usr_...)~region(us)` |
| `215` | 小整数 |
| `249` | 用户设置字典：`inVRMode`、`avatarEyeHeight`、`avatarHipHeight`、`groupOnNameplate`、`showSocialRank`、`useImpostorAsFallback` 等 |
| `248` | 认证/会话字典，包含 `eyJ...` 形式的 JWE 串与布尔标志 |
| `250` | 布尔 |

> `248` 里的 JWE 是敏感凭据，文档不记录具体值。

## 4. Event

社区文档给出的 VRChat 事件表：

| Code | 名称 | 内容 |
|---|---|---|
| 1 | Voice | USpeak + Opus |
| 2 | ExecutiveMessage | 后端告警 |
| 3 | PastEvents | 请求历史事件 |
| 4 | SyncEvents | 历史事件响应 |
| 5 | SyncFinished | 历史同步结束 |
| 6 | ProcessEvent | VrcEvents / RPC |
| 7 | Unreliable Serialization | 玩家物理（位置/旋转）、相机、Muscles、Ping |
| 8 | InterestManagement | 关注列表 |
| 9 | Reliable Serialization | AV3 参数、Udon Sync、迟到加入者位置 |
| 33 | ExecutiveAction |  moderation |
| 34 | RatelimitValueSync | 限速值 |
| 35 | RatelimitUpdate | 限速 tick |
| 40/42 | UserRecordUpdate | 用户资料更新 |
| 60 | PhysBonesPermissions | PhysBones 权限 |
| 202 | Instantiate | 实例化 |
| 209/210/211 | Ownership | 所有权 |
| 223 | Photon AuthEvent | 认证 |

实测还出现以下事件码（公开文档未列出，按载荷推断）：

| Code | 实测内容 |
|---|---|
| 253 | 参数 `251` 是 actor → 用户/avatar 的属性字典 |
| 254 | 参数 `203`，疑似离开/销毁 |
| 255 | 参数 `249` 用户+avatar 字典，参数 `252` 数组 |

### Event 253（玩家属性）

参数 `251` 是嵌套字典：

```
actorNr -> {
  showSocialRank, avatarEyeHeight,
  isInstanceCreator, canModerateInstance,
  platform, inVRMode, groupOnNameplate,
  user: {
    id, displayName, developerType,
    currentAvatarImageUrl, currentAvatarThumbnailImageUrl,
    last_platform, allowAvatarCopying,
    pronouns, status, statusDescription,
    ageVerified, ageVerificationStatus,
    iconUrl, iconFrame, nameplateEffect, profileEffect,
    bannerType, bannerColor, bannerUrl
  },
  avatarDict / favatarDict: {
    unityPackages: [{ assetUrl, unityVersion, unitySortNumber,
                      assetVersion, platform, variant,
                      scanStatus, performanceRating }],
    syncedExpressionParameterMap
  }
}
```

### Event 255（加入/离开）

参数 `249` 与 Event 253 的 `user/avatarDict` 结构一致，参数 `252` 是数组。
实测包含 `favatarDict` 和 `avatarDict` 两套 avatar 数据。

### Event 8（InterestManagement）

实测参数 `245` 是布尔值；文档描述接收方向是 `int[]` actor 列表，具体取决于方向。

### Event 6（ProcessEvent / RPC）

参数 `245` 是 byte[]，内部为 VRChat 自定义 RPC 数据；字符串可见
`DisableGravities`、`Switch_LiteMode` 等 avatar 参数名。

### Event 13 / 11 / 10 / 24 / 66 / 74

这些事件的 `245` 参数是自定义序列化 byte[]：

- 事件 13：高频（约 20 Hz），8 字节头 + `Protocol18` 数组；内容为 avatar 参数/状态
- 事件 11：含 `Switch_LiteMode` 等字符串和浮点数组
- 事件 10：短数组
- 事件 24：约 540 字节的二进制块
- 事件 66：24–522 字节，未分类
- 事件 74：包含 `unityPackages` 字段，疑似 avatar/资源信息

这些格式尚未公开，当前只保留原始 byte[]。

### Event 7 / 9（位置）

社区文档说明：

- Event 7 包含玩家物理位置、旋转、Flags，相机物理，Muscles，Ping/丢包
- Event 9 包含迟到加入者的位置同步、AV3 参数、Udon Sync

本次 31 MB 抓包中没有出现 Event 7/9，因为角色基本没有移动。要提取 XYZ，
需要在世界中持续走动后再抓一次。

## 5. 单次抓包统计

样本：`voice_234924.txt`，64939 行，52433 个数据报解密成功，8774 条消息。

| 事件 | 次数 |
|---|---|
| 13 | 6946 |
| 11 | 514 |
| 35 | 496 |
| 66 | 213 |
| 10 | 26 |
| 24 | 12 |
| 28 | 8 |
| 33 | 7 |
| 8 | 7 |
| 60 | 7 |
| 74 | 7 |
| 6 | 5 |
| 22 | 5 |
| 253 | 3 |
| 34 | 2 |
| 20 | 2 |
| 5 | 2 |
| 255 | 2 |
| 73 | 2 |
| 254 | 1 |
| 42 | 1 |

| 操作 | 次数 |
|---|---|
| 253 RaiseEvent | 471 |
| 226 JoinRoom | 3 |
| 231 AuthenticateOnce | 2 |
| 254 Leave | 1 |

## 6. 能还原出的信息

- 玩家：`usr_` ID、显示名、平台、VR/桌面、社交等级、年龄验证、pronouns、状态、简介
- Avatar：avatar ID、资源 URL、Unity 版本、平台、性能等级、扫描状态、参数映射
- 实例：世界/实例 ID、区域、房主、管理员权限、群组
- 行为：RPC 名称、avatar 参数变化、限速/审核事件
- 语音：Event 1 USpeak（本次抓包没有语音事件）
- 位置：Event 7/9（本次未触发，需移动后抓包）

## 7. 未解析 / 未知

- Event 7 / 9 的精确字段偏移（公开文档只给出内容类别）
- Event 13 / 11 / 10 / 24 / 66 / 74 的自定义序列化格式
- Photon `Protocol18` 的少数自定义类型
- Event 253 / 254 / 255 在公开文档中没有条目，当前按载荷推断
- `JoinRoom` 参数 `248` 的认证串用途

## 8. 工具

```powershell
# 解密整个抓包并输出 JSONL
python scripts\photon_dump.py captures\voice_234924.txt `
  --key <hex> --out captures\photon_234924
```

依赖：

- `bin\eventparse\eventparse.exe`（Photon SDK 构建，见 `docs/BUILD.md`）
- `pycryptodome`

输出：

```
captures\photon_234924\
  messages.bin
  photon.jsonl      每条消息的完整参数
  summary.json      事件/操作/字段统计
```

## 9. 隐私

解密后的数据包含：

- 认证 token（JoinRoom）
- 用户 ID、显示名、社交资料
- avatar 资源链接与参数
- 语音、位置（Event 7/9 可用时）

只能在自己拥有或获得同意的实例中使用，不要公开传播原始抓包。
