# 构建与部署

## 1. Python 环境

```powershell
python -m pip install -r requirements.txt
```

## 2. 配置

```powershell
Copy-Item config.example.json config.json
```

编辑 `config.json`：

- `vrchat_dir`：VRChat 安装目录（默认 Steam 路径），用于定位 `opus.dll`
- `python`：可留空，自动从 PATH 查找
- `eventparse_exe`：可选，名字辅助程序
- `dotnet_root`：若使用便携版 .NET，填写其根目录

## 3. 构建 GUI

无需 .NET SDK，使用 Windows 自带的 .NET Framework 编译器：

```powershell
scripts\build_gui.cmd
```

产物：`bin\VoxTrace.exe`。

## 4. 中继（Wintun）

`relay/` 下是中继源码。构建需要：

- .NET Framework 4.x（`csc.exe`）或 .NET SDK
- `wintun.dll`（WireGuard 项目，GPL-2.0）

把 `VrcWintunPoc.exe` 与 `wintun.dll` 放入 `bin/`。

也可以直接使用已有的中继构建产物，只要 `config.json` 的 `relay_exe`
指向它即可。

## 4.1 中继多网段

中继支持逗号分隔的多个网段：

```powershell
bin\VrcWintunPoc.exe --prefix "82.117.224.0/24,203.0.113.0/24" --port 27002 --dump captures\voice.txt
```

GUI 会读取 `config.json` 的 `relay_prefix` 并原样传入。

## 5. 名字辅助程序（可选）

自动名字依赖 Protocol18 嵌套字典解析：

```powershell
scripts\build_eventparse.cmd
```

要求 .NET SDK 8，并且能访问 NuGet（包 `Photon.Client`）。
产物在 `bin\eventparse\`。

若使用便携版 .NET，设置环境变量 `DOTNET_ROOT` 或在 `config.json` 的
`dotnet_root` 中指定路径。

没有该辅助程序时，GUI 仍可手动双击绑定名字。

## 6. 运行

```powershell
# 以管理员身份运行
bin\VoxTrace.exe
```

或命令行：

```powershell
python -m voxtrace live --capture captures\voice.txt --key <hex> --out captures\live
```

## 7. 目录约定

```
config.json          本地配置（不入库）
captures/            抓包与音轨输出（不入库）
bin/                 构建产物（不入库）
```
- 把 `VrcWintunPoc.exe` 与 `wintun.dll` 放入 `bin/`。
+ 把中继发布目录的**全部文件**复制到 `bin/`：
+ `VrcWintunPoc.exe`、`VrcWintunPoc.dll`、`VrcWintunPoc.runtimeconfig.json`、
+ `VrcWintunPoc.deps.json`、`wintun.dll`。
+
+> 只复制 exe 会报 “does not exist: VrcWintunPoc.dll”，因为 exe 只是 .NET 启动器。
