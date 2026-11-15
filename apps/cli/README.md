# cli

The command-line entry point. A standalone executable that does not depend on
the GUI application or its startup path.

- Depends on `shell` and `core`; never the reverse.
- Intended to expose the same browsing and conversion capabilities without a
  window, so scripts and batch workflows can use them.
- Current state: skeleton only. No implementation yet.
