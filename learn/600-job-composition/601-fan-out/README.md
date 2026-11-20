# How to enqueue many independent jobs from a handler

## Remember

- A parent and its children are each stored under their own job name. The names in `[Job("make-album-thumbnails")]` and `[Job("make-thumbnail")]`, and the namespace in `Run<FanOutJobs>("fan-out")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `MakeAlbumThumbnails` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `make-album-thumbnails-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
