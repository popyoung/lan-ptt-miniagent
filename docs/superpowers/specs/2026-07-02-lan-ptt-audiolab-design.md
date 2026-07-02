# LanPttAudioLab 设计

## 状态

设计范围已确认；本文用于 review 和后续实现依据，但不代表已经开始实现。

本文中文优先，方便用户审阅。类名、项目名、JSON 字段、文件名保留英文并用反引号标出。

## 用户确认需求对照表

| # | 用户确认需求 | 本文位置 |
|---|---|---|
| 1 | 需要一个内部音频分析工具，避免每次靠两台电脑实时实测调参 | 目标、第一版工作流 |
| 2 | `LanPttAudioLab` 不需要发布，不影响主程序绿色单文件定位 | 非目标、项目结构 |
| 3 | 录音应由程序内完成，使用环境尽量和 `LanPttIntercom` 一致 | 第一版工作流、录音路径 |
| 4 | 应复用主程序现有音频处理逻辑，避免另写一套导致漂移 | 共享音频配置、实现顺序 |
| 5 | 主程序和 AudioLab 都应使用 `AudioEnhancementProfile` | 共享音频配置 |
| 6 | 预设参数应通过 JSON 可调 | 预设 JSON |
| 7 | 第一版不是给用户手调的实时滑杆界面，而是便于 Codex 分析后调参 | 目标、非目标 |
| 8 | 普通说话测试和哼唱/音高测试应分成两部分 | Run Type：语音质量、Run Type：音高扫描 |
| 9 | 音高测试不能假设用户能唱固定低/中/高音，只要求尽量保持一个音高再换另一个 | Run Type：音高扫描 |
| 10 | 音高测试允许用户决定录制多段 | Run Type：音高扫描 |
| 11 | 新的音高分析不能替换原先爆破音、断续、底噪、削顶等问题分析 | 报告结构、Run Type：语音质量 |
| 12 | 不能静默 fallback；录音、JSON、预设、报告失败都要明确提示 | 错误处理 |
| 13 | 真实麦克风和主观听感仍需要人工验证 | 测试策略 |
| 14 | 录音完成后的 `raw.wav` 默认可复用，调 preset 或重跑报告时不应要求重复录制 | 第一版工作流、实验目录 |

## 目标

`LanPttAudioLab` 是内部离线音频分析工具，用于调试和改进麦克风增强效果。它存在的原因是：实时两机对讲测试速度慢、主观性强、无法稳定复现，也看不到波形和指标。

工具按一次 run 录制或复用一个真实麦克风输入 `raw.wav`，然后把同一段输入离线送入多组增强预设，生成可比较的 WAV 文件、CSV 指标和 HTML 报告。调 preset、重跑指标或重新生成报告时，默认复用既有 `raw.wav`，不要求用户重复录音。

`LanPttAudioLab` 不是面向最终用户的调音台。第一版的定位是：用户按提示录制样本并反馈听感；Codex 根据报告、波形和听感调整 JSON 预设；工具用同一段输入重新跑同一套生产处理代码。

## 非目标

- 不把 `LanPttAudioLab` 发布为主程序的一部分。
- 不改变 `LanPttIntercom` 的绿色单文件发布定位。
- 第一版不做实时滑杆式 DSP 编辑器。
- 不复制、不 fork 一套新的增强算法。
- 第一版不让 `LanPttIntercom` 加载外部实验 profile JSON。
- 不要求用户理解 DSP 参数名。

## 共享音频配置

新增共享模型 `AudioEnhancementProfile`，承载当前写死在 `VoiceEnhancer` 中的算法常量。

第一版字段：

- `HighPassBaseHz`
- `HighPassStrengthSlopeHz`
- `PresenceCenterHz`
- `PresenceQ`
- `PresenceGainDbAt100`
- `TargetRmsBase`
- `TargetRmsAt100`
- `MakeupGainAt100`
- `PlosiveStrengthThreshold`
- `PlosiveInputRmsThreshold`
- `PlosiveFilteredRatioThreshold`
- `PlosiveOutputRmsCeiling`
- `LimiterThresholdDb`
- `LimiterRatio`
- `LimiterAttackSeconds`
- `LimiterReleaseSeconds`
- `OutputCeiling`

`VoiceEnhancer` 接受 `AudioSettings` 和 `AudioEnhancementProfile`。现有生产行为必须由 `AudioEnhancementProfile.Default` 保持不变。

`LanPttIntercom` 第一版只使用 `AudioEnhancementProfile.Default`，不从外部 JSON 加载实验 profile。

`LanPttAudioLab` 从 JSON 读取实验预设，并把预设中的 profile 字段覆盖到 `AudioEnhancementProfile.Default` 上。这样可以保证主程序和离线分析用的是同一套处理路径，同时允许不改代码就做实验。

## 项目结构

新增项目：

