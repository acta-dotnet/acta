# Todo

- Purge/remove all obsolete-code guards, e.g. `obsoleteCoreFolders` in ArchitectureBoundaryTests:
  we are not even launched yet, so tests defending against re-creating dead pre-reorg layouts
  (SystemJobs/Runtime/Storage/Entities/Schema/Builders/Errors/Features/Operations) should go away
  entirely rather than accumulate. Decide whether any of that gate earns its keep pre-1.0.
