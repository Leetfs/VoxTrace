# 第三方组件

本仓库**不包含**以下组件的二进制文件，使用者需自行获取并遵守各自的许可证。

| 组件 | 用途 | 许可证 / 来源 |
|---|---|---|
| **Opus** | 语音解码（`opus.dll`） | BSD 3-Clause；VRChat 客户端自带，配置 `vrchat_dir` 后自动定位 |
| **Wintun** | 虚拟网卡驱动（`wintun.dll`） | GPL-2.0（WireGuard 项目）；需自行获取 |
| **Photon .NET SDK** | 名字辅助程序 `eventparse` 的依赖 | Exit Games 许可；通过 NuGet 还原，不随仓库分发 |
| **.NET Framework / .NET 8** | GUI、中继、辅助程序运行时 | MIT（.NET Foundation） |
| **pycryptodome** | AES-GCM 解密 | BSD 2-Clause |

## 构建中继

`relay/` 目录包含中继源码，依赖 Wintun 与 .NET Framework。构建说明见
[BUILD.md](docs/BUILD.md)。

## 构建名字辅助程序

`gui/eventparse/` 依赖 NuGet 包 `Photon.Client`（Exit Games）。该包仅用于本地
解析，构建产物不随仓库发布。如不需要自动名字，可跳过。
