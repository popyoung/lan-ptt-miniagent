# LanPttAudioLab Design

## Status

Approved for design. This document defines the first implementation target for an internal audio analysis tool. It does not authorize implementation by itself.

## Purpose

`LanPttAudioLab` is an internal offline analysis tool for tuning microphone enhancement. It exists because real-time two-machine testing is slow, subjective, and hard to reproduce. The tool records one real microphone sample, applies multiple enhancement profiles offline, and produces comparable WAV files plus reports.

The tool is not a user-facing tuning console. It is for Codex-assisted tuning: the user records samples and gives listening feedback; Codex adjusts JSON presets; the tool reruns the same input through the same production enhancement code.

## Non-Goals

- Do not publish `LanPttAudioLab` as part of the green single-file intercom app.
- Do not add a realtime slider-based DSP editor in the first version.
- Do not copy or fork the enhancement algorithm.
- Do not make `LanPttIntercom` load external experimental profiles in the first version.
- Do not require users to understand DSP parameter names.

## Shared Audio Profile

Add a shared `AudioEnhancementProfile` model that contains algorithm constants currently embedded in `VoiceEnhancer`.

Initial fields:

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

`VoiceEnhancer` must accept `AudioSettings` plus `AudioEnhancementProfile`. The existing production behavior should be preserved by `AudioEnhancementProfile.Default`.

`LanPttIntercom` uses only `AudioEnhancementProfile.Default` in the first version. It does not load external profile JSON.

`LanPttAudioLab` reads experiment presets from JSON and overlays preset profile values onto `AudioEnhancementProfile.Default`. This keeps production and offline processing consistent while allowing experiments without code edits.

## Project Structure

Add a new project:

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

`LanPttAudioLab.csproj` references `..\LanPttIntercom\LanPttIntercom.csproj`, similar to the existing tests project. It may be a normal framework-dependent WinForms executable and is not part of the main app publish flow.

## First-Version Workflow

The UI can be minimal. It needs only enough controls to run analysis:

1. Select or create an experiment directory.
2. Choose run type: `speech-quality` or `pitch-sweep`.
3. Choose input device, using the same `MmsAudioCapture` path as the main app.
4. Record `raw.wav` into the experiment directory.
5. Load or create `lab-presets.json`.
6. Run all presets against `raw.wav`.
7. Write output WAV files and reports.
8. Open the report directory or `report.html`.

The tool should only write inside the selected experiment directory unless the user explicitly chooses another path.

## Experiment Directory

Each run lives in its own directory:

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

`run-type.json` records run metadata such as run type, timestamp, sample rate, frame length, selected device id, and tool version if available.

## Preset JSON

`lab-presets.json` stores recording defaults and experiment presets:

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

`profile: "default"` means use `AudioEnhancementProfile.Default` unchanged. If `profile` is an object, only supplied fields override the default profile. Unknown fields should be reported as errors instead of silently ignored.

Preset names must be sanitized before use as output filenames.

## Run Type: Speech Quality

Purpose: reproduce normal push-to-talk speech quality issues.

Suggested recording prompt:

```text
Read one normal sentence, one quiet sentence, one louder sentence, then say several plosive-heavy words or syllables. Pause briefly and say one final normal sentence.
```

The report should focus on:

- Input and output RMS.
- RMS gain in dB.
- Peak and near-ceiling sample counts.
- Frame-level envelope stability.
- Low-energy or dropout-like frame ratio.
- Plosive burst score based on low-frequency transient energy.
- Post-plosive ducking over the next 50-200 ms.
- Output WAV players for every preset.

Outputs:

- `metrics.csv`
- `plosive-metrics.csv`
- `report.html`
- `outputs/<preset>.wav`

## Run Type: Pitch Sweep

Purpose: diagnose pitch-related distortion found when the user hums different tones.

The prompt must not assume the user can sing fixed low/mid/high notes. It should say:

```text
Each segment: try to hold one pitch for 1-3 seconds, pause briefly, then switch to a different pitch. You may record any number of segments.
```

The analyzer should:

1. Detect voiced segments by energy and silence gaps.
2. Drop segments that are too short, such as under 0.6 seconds.
3. Estimate dominant pitch for each segment.
4. Record a pitch confidence value.
5. Compare input and output for each segment.

The report should focus on:

- Segment start/end.
- Estimated pitch in Hz.
- Pitch confidence.
- Input/output RMS and gain dB by segment.
- Near-ceiling sample count.
- Low-frequency ratio.
- A THD-like or harmonic-ratio metric for distortion screening.
- Estimated cap/limiter activity when feasible.

Outputs:

- `pitch-metrics.csv`
- `report.html`
- `outputs/<preset>.wav`

## Report Format

`report.html` should be static and self-contained enough to open from disk. It should include:

- Raw input audio player.
- Output audio players for each preset.
- Preset parameter summary.
- Metrics tables.
- Simple envelope chart for input and output.
- Speech quality panel for speech runs.
- Pitch segment panel for pitch runs.

First version may use inline SVG or HTML canvas generated from compact sampled arrays. It does not need a polished UI.

## Metrics Notes

Use simple, explainable metrics first:

- RMS: root mean square of PCM16 samples.
- Peak: maximum absolute sample value.
- Near-ceiling count: samples near the output ceiling, e.g. absolute PCM value >= 29500.
- Frame RMS: RMS over 20 ms frames.
- Envelope stability: max/min or percentile ratio over voiced frames.
- Low-energy frame ratio: fraction of voiced input frames where output RMS is unexpectedly low.
- Plosive score: short-window low-frequency energy spike compared with nearby baseline.
- Pitch estimate: simple autocorrelation is acceptable for first version; confidence should be low when no stable pitch is found.
- THD-like metric: ratio of non-fundamental energy to total analyzed energy. It is a screening metric, not lab-grade THD.

## Error Handling

- If recording fails, show the WinMM error and do not create a partial successful run.
- If `lab-presets.json` is invalid, show the path and JSON error location when available.
- If a preset is invalid, continue only if the user explicitly chooses to skip invalid presets; otherwise stop the run.
- If report generation fails after WAV outputs are written, keep the WAV files and show the report error.
- Do not silently fall back to different devices, sample rates, algorithms, or default presets.

## Testing Strategy

Add tests for shared, non-UI logic:

- `AudioEnhancementProfile.Default` preserves current production behavior.
- JSON profile overlay applies only provided fields and rejects unknown fields.
- WAV reader/writer round-trips PCM16 mono samples.
- Offline batch processing produces one output per preset.
- Speech metrics compute expected RMS/peak/frame values on synthetic input.
- Pitch segmenter splits voiced segments separated by silence and drops too-short segments.
- Pitch estimator returns a reasonable Hz value for synthetic sine input and low confidence for noise.

Manual verification remains required for real microphone capture and subjective listening.

## Implementation Order

1. Extract `AudioEnhancementProfile` and update `VoiceEnhancer` to accept it while preserving current default behavior.
2. Add tests for profile default behavior and overlay parsing.
3. Add WAV read/write helpers.
4. Add `LanPttAudioLab` project with minimal WinForms shell and capture-to-`raw.wav` path.
5. Add offline batch runner that processes `raw.wav` through presets.
6. Add metrics CSV generation.
7. Add static HTML report generation.
8. Add speech-quality and pitch-sweep analyzers.
9. Run the Codex/MiniMax review workflow before merging implementation.