```text
src/LanPttAudioLab/
  LanPttAudioLab.csproj
  Program.cs
  MainForm.cs
  AudioLabPreset.cs
  AudioLabRunner.cs
  WavFile.cs
  Metrics/
    AudioMetrics.cs
    SpeechQualityAnalyzer.cs
    PitchSweepAnalyzer.cs
    HtmlReportWriter.cs
```

`LanPttAudioLab.csproj` 引用 `..\LanPttIntercom\LanPttIntercom.csproj`，类似现有测试项目。它可以是普通 framework-dependent WinForms 程序，不进入主程序发布流程。

## 第一版工作流

界面可以很简陋，只需要保证分析方便、可复现：

1. 选择或创建实验目录。
2. 选择 run type：`speech-quality` 或 `pitch-sweep`。
3. 选择输入设备，录音路径使用和主程序相同的 `MmsAudioCapture`。
4. 录制当前 run 的 `raw.wav`，或复用当前实验目录中已有的 `raw.wav`。
5. 加载或创建 `lab-presets.json`。
6. 对 `raw.wav` 运行所有预设；调 preset 或重新生成报告时默认复用同一份 `raw.wav`。
7. 写出输出 WAV、CSV 指标和 `report.html`。
8. 打开报告目录或 `report.html`。

除非用户显式选择其他路径，工具只写入所选实验目录。

## 实验目录

每个实验目录保存自己的 `raw.wav`。同一实验目录内，`raw.wav` 默认可反复复用，用于比较不同 preset 或重新生成报告。`speech-quality` 和 `pitch-sweep` 默认分开录制、分开分析、分开出报告；只有用户明确导入或复制既有样本时，才跨 run 复用输入。

```text
AudioLabRuns/
  2026-07-02-speech-001/
    run-type.json
    raw.wav
    lab-presets.json
    outputs/
      default-50-8.wav
      strong-75-30.wav
      max-100-100.wav
    metrics.csv
    plosive-metrics.csv
    report.html

  2026-07-02-pitch-001/
    run-type.json
    raw.wav
    lab-presets.json
    outputs/
      default-50-8.wav
      strong-75-30.wav
      max-100-100.wav
    pitch-metrics.csv
    report.html
```

`run-type.json` 记录 run type、时间戳、采样率、帧长、输入设备 id、工具版本等元数据。

## 预设 JSON

`lab-presets.json` 保存录音默认值和实验预设：

```json
{
  "recording": {
    "seconds": 8,
    "sampleRate": 16000,
    "bitsPerSample": 16,
    "channels": 1,
    "frameMilliseconds": 20,
    "inputDeviceId": -1
  },
  "presets": [
    {
      "name": "default-50-8",
      "strength": 50,
      "maxGainMultiplier": 8,
      "profile": "default"
    },
    {
      "name": "less-filtering-100-100",
      "strength": 100,
      "maxGainMultiplier": 100,
      "profile": {
        "highPassBaseHz": 65,
        "highPassStrengthSlopeHz": 0.5,
        "plosiveOutputRmsCeiling": 0.75
      }
    }
  ]
}
```

`profile: "default"` 表示完全使用 `AudioEnhancementProfile.Default`。如果 `profile` 是对象，则只覆盖对象里提供的字段。未知字段必须报错，不能静默忽略。

预设名用于输出文件名之前必须做文件名清理。

## Run Type：语音质量 `speech-quality`

目的：复现普通对讲时的语音质量问题，包括音量不足、声音闷、爆破音、断续、ducking、底噪和接近削顶的失真风险。

建议录音提示使用一段固化脚本，减少随机因素。后期如果覆盖面不够，再修改测试文本。

用户按以下脚本朗读：

```text
1. 请保持安静 2 秒，用于采集底噪。
2. 用正常音量说一句常用测试句，内容为：侬则钟桑。然后停顿 1~3 秒。
3. 用较小音量说同一句话。然后停顿 1~3 秒。
4. 用较大音量说同一句话。然后停顿 1~3 秒。
5. 说一些容易产生爆破音的词，词与词之间停顿 1~3 秒：怕痛，特别，铁血，等到，地皮，对付，跑步，破坏，肥胖，墙壁，笨蛋，疾病。
6. 说一句带有爆破音的句子。中间故意停顿，用于检查断续、门限和 ducking。句子内容为：外面发生了爆炸，你快逃。（此处停顿 1~3 秒）跑步前进！
7. 最后再用正常音量说一句话，用于检查爆破音或大声段之后是否恢复正常。内容为：侬则钟桑。
```

报告重点：

- 输入和输出 RMS。
- RMS 增益 dB。
- 峰值和 near-ceiling 样本数量。
- 帧级包络稳定性。
- 低能量帧比例和疑似 dropout 帧比例。
- 基于低频瞬态能量的爆破音分数。
- 爆破音之后 50-200 ms 的 ducking 情况。
- 底噪段被放大的程度。
- 每个预设的输出 WAV 播放器。

