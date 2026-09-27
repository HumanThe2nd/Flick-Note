# Unity-Game: Flick Note

A rhythm game controlled by the IMU glove, built around what the glove does
best: **pointing** and **wrist flicks** (orientation and rotation speed, both
accurate and instant). Inspired by Project Sekai's flick notes. It never relies
on absolute hand position, which an IMU can't measure without drift.

## How to play

1. **Board on** (BLE-Basic firmware; battery, charger or USB).
2. **Start the bridge** (it connects to the glove and feeds Unity):
   ```
   cd D:\Workspace\Flick-Note\BLE-Basic\pc-app
   python unity_bridge.py
   ```
   Hold the glove still for ~2 s until it prints `calibrated`.
3. **Start the game**, either:
   - **Build/FlickNote.exe** (no Editor needed), or
   - open this folder in Unity Hub (Add → project from disk →
     `D:\Workspace\Flick-Note\Unity-Game`), open `Assets/Scenes/FlickNote.unity`,
     and press **Play**.
4. Point your hand straight ahead and press **R** to re-center.
5. **Flick** (or press Enter) to start.

Notes fly toward you in four lanes: **up, down, left, right**.

| Note | Looks like | What to do |
|------|-----------|------------|
| NOTE | pale spinning cube | **point** at its lane (follow the laser) when it reaches the ring |
| FLICK NOTE | red orb with an arrow | **flick your wrist in the arrow's direction** on the beat, from anywhere; aim doesn't matter |

Flick direction is measured from how your fingertips move in the room (up,
down, left, right), so it works whichever way your wrist is rolled. A flick in
the wrong direction shows WRONG WAY. Timing: PERFECT ±70 ms, GOOD ±140 ms.
About 40% of notes are flick notes (`RhythmGame → Flick Note Share`).

The laser beam and white dot show where your hand points. The lane ring you're
aiming at grows and lights up. Lanes sit 22° (up/down) and 28° (left/right)
from center, and aiming counts within 14°, so pointing straight ahead aims at
nothing. Combo multiplier up to x4. A flick (or Enter) starts the song; the
bridge window prints each flick with its direction and speed.

**Keys:** `R` re-center · `[` / `]` latency offset −/+10 ms · `Enter` start ·
`Esc` quit (exe). Without a glove: arrow keys aim, `Space` flicks toward the aimed lane.

**If hits feel late or early**, adjust the latency offset with `[` `]`. The
default of 45 ms covers Bluetooth plus the bridge.

## How it fits together

```
glove (ESP32 + ICM-20948) --BLE--> unity_bridge.py --UDP 127.0.0.1:5005--> Unity
                                    - Madgwick orientation filter
                                    - gyro calibration
                                    - flick / shake / flip detection
```

Scripts (`Assets/Scripts/`):

- `GloveReceiver.cs`: UDP listener. Exposes `Rotation`, `LinearAcceleration`,
  `AngularVelocity`, `Still`, `Connected`, and a `Gesture` event.
- `GloveHand.cs`: moves the hand to mirror the glove. Re-centering, mount
  correction, arm model for position, keyboard fallback.
- `RhythmGame.cs`: game logic (chart, timing, judging, scoring).
- `RhythmGame.Visuals.cs`: neon rings, approach circles, note glow and trails,
  hit bursts, aim beam, beat-synced tunnel, starfield.
- `RhythmGame.Hud.cs`: HUD (status, score card, progress bar, combo, count-in,
  judgement pop-ups, menu and results with letter grade).
- `NeonFX.cs`: helpers for glow lines, soft particles and rounded panels.
- `Theme.cs`: **every color in the game** (dark monochrome red). Edit it to re-skin everything.
- `GloveModel.cs`: the glove: glossy jointed fingers, glowing fingertips, cuff
  ring and sensor LED, and a forearm sleeve. The fingers make a quick fist on
  every strike. **Hand → Model Scale** sets its size; **Show Arm** also draws
  the arm model's upper arm and elbow.
- `BeatSynth.cs`: generates the backing track in code (no audio files).
- `KeyInput.cs`: keyboard helper for either Unity input system.
- `Editor/SceneBuilder.cs`: menu **Glove → Build Rhythm Scene** recreates the
  scene. **Glove → Build Windows Player** builds `Build/FlickNote.exe`.

## Tuning (select the object, then edit in the Inspector)

- **Hand → Mount Rotation**: if the virtual hand turns the wrong way, the board
  is mounted differently than assumed (flat on the back of the hand, x toward
  the fingers). Try 90° steps.
- **Hand → Arm model**: shoulder/elbow/forearm lengths, how much the elbow follows.
- **Hand → Smoothing**: 0 = rawest and fastest.
- **RhythmGame → Song**: drop in your own audio clip and set **Bpm** and
  **Song Offset** (seconds to the first beat). The chart is generated from the BPM.
- **RhythmGame → Timing / Layout**: hit windows, note speed, lane angles, aim tolerance.
- Gesture sensitivity lives in the bridge (`FlickDetector`
  thresholds in `unity_bridge.py`).

## Rebuilding and self-test

After changing scripts, rebuild the exe from the Editor (**Glove → Build
Windows Player**) or from a terminal:

```
"D:\Unity\Editors\6000.6.3f1\Editor\Unity.exe" -batchmode -quit -projectPath D:\Workspace\Flick-Note\Unity-Game -executeMethod SceneBuilder.BuildPlayerBatch -logFile Logs\build.log
```

Self-test (no human needed): `Build\FlickNote.exe -glovebot -glovetest 66 shot.png`
plays the whole song with a bot that aims and strikes perfectly, saves a
screenshot at 66 s, writes a `GLOVETEST ...` summary line to the player log,
and quits. Without `-glovebot` it just runs with whatever input is connected.
The bot currently scores 78/78 Perfect.

## Ideas for next steps

- Twist notes: rotate the wrist to a shown angle (roll is very accurate).
- Hold notes: keep pointing along a moving path.
- Charts that follow a real song (beat-detect or hand-author a note list).
