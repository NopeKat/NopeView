# file list

This directory holds the shared file list service. It enumerates the files in
one directory, filters them by extension, sorts the result, and returns full
UTF-8 paths to the host.

- Boundary: `abi/nv_files_abi.h` is a core service ABI, separate from the
  router, format, and plugin ABIs.
- The host passes a directory and a comma-separated extension list; it never
  walks the directory itself.
- Only top-level files are returned. Directories are skipped.
- Extension matching and sorting are case-insensitive.
- Paths are owned by the list and released with `nv_file_list_destroy`.
- The Windows backend uses `FindFirstFileW` and converts UTF-16 names to UTF-8,
  so non-ASCII paths work end to end. Other platforms add their own backend
  behind the same ABI.
