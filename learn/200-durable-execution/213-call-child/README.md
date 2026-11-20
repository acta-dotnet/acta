# How to call another job and use its result

## Remember

- `"transcode"` is how `publish-episode` finds the child it already started. A renamed child is new work: the video is transcoded again.
- A parent and its children are each stored under their own job name. The names in `[Job("publish-episode")]` and `[Job("transcode-video")]`, and the namespace in `Run<CallChildJobs>("call-child")`, are stored with every job. Treat them as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `PublishEpisode` is stored with each job as its input, and `EpisodePublished` as its result. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `publish-episode-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).
