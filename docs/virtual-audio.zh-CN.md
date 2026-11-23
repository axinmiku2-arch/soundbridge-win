# 静音虚拟声卡计划与验收

目标：Windows 应用 → 不直接发声的虚拟播放设备 → 声桥 → 两台真实设备。这样两路真实设备都由声桥调度，不必依赖一个直接播放的物理设备。用默认设备还是只重定向一个应用，由用户在 Windows 手动选择。停止声桥时，若应用仍向虚拟设备输出而且没有其他接收方，就会静音；请在系统设置选回原设备。声桥不代改默认设备或安装系统驱动。

## 当前能做什么

在声桥里选择一个已安装、处于活动状态的播放端点，作为同步模式的音源。它可以是已验证的虚拟端点，也可以是已有设备。产品不会根据设备名字自动识别或声称“它一定不发声”。打开声音设置的按钮只导航，不更改任何选择。两路输出都需通过启动检查；缺失、重复或把源选作输出会拒绝，不会静默回落到默认端点。

本机 WASAPI 环回是从播放端点录取音频，不是凭空创建新的虚拟设备。仅用声桥的 EXE 没办法在系统里安装一个新的可信音频驱动。[Microsoft WASAPI 环回说明](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)

## 可选驱动的开源路线

- 一条路线是另做或适配独立虚拟驱动。微软 SysVAD 是 MS-PL 许可样例；它的模拟回环会返回测试正弦波，不会自动变成真实 PCM 声桥。若以它为基础，要保留来源和许可，并改成真实的本机音频路径，验证没有硬件发声、没有网络、没有写音频文件。[SysVAD 样例与限制](https://learn.microsoft.com/en-us/samples/microsoft/windows-driver-samples/sysvad-virtual-audio-device-driver-sample/) · [上游许可](https://github.com/microsoft/Windows-driver-samples/blob/main/LICENSE)
- 也可以审查现有开源 Windows 线缆，例如 [DeckX Virtual Audio Driver（第三方项目）](https://github.com/DeckX-Development/DeckX-Virtual-Audio-Driver)。它提供播放/录音端点，上游公开发行物并不等于本项目已确认签名、来源许可链、或播放端 WASAPI 能回读实际 PCM。审阅 v1.0.17 的安装脚本还发现，脚本会按宽泛的名称匹配修改音频端点并重启音频服务，所以声桥不会采用该安装器。**目前不捆绑、不推荐运行、不自动下载或安装。**
- 若本项目发布新内核驱动，需单独审查代码、驱动包及签名，走 Microsoft 支持的合作伙伴与生产签名流程。签名可能涉及证书和维护成本；软件开源免费不等于签名流程免费。正式安装包必须允许跳过驱动，保留撤销路径，不要求关闭安全启动、内存完整性或驱动签名检查。[微软驱动签名策略](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/kernel-mode-code-signing-policy--windows-vista-and-later-) · [签名选项](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings)

## 真实 Windows 验收清单

1. 安装前记录默认和通信设备。只用受信任的签名包、专用测试机或虚拟机，确认“不装驱动”仍可使用普通模式。
2. 在设置中将单个测试应用切到虚拟播放端点，播放合成音。确认未开声桥时没有扬声器出声，开声桥后捕获的是该实际样本，而不是连续测试音或麦克风。
3. 验证两个独立真实设备同时播放；分别测试总音量、静音、每路微调，以及经过测量后两路实际延迟。拔掉一台设备、休眠恢复、虚拟音源禁用，均不应悄悄切源，也不应形成反馈。
4. 验证重新选择原设备、卸载后恢复原设置、Windows 重启后按预期工作。记录系统架构、音频端点类型、驱动版本和签名结果。
