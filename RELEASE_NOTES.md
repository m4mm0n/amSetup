# amSetup 0.2.3.0

This release fixes the setup-builder preview so it looks like the generated
Windows installer instead of a generic web mockup.

Highlights:

- The builder preview now uses a runtime-shaped wizard frame with titlebar,
  sidebar, content surface, and bottom button strip.
- Preview palette resolution now matches the final installer palette rules.
- Glossy buttons, sidebar shine, and progress bars now match the generated
  Windows setup UI much more closely.
- A page selector previews Welcome, License, Options, Installing, and Complete
  pages.
- Previous 0.2.2.0 polish remains included: themed package-loading taskbar
  window and native Windows DWM caption/border/title colors.
- Embedded, adjacent, and split package layouts.
- Brotli compression with speed/size options.

The generated release assets contain self-contained stubs for Windows, Linux,
and macOS. Use the matching stub for the OS and architecture you are packaging
for.
