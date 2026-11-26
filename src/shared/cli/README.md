# cli

The command-line entry point. A standalone executable that does not depend on
the GUI application or its startup path.

- Location: `src/shared/cli/` groups the non-GUI entry points under shared.
  It is a top-level front end and is never imported by other shared libraries.
- Depends on `platforms` and `core`; never the reverse.
- Intended to expose the same browsing and conversion capabilities without a
  window, so scripts and batch workflows can use them.
- Current state: skeleton only. No implementation yet.

## License

Copyright (c) 2026 NopeKat. All rights reserved.

Part of NopeView, licensed under the GNU Affero General Public License v3.0.
See the root [LICENSE](../../../LICENSE) for the full text.
