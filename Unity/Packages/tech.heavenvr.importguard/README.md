# ImportGuard

Inspect a `.unitypackage` against your project *before* importing it, decide what
comes in, and import with colliding GUIDs remapped.

The hazard it exists for: a package claiming a GUID that already belongs to a
different asset in your project. The paths differ, so Unity's own import dialog
looks completely clean while existing references get silently re-pointed.

## Usage

`Tools > HeavenVR > ImportGuard`

## Features

- Diffs a `.unitypackage` against the current project before anything is written
- Flags GUID collisions and remaps them on import instead of overwriting
- Audits scripts inside the package
- Shows what each entry would land on, as a tree
- Drag a `.unitypackage` straight into the window, or double-click one, or use
  Choose Package
- Export a password-protected `.unitypackage.enc`; opening one prompts for the
  password before anything is read. This is a distribution gate, not DRM -
  the ciphertext and the code that decrypts it both ship in the file, so it
  only stops "double-click and it just opens," not a determined recipient.
  Authentication is pluggable (see `IUpkgAuthMethod`); password is the only
  method implemented today.

## License

GPL-3.0-or-later. See [LICENSE.md](LICENSE.md).

Copyright (C) 2026 HeavenVR. This program comes with ABSOLUTELY NO WARRANTY;
it is free software, and you are welcome to redistribute it under the terms of
the GNU General Public License as published by the Free Software Foundation.
