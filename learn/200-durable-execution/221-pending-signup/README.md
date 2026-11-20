# How to hold a sign-up until the email is confirmed

## Remember

- Nothing real exists until `create-user` runs. An unconfirmed sign-up ends as a cancelled job and leaves no half-made account behind.
- The deadline is stored once, as database time plus the timeout. Restarts and downtime never make it longer.
- The handler runs from the top after the confirmation. The email is not sent again, because the step that sends it already has a recorded outcome.
- Make `create-user` safe to repeat: insert only when the address is absent. A crash right after the insert runs the step again, and two submits of the form end as one account.
- A finished job is kept with its input for its retention period: ninety days by default, seven here, through `[Job("register-user", JobRetention = "7d")]`. Keep the input to what the work needs when it holds personal data.
- In a web app the confirm link opens a page with a Confirm button that posts: mail scanners fetch the links they see, and a fetch must not confirm anything.
- `"send-confirmation"` and `"create-user"` are the keys to each step's stored result. Rename `"send-confirmation"` and a sign-up that already sent its email sends it again.
- `"email-confirmed"` names the signal a sign-up waits on. A renamed signal is a different signal, so let the waiting sign-ups finish before you rename it.
- A job that waits for a signal or a timer can stay stored for days. The name in `[Job("register-user")]` and the namespace in `Run<PendingSignupJobs>("pending-signup")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `RegisterUser` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `register-user-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](../../500-payloads/502-job-versions/).

## Check yourself

**The app is redeployed twelve hours after the sign-up. What happens to the pending registration?**

- It is lost and the person has to sign up again
- Nothing: it is a suspended job, and the link still works
- The confirmation email is sent a second time

<details><summary>Answer</summary>

Nothing: it is a suspended job, and the link still works. The wait is a row with its deadline stored in the database. No process had to stay alive for it, and the email step is not repeated.

</details>

**Nobody opens the link. What is left when the deadline has passed?**

- A user row that has to be deleted
- A cancelled job with its timeline, and no user
- A job that waits forever

<details><summary>Answer</summary>

A cancelled job with its timeline, and no user. The wait times out, the handler cancels the job, and `create-user` never runs. The job row stays for its retention period as the record of what happened.

</details>