输出：

- `metrics.csv`
- `plosive-metrics.csv`
- `report.html`
- `outputs/<preset>.wav`

## Run Type：音高扫描 `pitch-sweep`

目的：排查用户哼出不同音高时出现的严重失真、音色突变或某些音高被过度过滤的问题。

提示不能假设用户能准确控制低音、中音、高音，只要求尽量保持一段相对稳定的音高：

```text
请录制任意数量的音高段。
每段尽量保持一个音高 1-3 秒，然后停顿一下。
下一段换一个不同音高，再保持 1-3 秒。
不要求唱准固定音名，也不要求按低/中/高顺序录制。
```

分析器应当：

1. 根据能量和静音间隔检测 voiced segments。
2. 丢弃过短段，例如短于 0.6 秒的片段。
3. 估算每段主音高。
4. 给出 `pitch_confidence`。
5. 比较每段输入和每个预设输出的变化。

报告重点：

- 每段开始/结束时间。
- 估算音高 Hz。
- `pitch_confidence`。
- 每段输入/输出 RMS 和增益 dB。
- near-ceiling 样本数量。
- 低频比例。
- 用于筛查失真的 THD-like 或 harmonic-ratio 指标。
- 可行时估算限幅器活动。

输出：

- `pitch-metrics.csv`
- `report.html`
- `outputs/<preset>.wav`

## 报告结构

`report.html` 应该是静态文件，能直接从磁盘打开。第一版可以用 inline SVG 或 HTML canvas 生成简单波形/包络图，不要求 polished UI。

报告分三层，避免把新增音高分析误实现成替代原有语音分析：

1. 通用增强指标：两种 run 都必须包含，包括原始音频播放器、每个预设输出播放器、预设参数摘要、RMS、峰值、near-ceiling、帧级包络等。
2. `speech-quality` 专属面板：爆破音、ducking、dropout、底噪放大、接近削顶风险、语音段恢复情况。
3. `pitch-sweep` 专属面板：segment 列表、音高 Hz、置信度、不同音高段的增益/失真/限幅筛查。

## 指标说明

第一版优先使用简单、可解释、足够定位问题的指标：

- RMS：PCM16 样本均方根。
- Peak：最大绝对样本值。
- Near-ceiling count：接近输出上限的样本数量，例如绝对 PCM 值 >= 29500。
- Frame RMS：20 ms 帧 RMS。
- Envelope stability：voiced frames 上的 max/min 或 percentile ratio。
- Low-energy frame ratio：输入有声音但输出异常低的帧比例。
- Plosive score：短窗口低频能量尖峰相对附近基线的分数。
- Pitch estimate：第一版可以使用简单 autocorrelation。
- Pitch confidence：没有稳定音高时应给低置信度。
- THD-like metric：非基频能量相对总能量的比例；它只是筛查指标，不是实验室级 THD。

## 错误处理

- 录音失败时显示 WinMM 错误，不创建“看起来成功”的部分 run。
- `lab-presets.json` 无效时显示文件路径和 JSON 错误位置。
- 单个 preset 无效时，只有用户显式选择跳过无效 preset 才继续，否则停止当前 run。
- WAV 输出已经写出但报告生成失败时，保留 WAV，并弹出报告错误。
- 不能静默 fallback 到其他设备、采样率、算法或默认预设。

## 测试策略

自动化测试覆盖共享且非 UI 的逻辑：

- `AudioEnhancementProfile.Default` 保持现有生产行为。
- JSON profile overlay 只覆盖提供字段，并拒绝未知字段。
- WAV reader/writer 可以 round-trip PCM16 mono 样本。
- 离线批处理对每个 preset 都生成一个输出。
- 语音指标能在合成输入上得到预期 RMS、peak、frame 值。
- pitch segmenter 能按静音切分 voiced segments，并丢弃过短片段。
- pitch estimator 对合成正弦波返回合理 Hz，对噪声返回低置信度。

真实麦克风采集、听感、爆破音是否干净、人声是否闷、不同音高是否失真，仍需要人工验证。

## 实现顺序

1. 抽取 `AudioEnhancementProfile`，更新 `VoiceEnhancer` 接收 profile，同时保持默认行为不变。
2. 为默认 profile 行为和 JSON overlay 写测试。
3. 增加 WAV 读写 helper。
4. 新增 `LanPttAudioLab` 项目，先做最小 WinForms shell 和录制 `raw.wav`。
5. 增加离线 batch runner，把 `raw.wav` 送入多个预设。
6. 增加 CSV 指标输出。
7. 增加静态 HTML 报告输出。
8. 增加 `speech-quality` 和 `pitch-sweep` 分析器。
9. 实现完成后按 Codex/MiniMax review workflow 生成审查包并交 MiniMax 审查。