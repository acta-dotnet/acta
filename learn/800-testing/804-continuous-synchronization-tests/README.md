# How to test a recurring synchronization job

## Remember

- `"sync-orders"` and the variable `"watermark"` are the strings the job stores its work under, and this test reads them by name. They are permanent once jobs exist under them. A renamed variable reads as absent, so renaming it in the job and in the test together keeps the test green while the stored job no longer knows how far it had read.
