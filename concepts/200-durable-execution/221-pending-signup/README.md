# How to hold a sign-up until the email is confirmed

## Remember

- Nothing real exists until `create-user` runs. An unconfirmed sign-up ends as a cancelled job and leaves no half-made account behind.
- The deadline is stored once, as database time plus the timeout. Restarts and downtime never make it longer.
- The handler runs from the top after the confirmation. The email is not sent again, because `send-confirmation` already has a recorded outcome.
- Make `create-user` safe to repeat: insert only when the address is absent. A crash right after the insert runs the step again, and two submits of the form end as one account.
- A finished job is kept with its input for its retention period: ninety days by default, seven here. Keep the input to what the work needs when it holds personal data.
- In a web app the confirm link opens a page with a Confirm button that posts: mail scanners fetch the links they see, and a fetch must not confirm anything.

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
