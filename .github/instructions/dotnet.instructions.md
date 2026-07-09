---
description: "Use when creating or changing .NET, C#, ASP.NET Core, project, build, test, or management API files. Covers architecture, trimming, concurrency, and validation rules."
name: "MockAPI .NET"
applyTo: "**/*.{cs,csproj,sln,props,targets}"
---

# .NET Instructions

- Target .NET 10 and keep nullable reference types and implicit usings enabled.
- Use ASP.NET Core minimal APIs for transport wiring; keep domain validation and state transitions out of route handlers.
- Represent the active endpoint registry as an immutable snapshot and publish replacements atomically.
- Keep request dispatch free of persistence and management concerns.
- Use cancellation tokens for asynchronous I/O and propagate them through application boundaries.
- Use UTC timestamps through `TimeProvider` where behavior depends on time.
- Use source-generated `System.Text.Json` contexts for application-owned request, response, and persistence types.
- Return problem details for management API errors and avoid exposing exceptions or filesystem details.
- Design concurrent statistics structures with bounded memory and test their rollover and contention behavior.
- Avoid reflection-heavy or dynamic features that undermine full trimming. Treat trim warnings as defects unless a narrowly documented suppression is necessary.
- Add unit tests for domain behavior and integration tests for HTTP/protocol behavior. Include a test proving runtime-created endpoints work without restart when changing dynamic routing.
- Format touched C# files and run the narrowest affected tests before the full solution test suite.
