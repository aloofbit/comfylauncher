# Patch files to ship, e.g. patch-V.mpq.
#
# Big binaries, so they are NOT tracked. Put the .mpq here and a one line
# description beside it as <name>.txt, which is what the launcher shows in the
# note column. Two flags may go on a line of their own in that .txt:
#
#   X-OptIn: 1     the row starts unticked for everybody, and the patch stays
#                  out of ComfyCraft.zip
#   X-Required: 1  the row is ticked and cannot be unticked, and the launcher
#                  holds back Start the game until the patch is installed and
#                  current
#
# patch-V.mpq is ours and REQUIRED. Its sidecar is tracked; the .mpq is not,
# because it is rebuilt from the client's own files. Build it before
# Build-Manifest.ps1, which stops if a sidecar has no patch beside it:
#
#   node tools/model-browser/build-patch-v.js
#
# Until 2026-09-23 patch-V.mpq was something else: a silent login track,
# opt-in, 1.6 KB and tracked. The letter was reused for the required patch, so
# a client that has the old one sees an update, and the Music button on the
# login screen replaces the silence. Its rebuild recipe is in git history.
