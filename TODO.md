# To do

Ideas that aren't planned for a release yet.

## Animated demos of the controls

A short animated clip for each control. Each clip shows what the control does, with a mouse cursor and the held
keys drawn in.

- Where: in the Help window (hover a row and its clip plays beside it), or when hovering the controls themselves
  in the main window. I haven't decided which.
- Which: Help lists about 33 controls. The rows for drags, snapping, zooming and key points gain the most; rows
  like Esc or F1 gain little.
- Recording: drive the debug build with its test hooks (simulated input, not the real mouse), capture the window
  frame by frame, draw the cursor and the badges for held keys on each frame, and encode with ffmpeg
  (`libwebp_anim`).
- Playing: Windows can't play animated WebP itself, so the app would decode each clip with its own ffmpeg on
  hover and show the frames in a popup. It would show nothing until ffmpeg is set up.
- Cost: roughly 100-300 KB a clip, so 5-8 MB more in the installer for the full set.
- Start with one sample (Shift + drag a point) to judge the look before recording the rest.

## Left and right channels in the waveform

"Mirrored" waveforms draw the same shape above and below the middle. Showing the left channel on top and the
right underneath would need each channel measured separately; the app keeps one loudness value per track.
