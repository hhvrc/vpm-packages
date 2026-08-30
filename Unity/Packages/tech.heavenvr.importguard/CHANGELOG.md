# Changelog

## [Unreleased]
- Encrypted package export/import: "Encrypt" on export password-protects a
  `.unitypackage.enc`; dragging one into Import Guard (or double-clicking it,
  or opening it via Choose Package) prompts for the password before reading
  anything. Authentication is pluggable (`IUpkgAuthMethod`) - password is the
  only method implemented so far.
- Drag-and-drop: a `.unitypackage`/`.unitypackage.enc` can be dropped straight
  into the Import Guard window instead of using Choose Package.

## [0.1.0]
- Initial package.
