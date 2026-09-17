# Function telemetry

Portal Sync has two Application Insights telemetry pipelines:

- The Azure Functions host emits invocation lifecycle traces, request records,
  exceptions, and runtime warnings.
- The .NET isolated worker sends application logs, custom telemetry, and scheduled
  job audit events directly to Application Insights from `Program.cs`.

The isolated-worker telemetry filters do not receive host-originated telemetry.
Host volume is controlled independently in `host.json` with deterministic category
filters:

```json
"logLevel": {
  "Function": "Warning",
  "Function.HealthCheck": "Warning",
  "Host.Results": "Error"
}
```

`Function = Warning` suppresses successful function start and completion traces,
which the Functions host emits at `Information`, while retaining warning, error,
and exception traces.

`Host.Results = Error` suppresses successful invocation request records while
retaining failed function execution request records. Host sampling remains
disabled, so failures are retained by severity rather than probabilistically.

The worker pipeline, durable audit events, processing behavior, retries, and
dead-letter behavior are unchanged. Scheduled-job alerts query worker
`customEvents`, so they do not depend on successful host request records.

References:

- [Configure Azure Functions monitoring categories and log levels](https://learn.microsoft.com/azure/azure-functions/configure-monitoring#configure-categories)
- [Azure Functions host.json reference](https://learn.microsoft.com/azure/azure-functions/functions-host-json#applicationinsights)
- [.NET isolated worker Application Insights](https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide#application-insights)
