# Todo

- Purge/remove all obsolete-code guards, e.g. `obsoleteCoreFolders` in ArchitectureBoundaryTests:
  we are not even launched yet, so tests defending against re-creating dead pre-reorg layouts
  (SystemJobs/Runtime/Storage/Entities/Schema/Builders/Errors/Features/Operations) should go away
  entirely rather than accumulate. Decide whether any of that gate earns its keep pre-1.0.

- Extract the ScopeSelector popover into a shared Dropdown (add typeahead + close-on-focusout) and
  use it for the long lists only: EnqueueJob namespace/job name, ScheduleControls time zone. Short
  enum filters stay native selects.
