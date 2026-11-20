# How to test the first-job guide's app

## Remember

- The namespace in `Run<ShippingJobs>("shipping")` and the job names this test drives are the same strings the app stores with every job. Treat them as permanent once jobs exist under them: renaming one in the app and in the test together keeps the test green, and leaves the jobs already stored with no handler to run them.
