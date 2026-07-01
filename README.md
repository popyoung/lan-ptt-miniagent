# 局域网对讲机(LAN PTT Intercom)

一个轻量级的 Windows 局域网语音对讲程序,按住说话(PTT),和对讲机一样。

主要特性:

- 打开后**自动开始监听**,无需点击"开始监听"按钮。
- 按住主界面上的"按住说话"按钮,或者按住 **空格键**,开始说话;松开即停止。
- 自动加载上次保存的"默认" IP,但**不会自动开始发送**,只有按住说话时才会发送音频。
- 已保存的 IP 列表可添加 / 编辑 / 删除,每条可以起一个备注名。
- 选定某条保存的 IP,可以设为"默认",也可以一键设为当前通话目标。
- 麦克风、扬声器、接收音量、端口和语音增强都在界面上可调。
- 绿色单文件模式:设置、损坏备份和必要日志只写在 `LanPttIntercom.exe` 同目录;不写注册表,不写用户目录、AppData 或用户 profile。
- 默认发布为 framework-dependent single-file:输出一个 `LanPttIntercom.exe`,目标机器需要已安装 .NET 6 Desktop Runtime。

## 环境要求

- Windows 10 / Windows 11(64 位)
- 编译需要 .NET 6 SDK 或更高版本 SDK。
- 运行默认发布产物需要目标机器已安装 .NET 6 Desktop Runtime(`Microsoft.WindowsDesktop.App 6.x`)。

音频采集与播放直接使用 Windows 自带的 `winmm.dll`。语音增强使用 NuGet 包 `NWaves` 0.9.6,用于在线高通/DC 抑制、RMS 自动增益和限幅处理;包体约 418 KB,netstandard2.0 DLL 约 323 KB。当前发布仍是 framework-dependent single-file,不把 .NET 运行时打包进 EXE。

## 编译

在工程根目录(本 README 所在目录)下打开 PowerShell 或 CMD,执行:

```bat
dotnet build src\LanPttIntercom\LanPttIntercom.csproj -c Release
```

编译成功后会生成 `src\LanPttIntercom\bin\Release\net6.0-windows\win-x64\LanPttIntercom.exe`。

## 发布单一可执行文件

```bat
dotnet publish src\LanPttIntercom\LanPttIntercom.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

发布产物在 `src\LanPttIntercom\bin\Release\net6.0-windows\win-x64\publish\LanPttIntercom.exe`。这是 framework-dependent single-file 发布:应用本身是单个 EXE,但运行时仍需要目标机器上已安装 .NET 6 Desktop Runtime(`Microsoft.WindowsDesktop.App 6.x`)。

## 使用方法

1. 准备两台(或多台)处于同一局域网的 Windows 电脑,双方都运行 `LanPttIntercom.exe`。
2. 确认两边的"端口"一致(默认 `41000`),防火墙允许该端口的 UDP 通信。
3. 在 A 电脑上点击"添加",输入 B 电脑的局域网 IP(例如 `192.168.1.20`)和备注,保存。
4. 在 A 电脑的列表里选中该条记录,点击"载入为目标"。
5. 按住"按住说话"按钮(或者按住空格键),对着麦克风说话;在 B 电脑上应能听到声音。
6. 松开按钮即停止发送。可以再在 B 电脑上把 A 设为目标,反向对话。
7. 选一条记录点"设为默认"后,以后启动 A 电脑时,该 IP 会自动出现在"目标 IP"框中,**但仍需要按住说话才会开始发送**。
8. 单实例本机自测时,目标 IP 可填 IPv4 loopback 地址 `127.0.0.1` 并按住说话;程序会接收 loopback 包用于测试播放链路。当前界面输入只支持 IPv4,因此实际填写 `127.0.0.1`。

## 配置文件和绿色模式

- 路径:`LanPttIntercom.exe` 同目录的 `settings.json`。
- 包含:监听端口、已保存的 IP 列表、默认 IP、输入/输出设备、接收音量、语音增强开关和强度等。
- 格式损坏或读取失败时,程序会尽量把原文件备份为同目录 `settings.failed-时间戳.json`,本次启动使用默认设置,并在状态日志里显示警告。
- 保存失败时,界面会弹窗和状态提示;程序会尽量把错误写入同目录 `LanPttIntercom.log`。如果 EXE 目录不可写,不会改写到 AppData、注册表、用户 profile 或其他目录。
- 界面上的"设置文件"按钮可以用系统默认编辑器打开这个文件。

## 语音增强

"启用麦克风语音增强"只处理本机麦克风发送前的 PCM16 单声道帧,不改变 UDP 协议。处理链使用 NWaves 在线高通滤波器和动态限幅器,再配合保守 RMS 自动增益,用于减少低频/DC 偏移并抬高较小的人声。强度越高,增益和高通截止越明显;默认关闭,打开后默认强度 50。接收端仍会按界面上的"音量"设置缩放播放帧。

## 故障排查

- 如果 `settings.json` 或 `LanPttIntercom.log` 保存失败,检查 `LanPttIntercom.exe` 所在目录是否可写;程序不会改写到 AppData、注册表、用户 profile 或其他目录。
- 默认发布产物是 framework-dependent single-file,目标机器仍需已有 .NET 6 Desktop Runtime(`Microsoft.WindowsDesktop.App 6.x`)。
- NWaves 已随应用打进 single-file EXE,不需要单独放置 NWaves DLL;但它不包含 .NET 运行时。
- 如果听不到对方,优先确认双方在同一局域网/网段、目标 IP 正确、端口一致,并且 Windows 防火墙允许该 UDP 端口通信。

## 协议说明(简要)

UDP,默认端口 `41000`。每个包由 8 字节头 + PCM 负载组成:

```
[0]     包类型: 1 = 音频, 2 = 按下说话(press), 3 = 松开说话(release)
[1]     保留
[2..3]  序列号(大端,仅音频包)
[4..7]  时间戳毫秒(大端,仅音频包)
[8..N]  PCM 16 位 / 16 kHz / 单声道 负载
```

如果要让多台机器互联,把端口和音频格式保持一致即可。

## 文件结构

```
src/LanPttIntercom/
  Program.cs                  入口
  MainForm.cs                 主窗体(中文 UI)
  IntercomController.cs       业务协调
  app.manifest                应用程序清单
  LanPttIntercom.csproj       项目文件
  appsettings.example.json    配置文件示例
  Audio/
    MmsAudioCapture.cs        录音(P/Invoke winmm.dll)
    MmsAudioPlayback.cs       播放(P/Invoke winmm.dll)
    Pcm16Frame.cs             PCM16 帧工具
    VoiceEnhancer.cs          NWaves 语音增强
  Network/
    VoiceUdpClient.cs         UDP 语音收发
  Storage/
    PortableRuntimeLog.cs     同目录运行日志
    SettingsStore.cs          设置读写
  Models/
    AppSettings.cs            设置与端点数据模型
```

## 已知限制

- 仅支持 IPv4(局域网最常见的场景)。
- 音频格式固定为 16 kHz / 16 bit / 单声道,无法在界面上调整;如需调整请修改 `Models/AppSettings.cs` 的默认值。
- 同一台电脑只能有一个程序实例运行(因为绑定 UDP 端口);单实例自测可使用 IPv4 loopback 地址 `127.0.0.1` 听到自己。程序会接收 loopback 包用于自测;当前界面输入仍只支持 IPv4,所以实际填写 `127.0.0.1`。非 loopback 的本机局域网地址仍会被过滤,避免普通局域网使用时意外自回放。
- 没有做加密,如在不可信网络中使用请注意。
