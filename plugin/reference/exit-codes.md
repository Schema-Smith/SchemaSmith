# Exit codes

Every SchemaSmith tool ends with one of these codes. The full table is in the configuration reference: https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/reference/configuration.md#exit-codes

| Code | Meaning | What to do |
|---|---|---|
| `0` | Finished. Also what a tool returns when run with no settings at all: it prints the settings to fill in and runs nothing. | Nothing, but check the run actually had its settings: give them through a settings file, `--ConfigFile:<path>` or `SmithySettings_` environment variables. |
| `1` | Finished, but did not produce everything asked: a feature the target lacks was downgraded, or an object or table was left out. | The progress log names each one and why. |
| `2` | Failed for a reason you can fix: an argument or setting, a package that does not load, a refused target, a failed script or deployment. | Read the progress and error logs (use `explain-run`). |
| `3` | An unexpected error: a defect in the tool. | Report it with the logs. |
| `4` | The run's work finished, but its logs could not be archived. | Check the log folder's permissions and free space. |
| `5` | `--Drift` only: at least one target has drifted from its package. | Read the summary's `drift` section. |

With `--ExitNonZeroOnWarning`, a run that logged a warning returns `1` instead of `0`.
