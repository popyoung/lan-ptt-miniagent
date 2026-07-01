# LAN PTT Intercom Task

Build a lightweight Windows LAN voice intercom, similar to a push-to-talk walkie-talkie.

## Platform

- Use .NET 6.
- Build a Windows desktop application.
- Target low resource usage.
- Prefer a single executable output if practical.
- Keep the application on .NET 6.
- Use green/portable runtime storage: application settings, failed-setting backups, and necessary logs must stay beside the executable. Do not write runtime state to registry, AppData, user profile, or other directories.
- Keep the publish shape framework-dependent single-file; do not switch to self-contained publishing.

## User Interface

- The application UI must be in Chinese.
- The UI should be simple and practical, focused on repeated daily use.
- The main screen should show:
  - Local listening status.
  - Target IP address.
  - Saved IP address list.
  - Push-to-talk control.
  - Basic connection/audio status.

## Startup Behavior

- The application must start listening automatically when opened.
- Startup listening must not require the user to press a separate "start listen" button.
- Loading a saved default IP on startup must not automatically start transmitting audio.

## Voice Calling

- The application should work on a Windows LAN.
- The user can press and hold a corresponding key/control to start calling.
- Releasing the key/control stops transmitting.
- The application should continue listening while idle.

## Saved IP Addresses

- The user can manually save previously called IP addresses.
- Saved IP addresses must be loadable from the UI.
- Saved IP addresses must be editable.
- Saved IP addresses must be deletable.
- A saved IP address may have a user-editable display name or note.
- The user can choose one saved IP address to load automatically after the program starts.

## Voice Enhancement

- Provide a persisted microphone voice enhancement switch and strength setting.
- Implement the enhancement with NWaves, not NAudio, RNNoise, WebRTC native code, or a silent hand-written-DSP fallback.
- Enhancement applies before sending local microphone PCM16 mono frames. Receiving still respects the output volume setting.

## Expected Deliverables

- Source code for the .NET 6 Windows desktop application.
- A short README in Chinese that explains how to build, publish, and run using already-installed tools.
- A safe build command and publish command if available.
- Do not include generated build output unless it is necessary for the task.
