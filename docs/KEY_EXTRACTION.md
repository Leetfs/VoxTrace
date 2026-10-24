# 会话密钥提取（pagefile 路线）

## 背景

VRChat 的 Photon 数据报密钥只存在于游戏进程内存中。EAC 会在句柄创建时剥离
`PROCESS_VM_READ`，因此 `ReadProcessMemory`、`MiniDumpWriteDump`、
`PssCaptureSnapshot` 等用户态读内存手段都会失败。

## 思路

EAC 没有剥离 `PROCESS_SET_QUOTA`。`EmptyWorkingSet` 只需要该权限，它不读取
内存，只要求内核回收工作集：

1. 干净页进入待机列表；
2. 私有脏页由修改页写回线程写入 `pagefile.sys`；
3. 读取 pagefile 是**磁盘操作**，与进程句柄和 EAC 保护无关。

### 前置条件

Windows 10/11 默认开启内存压缩，被换出的页会压缩后留在内存，不会写盘。
必须先关闭（只需一次，重启后仍有效）：

```powershell
Disable-MMAgent -MemoryCompression
# 重启，确认 "Memory Compression" 进程消失
```

## 流程

```
1. 启动中继，进入世界，产生加密流量
2. OpenProcess(PROCESS_SET_QUOTA|PROCESS_QUERY_INFORMATION)
3. EmptyWorkingSet(handle)
4. 等待 3-5 秒（脏页落盘）
5. 解析 NTFS MFT，定位 pagefile.sys 的数据段
6. 原始卷读取各段，搜索 32 字节密钥特征
7. 用抓包中的加密包做 AES-GCM 校验
```

## 密钥特征

托管 `byte[32]` 在内存中的布局：

```
20 00 00 00 00 00 00 00  <32 字节密钥>
```

扫描时对每个候选做 GCM 校验，排除误报（实测 pagefile 中约有 1 万个候选窗口）。

## 实测证据（开发记录）

- 两次独立会话均在 pagefile 命中，解密 312/312、12,056/12,056 包
- 命中时 pagefile 3-7.6 GB，扫描耗时数秒
- 全程未崩溃、未挂起进程、未读取进程内存

## 限制

- 必须管理员运行（原始卷读取）
- 必须保持内存压缩关闭
- 世界加载期间 trim 会增加 Unity 崩溃风险；工具限制为进入世界 12 秒后、
  最多重试 6 次
- pagefile 所在卷必须是可解析的 NTFS
- 该方法不涉及 EAC 的内存保护，但没有任何账号安全保证
